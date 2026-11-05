namespace SmartShift.Processing.PresentationStudio.Models;

public enum SlideAspectRatio
{
    Widescreen16x9,
    Standard4x3
}

public sealed record SlideDimensions
{
    public SlideAspectRatio AspectRatio { get; init; } = SlideAspectRatio.Widescreen16x9;
    public long WidthEmu { get; init; } = 12192000L;  // 16:9 default in EMUs (13.33 inches)
    public long HeightEmu { get; init; } = 6858000L;  // 7.5 inches

    public double PixelWidth => AspectRatio == SlideAspectRatio.Widescreen16x9 ? 960.0 : 720.0;
    public double PixelHeight => 540.0;

    public static SlideDimensions Widescreen16x9 => new()
    {
        AspectRatio = SlideAspectRatio.Widescreen16x9,
        WidthEmu = 12192000L,
        HeightEmu = 6858000L
    };

    public static SlideDimensions Standard4x3 => new()
    {
        AspectRatio = SlideAspectRatio.Standard4x3,
        WidthEmu = 9144000L,
        HeightEmu = 6858000L
    };
}

