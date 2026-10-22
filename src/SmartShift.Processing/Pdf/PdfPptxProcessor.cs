using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Pdf.Office;

namespace SmartShift.Processing.Pdf;

public sealed class PdfPptxProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfPptxProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator;
        _classifier = classifier;
    }

    public string ActionId => ActionIds.ConvertPdfToPptx;

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
            throw new InvalidOperationException("Only recognized PDF files can be converted to PowerPoint (PPTX).");
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

        request.Options.Values.TryGetValue("pageRange", out var pageRange);
        var slideModeStr = request.Options.Values.GetValueOrDefault("slideMode", "pageAsImage");
        var slideMode = string.Equals(slideModeStr, "editable", StringComparison.OrdinalIgnoreCase)
            ? PptxSlideMode.Editable
            : PptxSlideMode.PageAsImage;

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

        var result = PdfToPptxConverter.ConvertToPptx(
            request.Input.Path,
            _allocator,
            slideMode,
            pageRange,
            progress,
            cancellationToken);

        // Strict source immutability verification
        var afterHash = FileHasher.Sha256Hex(request.Input.Path);
        if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
        {
            try { File.Delete(result.OutputPath); } catch { }
            return ProcessingResult.Fail("The original file changed during processing. The operation was aborted.");
        }

        long outputBytes = new FileInfo(result.OutputPath).Length;

        var details = new Dictionary<string, string>
        {
            ["actionId"] = ActionIds.ConvertPdfToPptx,
            ["outputPath"] = result.OutputPath,
            ["originalBytes"] = inputLength.ToString(),
            ["originalSize"] = FileSizeFormatter.Format(inputLength),
            ["outputBytes"] = outputBytes.ToString(),
            ["outputSize"] = FileSizeFormatter.Format(outputBytes),
            ["outputFormat"] = "PPTX",
            ["slideMode"] = result.SlideMode == PptxSlideMode.PageAsImage ? "pageAsImage" : "editable",
            ["slidesCount"] = result.SlidesCreatedCount.ToString(),
            ["totalPages"] = result.TotalPagesInDoc.ToString(),
            ["summaryNote"] = result.SummaryNote,
            ["originalUnchanged"] = "true"
        };

        string statusMsg = $"Completed. {result.SummaryNote} Original file was not modified.";
        return ProcessingResult.Success(statusMsg, result.OutputPath, details);
    }
}
