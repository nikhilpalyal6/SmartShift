using SkiaSharp;
using SmartShift.Core.Images;

namespace SmartShift.Processing.Images;

/// <summary>
/// Fast image metadata inspector that queries headers and codec info without full pixel decoding.
/// </summary>
public static class ImageMetadataReader
{
    public static ImageMetadataInfo Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        using var data = SKData.Create(filePath) ?? throw new InvalidOperationException("Could not open image file.");
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("File is not a supported or readable image.");

        var info = codec.Info;
        var origin = codec.EncodedOrigin;
        var (degrees, isFlipped) = OriginToRotation(origin);

        var hasExif = SkiaImagePipeline.ContainsExifOrGpsSignature(filePath);
        var hasGps = HasGpsMarker(filePath);
        var hasXmp = HasXmpMarker(filePath);

        return new ImageMetadataInfo(
            FilePath: filePath,
            FileName: fileInfo.Name,
            FileSizeBytes: fileInfo.Length,
            Format: codec.EncodedFormat.ToString(),
            Width: info.Width,
            Height: info.Height,
            ColorType: info.ColorType.ToString(),
            AlphaType: info.AlphaType.ToString(),
            ExifOrientation: origin.ToString(),
            RotationDegrees: degrees,
            HasExif: hasExif,
            HasGps: hasGps,
            HasXmp: hasXmp);
    }

    private static (int Degrees, bool IsFlipped) OriginToRotation(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => (0, false),
        SKEncodedOrigin.TopRight => (0, true),
        SKEncodedOrigin.BottomRight => (180, false),
        SKEncodedOrigin.BottomLeft => (180, true),
        SKEncodedOrigin.LeftTop => (90, true),
        SKEncodedOrigin.RightTop => (90, false),
        SKEncodedOrigin.RightBottom => (270, true),
        SKEncodedOrigin.LeftBottom => (270, false),
        _ => (0, false)
    };

    private static bool HasGpsMarker(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return StreamContainsAscii(stream, "GPSVersionID") || StreamContainsAscii(stream, "GPSLatitude");
        }
        catch
        {
            return false;
        }
    }

    private static bool HasXmpMarker(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return StreamContainsAscii(stream, "http://ns.adobe.com/xap") || StreamContainsAscii(stream, "<?xpacket");
        }
        catch
        {
            return false;
        }
    }

    private static bool StreamContainsAscii(Stream stream, string ascii)
    {
        stream.Position = 0;
        var needle = System.Text.Encoding.ASCII.GetBytes(ascii);
        var buffer = new byte[8192];
        int read;
        var needleIndex = 0;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == needle[needleIndex])
                {
                    needleIndex++;
                    if (needleIndex == needle.Length)
                    {
                        return true;
                    }
                }
                else
                {
                    if (needleIndex > 0)
                    {
                        needleIndex = buffer[i] == needle[0] ? 1 : 0;
                    }
                }
            }
        }

        return false;
    }
}
