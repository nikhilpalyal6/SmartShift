namespace SmartShift.Core.IO;

public static class ImageSafetyLimits
{
    public const int MaxDimension = 8192;
    public const int MaxPixelCount = 40_000_000;

    public static bool Exceeds(int width, int height)
        => width <= 0
           || height <= 0
           || width > MaxDimension
           || height > MaxDimension
           || (long)width * height > MaxPixelCount;
}
