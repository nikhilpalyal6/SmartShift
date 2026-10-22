namespace SmartShift.Core.Drops;

/// <summary>
/// Extension-based classification only. Phase 0 does not inspect or convert file contents.
/// </summary>
public static class FileKindClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };

    public static SupportedFileKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedFileKind.Pdf;
        }

        return ImageExtensions.Contains(extension)
            ? SupportedFileKind.Image
            : SupportedFileKind.Unsupported;
    }

    public static bool IsSupported(string path) => Classify(path) != SupportedFileKind.Unsupported;
}
