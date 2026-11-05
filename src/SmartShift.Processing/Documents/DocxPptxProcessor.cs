using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.PresentationStudio.Models;

namespace SmartShift.Processing.Documents;

public sealed class DocxPptxProcessor : IFileProcessor
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public DocxPptxProcessor(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public string ActionId => ActionIds.ConvertDocxToPptx;

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
                : $"{Path.GetFileNameWithoutExtension(request.Input.Path)}.pptx";

            if (!baseName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                baseName += ".pptx";
            }

            var themeKey = request.Options.Values.TryGetValue("theme", out var t) ? t : "DarkSlate";
            var theme = themeKey switch
            {
                "ClassicNavy" => PresentationTheme.ClassicNavy,
                "ModernMinimal" => PresentationTheme.ModernMinimal,
                _ => PresentationTheme.DarkSlate
            };

            var aspectKey = request.Options.Values.TryGetValue("aspectRatio", out var ar) ? ar : "16:9";
            var aspect = aspectKey == "4:3" ? SlideAspectRatio.Standard4x3 : SlideAspectRatio.Widescreen16x9;

            var titleSlide = !request.Options.Values.TryGetValue("generateTitleSlide", out var gts) ||
                             !string.Equals(gts, "false", StringComparison.OrdinalIgnoreCase);

            var options = new DocxPptxConversionOptions(
                Theme: theme,
                AspectRatio: aspect,
                GenerateTitleSlide: titleSlide);

            var finalPath = _allocator.Allocate(request.Input.Path, baseName);
            var generatedPath = await DocxToPptxConverter.ConvertAsync(
                request.Input.Path,
                finalPath,
                options,
                progress,
                cancellationToken).ConfigureAwait(false);

            return ProcessingResult.Success(
                "Converted Word document to PowerPoint presentation successfully.",
                generatedPath,
                new Dictionary<string, string> { ["OutputPath"] = generatedPath });
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail($"Word to PowerPoint conversion failed: {ex.Message}");
        }
    }
}
