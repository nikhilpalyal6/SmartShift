namespace SmartShift.Core.Images;

/// <summary>
/// Represents inspected image metadata without requiring full pixel decoding or re-encoding.
/// </summary>
public sealed record ImageMetadataInfo(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string Format,
    int Width,
    int Height,
    string ColorType,
    string AlphaType,
    string ExifOrientation,
    int RotationDegrees,
    bool HasExif,
    bool HasGps,
    bool HasXmp)
{
    public string DimensionsText => $"{Width} × {Height} px";

    public string AspectRatioText
    {
        get
        {
            if (Width <= 0 || Height <= 0) return "Unknown";
            int gcd = GreatestCommonDivisor(Width, Height);
            int rw = Width / gcd;
            int rh = Height / gcd;
            return $"{rw}:{rh}";
        }
    }

    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>();
            if (HasExif) parts.Add("EXIF");
            if (HasGps) parts.Add("GPS Location");
            if (HasXmp) parts.Add("XMP");
            return parts.Count > 0 ? string.Join(", ", parts) : "None detected";
        }
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int t = b;
            b = a % b;
            a = t;
        }
        return Math.Max(1, a);
    }
}
