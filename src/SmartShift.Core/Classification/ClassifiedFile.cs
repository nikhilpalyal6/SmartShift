namespace SmartShift.Core.Classification;

public sealed record ClassifiedFile(
    string Path,
    string FileName,
    string Extension,
    long LengthBytes,
    DateTimeOffset? LastWriteTimeUtc,
    DetectedFormat Format,
    DetectedFormat ExtensionHint,
    ClassificationIssue Issue,
    string Status,
    int? PixelWidth,
    int? PixelHeight,
    string? PdfVersion,
    FileCategory? ExplicitCategory = null,
    FileMetadata? ExplicitMetadata = null)
{
    public bool Exists => Issue is not ClassificationIssue.Missing and not ClassificationIssue.Directory;

    public bool IsAccessible => Issue != ClassificationIssue.Inaccessible && Issue != ClassificationIssue.Missing && Issue != ClassificationIssue.Directory;

    /// <summary>
    /// True when SmartShift recognizes a Phase 1 category (PNG, JPEG, WebP, PDF).
    /// Preserved for backward compatibility with Phase 1 tests.
    /// </summary>
    public bool IsSupportedCategory => Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Pdf
        && Issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch;

    /// <summary>
    /// True when the file format is supported for workflows (editing, processing, or importing) in SmartShift.
    /// </summary>
    public bool IsSupported => Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Pdf
        or DetectedFormat.Docx or DetectedFormat.Txt or DetectedFormat.Markdown or DetectedFormat.Pptx
        && Issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch;

    /// <summary>
    /// True when the file format is recognized by SmartShift, even if editing or preview is not supported.
    /// </summary>
    public bool IsRecognized => Format is not DetectedFormat.Unknown and not DetectedFormat.Empty;

    public FileCategory Category => ExplicitCategory ?? Format switch
    {
        DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff => FileCategory.Image,
        DetectedFormat.Pdf or DetectedFormat.Docx or DetectedFormat.Txt or DetectedFormat.Markdown => FileCategory.Document,
        DetectedFormat.Pptx => FileCategory.Presentation,
        DetectedFormat.Xlsx => FileCategory.Spreadsheet,
        DetectedFormat.Zip => FileCategory.Archive,
        _ => FileCategory.Unknown
    };

    public FileMetadata Metadata => ExplicitMetadata ?? new FileMetadata(
        Path: Path,
        FileName: FileName,
        Extension: Extension,
        LengthBytes: LengthBytes,
        LastWriteTimeUtc: LastWriteTimeUtc,
        PixelWidth: PixelWidth,
        PixelHeight: PixelHeight,
        DocumentVersion: PdfVersion,
        PageCount: null);

    public string DisplayType => Format switch
    {
        DetectedFormat.Png => "PNG image",
        DetectedFormat.Jpeg => "JPEG image",
        DetectedFormat.Webp => "WebP image",
        DetectedFormat.Pdf => "PDF document",
        DetectedFormat.Gif => "GIF image",
        DetectedFormat.Bmp => "BMP image",
        DetectedFormat.Tiff => "TIFF image",
        DetectedFormat.Docx => "Word document (DOCX)",
        DetectedFormat.Pptx => "PowerPoint presentation (PPTX)",
        DetectedFormat.Xlsx => "Excel spreadsheet (XLSX)",
        DetectedFormat.Txt => "Plain text document",
        DetectedFormat.Markdown => "Markdown document",
        DetectedFormat.Zip => "ZIP archive",
        DetectedFormat.Empty => "Empty file",
        _ => "Unknown"
    };
}
