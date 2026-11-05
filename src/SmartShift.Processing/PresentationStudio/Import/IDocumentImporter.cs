namespace SmartShift.Processing.PresentationStudio.Import;

public interface IDocumentImporter
{
    IReadOnlyList<string> SupportedExtensions { get; }
    bool CanImport(string filePath);
    Task<ExtractedDocumentContent> ImportAsync(string filePath, CancellationToken cancellationToken);
}

