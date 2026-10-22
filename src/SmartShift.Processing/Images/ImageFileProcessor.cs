using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Images;

public sealed class ImageFileProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public ImageFileProcessor(string actionId, IOutputPathAllocator allocator, FileClassifier classifier)
    {
        ActionId = actionId;
        _allocator = allocator;
        _classifier = classifier;
    }

    public string ActionId { get; }

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
            return ProcessingResult.Fail(UserMessage(ex), [UserMessage(ex)]);
        }
    }

    private ProcessingResult Process(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Input.Format is not (DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
            || !request.Input.IsSupportedCategory)
        {
            throw new InvalidOperationException("Only recognized JPEG, PNG, and WebP files can be processed.");
        }

        var originalHash = FileHasher.Sha256Hex(request.Input.Path);
        progress?.Report(new ProcessingProgress(8, "Reading image…"));

        using var decoded = SkiaImagePipeline.Decode(request.Input.Path, cancellationToken);
        var working = decoded.Bitmap;
        SKBitmap? resized = null;
        string? tempPath = null;
        string? finalPath = null;

        try
        {
            var quality = ParseQuality(request);
            var outputFormat = ActionId switch
            {
                ActionIds.ConvertToWebp => SKEncodedImageFormat.Webp,
                ActionIds.ConvertToPng => SKEncodedImageFormat.Png,
                ActionIds.ConvertToJpeg => SKEncodedImageFormat.Jpeg,
                _ => decoded.SourceFormat
            };
            var suffix = ActionId switch
            {
                ActionIds.CompressImage => "-compressed",
                ActionIds.ConvertToWebp => string.Equals(Path.GetExtension(request.Input.FileName), ".webp", StringComparison.OrdinalIgnoreCase) ? "-converted" : "",
                ActionIds.ConvertToPng => string.Equals(Path.GetExtension(request.Input.FileName), ".png", StringComparison.OrdinalIgnoreCase) ? "-converted" : "",
                ActionIds.ConvertToJpeg => (string.Equals(Path.GetExtension(request.Input.FileName), ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(request.Input.FileName), ".jpeg", StringComparison.OrdinalIgnoreCase)) ? "-converted" : "",
                ActionIds.ResizeImage => "-resized",
                ActionIds.RemoveImageMetadata => "-stripped",
                ActionIds.EditImage => "-edited",
                _ => "-processed"
            };

            if (ActionId == ActionIds.ResizeImage)
            {
                progress?.Report(new ProcessingProgress(35, "Resizing…"));
                var (width, height) = ResolveResize(request, working.Width, working.Height);
                resized = SkiaImagePipeline.Resize(working, width, height, cancellationToken);
                working = resized;
            }

            progress?.Report(new ProcessingProgress(60, "Encoding…"));
            using var encoded = SkiaImagePipeline.Encode(working, outputFormat, quality, out var settingNote);
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileNameWithoutExtension(request.Input.FileName) + suffix + SkiaImagePipeline.Extension(outputFormat);
            _allocator.EnsureRoot();
            if (!string.Equals(Path.GetFullPath(_allocator.RootDirectory), Path.GetFullPath(request.OutputDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return ProcessingResult.Fail("The output directory does not match the SmartShift output policy.");
            }

            finalPath = _allocator.Allocate(request.Input.Path, fileName);
            tempPath = finalPath + ".partial";
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoded.SaveTo(output);
                output.Flush(true);
            }

            progress?.Report(new ProcessingProgress(85, "Validating output…"));
            cancellationToken.ThrowIfCancellationRequested();
            SkiaImagePipeline.ValidateEncoded(tempPath, outputFormat, out var outWidth, out var outHeight);
            var classified = _classifier.Classify(tempPath);
            if (classified.Format != SkiaImagePipeline.ToDetected(outputFormat) || !classified.IsSupportedCategory)
            {
                throw new InvalidOperationException("Output validation failed: the encoded file was not the expected format.");
            }

            File.Move(tempPath, finalPath);
            tempPath = null;

            if (!File.Exists(finalPath))
            {
                throw new InvalidOperationException("The output file was not created.");
            }

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
                ["outputBytes"] = outputLength.ToString(),
                ["outputSize"] = FileSizeFormatter.Format(outputLength),
                ["outputDimensions"] = $"{outWidth} × {outHeight}",
                ["outputFormat"] = outputFormat.ToString(),
                ["encoder"] = settingNote,
                ["originalUnchanged"] = "true"
            };

            if (ActionId == ActionIds.RemoveImageMetadata)
            {
                details["metadata"] = SkiaImagePipeline.ContainsExifOrGpsSignature(finalPath)
                    ? "Re-encoded without copying source EXIF/GPS/XMP chunks. Residual encoder tags may remain; this is not a claim of total metadata erasure."
                    : "Source EXIF, GPS, and XMP signatures were not found in the output. Orientation was baked into pixels.";
            }

            if (ActionId == ActionIds.CompressImage && outputFormat == SKEncodedImageFormat.Png)
            {
                details["sizeNote"] = "PNG recompression is lossless and may not reduce size.";
            }

            progress?.Report(new ProcessingProgress(100, "Done."));
            return ProcessingResult.Success(
                $"Wrote {Path.GetFileName(finalPath)} ({FileSizeFormatter.Format(outputLength)}, {outWidth}×{outHeight}). The original file was not modified.",
                finalPath,
                details);
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
        finally
        {
            resized?.Dispose();
        }
    }

    private static int ParseQuality(ProcessingRequest request)
    {
        if (request.ActionId is ActionIds.CompressImage or ActionIds.ConvertToWebp or ActionIds.ConvertToJpeg)
        {
            var errors = ActionOptionValidator.Validate(request.ActionId, request.Options.Values);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", errors));
            }

            return int.Parse(request.Options["quality"]!);
        }

        if (request.ActionId == ActionIds.ConvertToPng && request.Options["quality"] is string qStr && int.TryParse(qStr, out var q))
        {
            return q;
        }

        return 80;
    }

    private static (int Width, int Height) ResolveResize(ProcessingRequest request, int sourceWidth, int sourceHeight)
    {
        var errors = ActionOptionValidator.Validate(ActionIds.ResizeImage, request.Options.Values);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        var width = int.Parse(request.Options["width"]!);
        var keepAspect = request.Options["keepAspect"] is string raw && bool.TryParse(raw, out var flag) && flag;
        int height;
        if (keepAspect)
        {
            height = Math.Max(1, (int)Math.Round(width * (sourceHeight / (double)sourceWidth)));
        }
        else
        {
            height = int.Parse(request.Options["height"]!);
        }

        if (ImageSafetyLimits.Exceeds(width, height))
        {
            throw new InvalidOperationException("The requested size exceeds SmartShift safety limits.");
        }

        return (width, height);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of files we created.
        }
    }

    private static string UserMessage(Exception ex)
        => ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            ? ex.Message
            : "The image could not be processed. The original file was not modified.";
}
