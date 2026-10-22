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
    string? PdfVersion)
{
    public bool Exists => Issue is not ClassificationIssue.Missing and not ClassificationIssue.Directory;

    public bool IsAccessible => Issue != ClassificationIssue.Inaccessible && Issue != ClassificationIssue.Missing && Issue != ClassificationIssue.Directory;

    /// <summary>
    /// True when SmartShift recognizes a Phase 1 category (PNG, JPEG, WebP, PDF), even if engines are not implemented.
    /// </summary>
    public bool IsSupportedCategory => Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Pdf
        && Issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch;

    public string DisplayType => Format switch
    {
        DetectedFormat.Png => "PNG image",
        DetectedFormat.Jpeg => "JPEG image",
        DetectedFormat.Webp => "WebP image",
        DetectedFormat.Pdf => "PDF document",
        DetectedFormat.Gif => "GIF image",
        DetectedFormat.Bmp => "BMP image",
        DetectedFormat.Tiff => "TIFF image",
        DetectedFormat.Empty => "Empty file",
        _ => "Unknown"
    };
}
