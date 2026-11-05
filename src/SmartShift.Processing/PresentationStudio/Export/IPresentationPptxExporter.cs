using SmartShift.Processing.PresentationStudio.Models;

namespace SmartShift.Processing.PresentationStudio.Export;

public interface IPresentationPptxExporter
{
    Task<string> ExportAsync(
        PresentationDeck presentation,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);
}

