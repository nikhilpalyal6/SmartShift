using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Documents;

public sealed class DocxCompressProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public DocxCompressProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.CompressDocx;

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Input.Path) || !File.Exists(request.Input.Path))
        {
            return ProcessingResult.Fail("The source Word document does not exist or has been moved.");
        }

        try
        {
            _allocator.EnsureRoot();
            var baseName = request.Options.Values.TryGetValue("outputFileName", out var customName) && !string.IsNullOrWhiteSpace(customName)
                ? customName.Trim()
                : $"{Path.GetFileNameWithoutExtension(request.Input.Path)}_compressed.docx";

            if (!baseName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                baseName += ".docx";
            }

            var finalPath = _allocator.Allocate(request.Input.Path, baseName);

            var report = await Task.Run(
                () => DocxCompressor.Compress(request.Input.Path, finalPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            return ProcessingResult.Success(
                report.SummaryMessage,
                report.OutputPath,
                new Dictionary<string, string>
                {
                    ["OriginalBytes"] = report.OriginalSizeBytes.ToString(),
                    ["OutputBytes"] = report.OutputSizeBytes.ToString(),
                    ["BytesSaved"] = report.BytesSaved.ToString(),
                    ["PercentChange"] = report.PercentChange.ToString("0.1")
                });
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail($"Word document compression failed: {ex.Message}");
        }
    }
}
