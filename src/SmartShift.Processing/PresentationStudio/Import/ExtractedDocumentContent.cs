namespace SmartShift.Processing.PresentationStudio.Import;

public enum ContentBlockKind
{
    Paragraph,
    BulletItem,
    NumberedItem,
    Heading,
    CodeBlock,
    ImageRef
}

public sealed record ExtractedContentBlock(
    ContentBlockKind Kind,
    string Text,
    int? SourcePageNumber = null,
    int Level = 0);

public sealed record ExtractedContentSection(
    string? Heading,
    int Level,
    IReadOnlyList<ExtractedContentBlock> Blocks,
    int? SourcePageNumber = null);

public sealed record ExtractedDocumentContent(
    string SourceFilePath,
    string DocumentTitle,
    IReadOnlyList<ExtractedContentSection> Sections,
    int TotalCharacters,
    bool HasScannedContentOnly = false,
    string? WarningMessage = null);

