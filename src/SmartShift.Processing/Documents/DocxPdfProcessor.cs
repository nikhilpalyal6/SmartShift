using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Documents;

public sealed class DocxPdfProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public DocxPdfProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.ConvertDocxToPdf;

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
                : $"{Path.GetFileNameWithoutExtension(request.Input.Path)}.pdf";

            if (!baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                baseName += ".pdf";
            }

            var preferWord = request.Options.Values.TryGetValue("preferWord", out var pw) &&
                             (string.Equals(pw, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(pw, "1", StringComparison.OrdinalIgnoreCase));

            var options = new DocxPdfConversionOptions(PreferLocalWordEngine: preferWord);
            var finalPath = _allocator.Allocate(request.Input.Path, baseName);

            var generatedPath = await Task.Run(
                () => DocxToPdfConverter.Convert(request.Input.Path, finalPath, options, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            return ProcessingResult.Success(
                "Converted Word document to PDF successfully.",
                generatedPath,
                new Dictionary<string, string> { ["OutputPath"] = generatedPath });
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail($"Word to PDF conversion failed: {ex.Message}");
        }
    }
}
