using System.IO;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfOrganizeProcessor : IFileProcessor
{
    private readonly IPdfPageOrganizerService _organizeService;
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public PdfOrganizeProcessor(
        IPdfPageOrganizerService organizeService,
        IOutputPathAllocator allocator,
        FileClassifier classifier)
    {
        _organizeService = organizeService ?? throw new ArgumentNullException(nameof(organizeService));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.OrganizePdf;

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

        var validation = await _organizeService.ValidateSourceAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ProcessingResult.Fail(validation.ErrorReason ?? "The source PDF document is invalid.");
        }

        var transforms = new List<PdfPageTransform>();

        // Check if explicit transform specs were provided in options (e.g. "order" = "1,3:90,2:180")
        if (request.Options.Values.TryGetValue("order", out var orderStr) && !string.IsNullOrWhiteSpace(orderStr))
        {
            var parts = orderStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                // Each item can be "PageNumber" or "PageNumber:Rotation" (1-based page number)
                var subParts = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!int.TryParse(subParts[0], out var pageNum) || pageNum < 1 || pageNum > validation.TotalPages)
                {
                    return ProcessingResult.Fail($"Invalid page number '{subParts[0]}' in organize configuration.");
                }

                int rotation = 0;
                if (subParts.Length > 1 && (!int.TryParse(subParts[1], out rotation) || rotation % 90 != 0))
                {
                    return ProcessingResult.Fail($"Invalid rotation angle '{subParts[1]}' for page {pageNum}.");
                }

                transforms.Add(new PdfPageTransform(pageNum - 1, rotation));
            }
        }
        else
        {
            // Default to all pages in natural order with 0 rotation
            for (int i = 0; i < validation.TotalPages; i++)
            {
                transforms.Add(new PdfPageTransform(i, 0));
            }
        }

        var outDir = request.Options.Values.TryGetValue("outputDir", out var dir) && !string.IsNullOrWhiteSpace(dir)
            ? dir.Trim()
            : _allocator.RootDirectory;

        string? customName = request.Options.Values.TryGetValue("outputFileName", out var fn) && !string.IsNullOrWhiteSpace(fn)
            ? fn.Trim()
            : null;

        var organizeRequest = new PdfOrganizeRequest(
            request.Input.Path,
            outDir,
            transforms,
            customName);

        var result = await _organizeService.OrganizeAsync(organizeRequest, progress, cancellationToken).ConfigureAwait(false);

        if (result.WasCancelled)
        {
            return ProcessingResult.Cancelled();
        }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath))
        {
            return ProcessingResult.Fail(result.ErrorReason ?? "PDF organization failed.");
        }

        return ProcessingResult.Success(
            status: result.SummaryMessage,
            outputPath: result.OutputPath,
            details: new Dictionary<string, string>
            {
                ["OutputPages"] = result.OutputPageCount.ToString(),
                ["FileSizeBytes"] = result.FileSizeBytes.ToString(),
                ["OutputPath"] = result.OutputPath
            });
    }
}
