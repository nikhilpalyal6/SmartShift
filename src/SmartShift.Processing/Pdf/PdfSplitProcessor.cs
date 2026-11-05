using System.IO;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfSplitProcessor : IFileProcessor
{
    private readonly IPdfSplitService _splitService;
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfSplitProcessor(IPdfSplitService splitService, IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _splitService = splitService ?? throw new ArgumentNullException(nameof(splitService));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.SplitPdf;

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Input.Path) || !File.Exists(request.Input.Path))
        {
            return ProcessingResult.Fail("Source PDF file does not exist or has been removed.");
        }

        var classified = _classifier.Classify(request.Input.Path);
        if (classified.Format != DetectedFormat.Pdf || !classified.IsRecognized)
        {
            return ProcessingResult.Fail("The selected file is not a valid recognized PDF document.");
        }

        var validation = await _splitService.ValidateSourceAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ProcessingResult.Fail(validation.ErrorReason ?? "The source PDF document is invalid.");
        }

        var mode = PdfSplitMode.ExtractRanges;
        if (request.Options.Values.TryGetValue("mode", out var modeStr) &&
            string.Equals(modeStr, "splitEvery", StringComparison.OrdinalIgnoreCase))
        {
            mode = PdfSplitMode.SplitEveryPage;
        }

        IReadOnlyList<PdfPageRangeItem>? ranges = null;
        if (mode == PdfSplitMode.ExtractRanges)
        {
            var rangeStr = request.Options.Values.TryGetValue("range", out var r) ? r : "1";
            if (!PdfPageRangeParser.TryParseRanges(rangeStr, validation.TotalPages, out ranges, out var parseError))
            {
                return ProcessingResult.Fail(parseError ?? "Invalid page range expression.");
            }
        }

        var outDir = request.Options.Values.TryGetValue("outputDir", out var dir) && !string.IsNullOrWhiteSpace(dir)
            ? dir.Trim()
            : _allocator.RootDirectory;

        var splitRequest = new PdfSplitRequest(
            request.Input.Path,
            outDir,
            mode,
            ranges);

        var splitResult = await _splitService.SplitAsync(splitRequest, progress, cancellationToken).ConfigureAwait(false);

        if (splitResult.WasCancelled)
        {
            return ProcessingResult.Cancelled();
        }

        if (!splitResult.Succeeded)
        {
            return ProcessingResult.Fail(splitResult.ErrorReason ?? "PDF split failed.");
        }

        var firstOutput = splitResult.GeneratedFiles.Count > 0 ? splitResult.GeneratedFiles[0].OutputPath : string.Empty;

        return ProcessingResult.Success(
            outputPath: firstOutput,
            status: splitResult.SummaryMessage,
            details: new Dictionary<string, string>
            {
                ["FilesGenerated"] = splitResult.GeneratedFiles.Count.ToString(),
                ["TotalPages"] = splitResult.TotalSourcePages.ToString(),
                ["OutputDirectory"] = splitResult.OutputDirectory
            });
    }
}
