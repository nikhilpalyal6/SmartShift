using SmartShift.Core.IO;
using SmartShift.Processing.PresentationStudio.Export;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;

namespace SmartShift.Processing.PresentationStudio.Service;

public interface IPresentationStudioService
{
    DocumentImporterCatalog Importers { get; }
    IOutlineGenerator OutlineGenerator { get; }
    IOptionalAiOutlineService AiService { get; }
    IPresentationPptxExporter Exporter { get; }

    Task<ExtractedDocumentContent> ImportDocumentAsync(string filePath, CancellationToken cancellationToken);
    PresentationOutline GenerateOutline(ExtractedDocumentContent content, OutlineGenerationOptions options);
    Task<string> ExportPresentationAsync(
        PresentationDeck presentation,
        string? customOutputName,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class PresentationStudioService : IPresentationStudioService
{
    private readonly IOutputPathAllocator _allocator;

    public PresentationStudioService(
        IOutputPathAllocator allocator,
        DocumentImporterCatalog? importers = null,
        IOutlineGenerator? outlineGenerator = null,
        IOptionalAiOutlineService? aiService = null,
        IPresentationPptxExporter? exporter = null)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        Importers = importers ?? new DocumentImporterCatalog();
        OutlineGenerator = outlineGenerator ?? new DeterministicOutlineGenerator();
        AiService = aiService ?? new LocalAiOutlineService();
        Exporter = exporter ?? new PresentationPptxExporter();
    }

    public DocumentImporterCatalog Importers { get; }
    public IOutlineGenerator OutlineGenerator { get; }
    public IOptionalAiOutlineService AiService { get; }
    public IPresentationPptxExporter Exporter { get; }

    public async Task<ExtractedDocumentContent> ImportDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        return await Importers.ImportDocumentAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    public PresentationOutline GenerateOutline(ExtractedDocumentContent content, OutlineGenerationOptions options)
    {
        return OutlineGenerator.GenerateOutline(content, options);
    }

    public async Task<string> ExportPresentationAsync(
        PresentationDeck presentation,
        string? customOutputName,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        _allocator.EnsureRoot();
        var baseName = !string.IsNullOrWhiteSpace(customOutputName)
            ? customOutputName.Trim()
            : (!string.IsNullOrWhiteSpace(presentation.Title) ? presentation.Title : "Presentation");

        if (!baseName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            baseName += ".pptx";
        }

        // Clean file name
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalid, '_');
        }

        string sourceRef = !string.IsNullOrWhiteSpace(presentation.SourceFilePath)
            ? presentation.SourceFilePath
            : Path.Combine(Path.GetTempPath(), "smartshift_virtual_source", baseName);
        var finalPath = _allocator.Allocate(sourceRef, baseName);

        return await Exporter.ExportAsync(presentation, finalPath, progress, cancellationToken).ConfigureAwait(false);
    }
}
