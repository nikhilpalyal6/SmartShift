using System.Globalization;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Pdf;

public sealed class PdfExportProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfExportProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator;
        _classifier = classifier;
    }

    public string ActionId => ActionIds.ExportPdfPages;

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
            throw new InvalidOperationException("Only recognized PDF files can be exported.");
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

        var format = ExportImageFormat.Png;
        if (request.Options.Values.TryGetValue("format", out var rawFmt) &&
            string.Equals(rawFmt.Trim(), "jpeg", StringComparison.OrdinalIgnoreCase))
        {
            format = ExportImageFormat.Jpeg;
        }

        int dpi = 150;
        if (request.Options.Values.TryGetValue("dpi", out var rawDpi) &&
            int.TryParse(rawDpi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDpi))
        {
            dpi = parsedDpi;
        }

        int quality = 85;
        if (request.Options.Values.TryGetValue("quality", out var rawQ) &&
            int.TryParse(rawQ, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQ))
        {
            quality = parsedQ;
        }

        request.Options.Values.TryGetValue("pageRange", out var pageRange);

        var originalHash = FileHasher.Sha256Hex(request.Input.Path);
        var inputLength = new FileInfo(request.Input.Path).Length;
        if (inputLength == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        _allocator.EnsureRoot();
        if (!string.Equals(
            Path.GetFullPath(_allocator.RootDirectory),
            Path.GetFullPath(request.OutputDirectory),
            StringComparison.OrdinalIgnoreCase))
        {
            return ProcessingResult.Fail("The output directory does not match the SmartShift output policy.");
        }

        var result = PdfPageExporter.ExportPages(
            request.Input.Path,
            _allocator,
            format,
            dpi,
            quality,
            pageRange,
            progress,
            cancellationToken);

        // Strict source immutability verification
        var afterHash = FileHasher.Sha256Hex(request.Input.Path);
        if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
        {
            foreach (var f in result.ExportedFiles)
            {
                try { File.Delete(f); } catch { }
            }
            return ProcessingResult.Fail("The original file changed during processing. The operation was aborted.");
        }

        var firstOutput = result.ExportedFiles.Count > 0 ? result.ExportedFiles[0] : null;
        var fmtName = format == ExportImageFormat.Jpeg ? "JPEG" : "PNG";

        var details = new Dictionary<string, string>
        {
            ["actionId"] = ActionIds.ExportPdfPages,
            ["originalBytes"] = inputLength.ToString(),
            ["originalSize"] = FileSizeFormatter.Format(inputLength),
            ["outputBytes"] = result.TotalBytesWritten.ToString(),
            ["outputSize"] = FileSizeFormatter.Format(result.TotalBytesWritten),
            ["outputFormat"] = fmtName,
            ["dpi"] = dpi.ToString(),
            ["exportedPages"] = result.ExportedCount.ToString(),
            ["totalPages"] = result.TotalPagesInDoc.ToString(),
            ["summaryNote"] = result.SummaryNote,
            ["originalUnchanged"] = "true"
        };

        if (firstOutput is not null)
        {
            details["outputPath"] = firstOutput;
        }

        string statusMsg = $"Completed. Exported {result.ExportedCount} page(s) as {fmtName} ({dpi} DPI). Original file was not modified.";
        return ProcessingResult.Success(statusMsg, firstOutput ?? request.OutputDirectory, details);
    }
}
