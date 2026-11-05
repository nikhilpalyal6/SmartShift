using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfMergeProcessor : IFileProcessor
{
    private readonly IPdfMergeService _mergeService;
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfMergeProcessor(IPdfMergeService mergeService, IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _mergeService = mergeService ?? throw new ArgumentNullException(nameof(mergeService));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.MergePdf;

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Support passing multiple files via options or source
        var files = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Input.Path))
        {
            files.Add(request.Input.Path);
        }

        if ((request.Options.Values.TryGetValue("additionalFiles", out var additional) ||
             request.Options.Values.TryGetValue("additionalPaths", out additional)) &&
            !string.IsNullOrWhiteSpace(additional))
        {
            var split = additional.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            files.AddRange(split);
        }

        if (files.Count < 2)
        {
            return ProcessingResult.Fail("At least 2 PDF files are required to merge.");
        }

        try
        {
            _allocator.EnsureRoot();
            var baseName = (request.Options.Values.TryGetValue("outputFileName", out var customName) ||
                            request.Options.Values.TryGetValue("customOutputName", out customName)) &&
                           !string.IsNullOrWhiteSpace(customName)
                ? customName.Trim()
                : $"{Path.GetFileNameWithoutExtension(request.Input.Path)}_merged.pdf";

            if (!baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                baseName += ".pdf";
            }

            var finalPath = _allocator.Allocate(request.Input.Path, baseName);
            var merged = await _mergeService.MergeAsync(files, finalPath, progress, cancellationToken).ConfigureAwait(false);

            int totalPages = 0;
            try
            {
                using var readDoc = PdfSharp.Pdf.IO.PdfReader.Open(merged, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                totalPages = readDoc.PageCount;
            }
            catch { }

            return ProcessingResult.Success(
                $"Successfully merged {files.Count} PDF documents into {totalPages} pages.",
                merged,
                new Dictionary<string, string>
                {
                    ["OutputPath"] = merged,
                    ["TotalFiles"] = files.Count.ToString(),
                    ["MergedPageCount"] = totalPages.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail(ex.Message);
        }
    }
}
