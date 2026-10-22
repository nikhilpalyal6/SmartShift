using System.Globalization;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Pdf;

public sealed class PdfFileProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfFileProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator;
        _classifier = classifier;
    }

    public string ActionId => ActionIds.CompressPdf;

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessingResult.Cancelled();
        }

        try
        {
            return await Task.Run(() => Process(request, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail(ex.Message, [ex.Message]);
        }
    }

    private ProcessingResult Process(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Input.Format != DetectedFormat.Pdf || !request.Input.IsSupportedCategory)
        {
            throw new InvalidOperationException("Only recognized PDF files can be processed.");
        }

        if (!File.Exists(request.Input.Path))
        {
            throw new FileNotFoundException("The original PDF file is no longer available.", request.Input.Path);
        }

        var validationErrors = ActionOptionValidator.Validate(request.ActionId, request.Options.Values);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", validationErrors));
        }

        long? targetSizeBytes = null;
        if (request.Options.Values.TryGetValue("targetBytes", out var rawBytes) && long.TryParse(rawBytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBytes) && parsedBytes > 0)
        {
            targetSizeBytes = parsedBytes;
        }
        else if (request.Options.Values.TryGetValue("targetSize", out var rawSize) && double.TryParse(rawSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0)
        {
            var unit = request.Options.Values.TryGetValue("targetUnit", out var u) ? u.Trim().ToUpperInvariant() : "MB";
            targetSizeBytes = unit == "KB"
                ? (long)Math.Round(size * 1024.0)
                : (long)Math.Round(size * 1024.0 * 1024.0);
        }

        var imageQuality = request.Options.Values.TryGetValue("imageQuality", out var rawQuality) && !string.IsNullOrWhiteSpace(rawQuality)
            ? rawQuality.Trim().ToLowerInvariant()
            : "balanced";

        var originalHash = FileHasher.Sha256Hex(request.Input.Path);
        var inputLength = new FileInfo(request.Input.Path).Length;
        if (inputLength == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        string? tempPath = null;
        string? finalPath = null;

        try
        {
            _allocator.EnsureRoot();
            if (!string.Equals(
                Path.GetFullPath(_allocator.RootDirectory),
                Path.GetFullPath(request.OutputDirectory),
                StringComparison.OrdinalIgnoreCase))
            {
                return ProcessingResult.Fail("The output directory does not match the SmartShift output policy.");
            }

            var fileName = Path.GetFileNameWithoutExtension(request.Input.FileName) + "-compressed.pdf";
            finalPath = _allocator.Allocate(request.Input.Path, fileName);
            tempPath = finalPath + ".partial";

            // Run compression into temporary path
            var compressionResult = PdfPipeline.Compress(request.Input.Path, tempPath, targetSizeBytes, imageQuality, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Validate that classifier identifies the output as a valid PDF
            var classified = _classifier.Classify(tempPath);
            if (classified.Format != DetectedFormat.Pdf || !classified.IsSupportedCategory)
            {
                throw new InvalidOperationException("Output validation failed: the generated file was not recognized as a valid PDF.");
            }

            // Atomically promote temporary file
            File.Move(tempPath, finalPath);
            tempPath = null;

            if (!File.Exists(finalPath))
            {
                throw new InvalidOperationException("The output file was not created.");
            }

            // Strict source file immutability verification
            var afterHash = FileHasher.Sha256Hex(request.Input.Path);
            if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
            {
                TryDelete(finalPath);
                return ProcessingResult.Fail("The original file changed during processing. The operation was aborted.");
            }

            var outputLength = new FileInfo(finalPath).Length;
            var details = new Dictionary<string, string>
            {
                ["outputPath"] = finalPath,
                ["originalBytes"] = inputLength.ToString(),
                ["originalSize"] = FileSizeFormatter.Format(inputLength),
                ["outputBytes"] = outputLength.ToString(),
                ["outputSize"] = FileSizeFormatter.Format(outputLength),
                ["outputFormat"] = "PDF",
                ["encoder"] = compressionResult.ImagesOptimizedCount > 0
                    ? $"PDFsharp Stream Compression & SkiaSharp Image Optimization ({compressionResult.ImagesOptimizedCount} images, {imageQuality})"
                    : "PDFsharp Flate/Deflate Stream Compression & Structural Cleanup",
                ["stage"] = compressionResult.ProcessingStage,
                ["originalUnchanged"] = "true",
                ["imagesDiscovered"] = compressionResult.TotalImagesDiscovered.ToString(),
                ["imagesEligible"] = compressionResult.ImagesEligibleCount.ToString(),
                ["imagesOptimizedCount"] = compressionResult.ImagesOptimizedCount.ToString(),
                ["limitsExplanation"] = compressionResult.LimitsExplanation ?? compressionResult.TargetStatusNote
            };

            if (compressionResult.ImagesOptimizedCount > 0)
            {
                details["imagesOptimized"] = compressionResult.ImagesOptimizedCount.ToString();
            }

            if (compressionResult.SkippedReasons is not null && compressionResult.SkippedReasons.Count > 0)
            {
                details["skippedReasons"] = string.Join("; ", compressionResult.SkippedReasons.Select(kv => $"{kv.Key}: {kv.Value}"));
            }

            if (targetSizeBytes.HasValue)
            {
                details["targetSizeBytes"] = targetSizeBytes.Value.ToString();
                details["targetSize"] = FileSizeFormatter.Format(targetSizeBytes.Value);
                details["targetReached"] = compressionResult.TargetReached ? "true" : "false";
                details["targetStatus"] = compressionResult.TargetStatusNote;
            }

            if (compressionResult.PageCount > 0 && compressionResult.PageDimensions.Count > 0)
            {
                var first = compressionResult.PageDimensions[0];
                details["outputDimensions"] = $"{(int)Math.Round(first.Width)} × {(int)Math.Round(first.Height)} pt ({compressionResult.PageCount} {(compressionResult.PageCount == 1 ? "page" : "pages")})";
            }
            else
            {
                details["outputDimensions"] = $"{compressionResult.PageCount} pages";
            }

            if (compressionResult.HasSignatures)
            {
                details["signatureNote"] = "Digital signature detected in source document. Note that rewriting invalidates existing cryptographic signatures.";
            }

            // Honest size reporting
            double pctSaved = 0.0;
            double pctIncrease = 0.0;
            if (outputLength > inputLength)
            {
                var increaseBytes = outputLength - inputLength;
                pctIncrease = (increaseBytes / (double)inputLength) * 100.0;
                details["isIncrease"] = "true";
                details["deltaBytes"] = increaseBytes.ToString();
                details["sizeNote"] = $"Output grew (+{pctIncrease.ToString("F1", CultureInfo.InvariantCulture)}%). This document was already compressed or contains pre-deflated content that did not benefit from re-encoding.";
            }
            else
            {
                var savedBytes = inputLength - outputLength;
                pctSaved = (savedBytes / (double)inputLength) * 100.0;
                details["isReduction"] = (savedBytes > 0) ? "true" : "false";
                details["deltaBytes"] = (-savedBytes).ToString();
                details["sizeNote"] = savedBytes > 0
                    ? $"Saved {FileSizeFormatter.Format(savedBytes)} ({pctSaved.ToString("F1", CultureInfo.InvariantCulture)}% reduction)."
                    : "Output size is identical to original.";
            }

            progress?.Report(new ProcessingProgress(100, "Done."));

            string statusMsg;
            if (targetSizeBytes.HasValue)
            {
                var targetText = FileSizeFormatter.Format(targetSizeBytes.Value);
                if (compressionResult.TargetReached)
                {
                    statusMsg = $"Completed — Target reached. Compressed {Path.GetFileName(finalPath)} to {FileSizeFormatter.Format(outputLength)} ({pctSaved.ToString("F1", CultureInfo.InvariantCulture)}% reduction, target {targetText} met). Original file was not modified.";
                }
                else
                {
                    statusMsg = outputLength < inputLength
                        ? $"Completed — Target not reached. Compressed {Path.GetFileName(finalPath)} to {FileSizeFormatter.Format(outputLength)} ({pctSaved.ToString("F1", CultureInfo.InvariantCulture)}% reduction, target {targetText}). Original file was not modified."
                        : $"Completed — Target not reached. Output size is {FileSizeFormatter.Format(outputLength)} (target {targetText}). File did not reduce. Original file was not modified.";
                }
            }
            else
            {
                statusMsg = outputLength < inputLength
                    ? $"Completed. Compressed {Path.GetFileName(finalPath)} to {FileSizeFormatter.Format(outputLength)} ({pctSaved.ToString("F1", CultureInfo.InvariantCulture)}% reduction). Original file was not modified."
                    : $"Completed. Wrote {Path.GetFileName(finalPath)} ({FileSizeFormatter.Format(outputLength)}, {compressionResult.PageCount} pages). File did not reduce. Original file was not modified.";
            }

            return ProcessingResult.Success(statusMsg, finalPath, details);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            TryDelete(finalPath);
            throw;
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(finalPath);
            throw;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors on failure paths
        }
    }
}
