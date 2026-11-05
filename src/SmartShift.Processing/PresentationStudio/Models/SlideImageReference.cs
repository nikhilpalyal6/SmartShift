namespace SmartShift.Processing.PresentationStudio.Models;

public sealed record SlideImageReference
{
    public string ImagePath { get; init; } = string.Empty;
    public int NaturalWidth { get; init; } = 1920;
    public int NaturalHeight { get; init; } = 1080;
    public double AspectRatio => NaturalHeight > 0 ? (double)NaturalWidth / NaturalHeight : 1.0;
    public string? AltText { get; init; }

    public static bool IsSupportedFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp";
    }
}
