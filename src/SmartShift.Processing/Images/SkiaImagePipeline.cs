using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Images;

internal static class SkiaImagePipeline
{
    public sealed record DecodedImage(SKBitmap Bitmap, SKEncodedImageFormat SourceFormat, SKEncodedOrigin Origin) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    public static DecodedImage Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.Create(path) ?? throw new InvalidOperationException("The image could not be opened.");
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("The file is not a supported image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new InvalidOperationException("The image has invalid dimensions.");
        }

        if (ImageSafetyLimits.Exceeds(info.Width, info.Height))
        {
            throw new InvalidOperationException(
                $"The image is {info.Width}×{info.Height}, which exceeds the safety limit of {ImageSafetyLimits.MaxDimension}px per side or {ImageSafetyLimits.MaxPixelCount:N0} pixels.");
        }

        var format = codec.EncodedFormat;
        if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP can be processed.");
        }

        var imageInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(imageInfo);
        var pixels = codec.GetPixels(imageInfo, bitmap.GetPixels());
        if (pixels != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidOperationException("The image could not be decoded. It may be corrupt.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var oriented = ApplyOrigin(bitmap, codec.EncodedOrigin);
        return new DecodedImage(oriented, format, codec.EncodedOrigin);
    }

    public static SKBitmap Resize(SKBitmap source, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ImageSafetyLimits.Exceeds(width, height))
        {
            throw new InvalidOperationException("The requested size exceeds SmartShift safety limits.");
        }

        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);
        var resized = source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null)
        {
            throw new InvalidOperationException("The image could not be resized.");
        }

        return resized;
    }

    public static SKData Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality, out string settingNote)
    {
        quality = Math.Clamp(quality, 1, 100);
        var pixmap = bitmap.PeekPixels() ?? throw new InvalidOperationException("The image had no pixel data to encode.");
        SKData? data;
        switch (format)
        {
            case SKEncodedImageFormat.Jpeg:
                settingNote = $"JPEG quality {quality} (lossy). Transparency is not supported in JPEG.";
                data = pixmap.Encode(new SKJpegEncoderOptions(quality));
                break;
            case SKEncodedImageFormat.Png:
                var zlib = Math.Clamp((int)Math.Round(quality / 11.11d), 1, 9);
                settingNote = $"PNG is lossless. The quality slider maps to zlib compression level {zlib} (1–9), not JPEG-style loss.";
                data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, zlib));
                break;
            case SKEncodedImageFormat.Webp:
                if (quality >= 100)
                {
                    settingNote = "WebP lossless. Alpha is preserved when present.";
                    data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100));
                }
                else
                {
                    settingNote = $"WebP lossy quality {quality}. Alpha is preserved when present.";
                    data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality));
                }

                break;
            default:
                throw new InvalidOperationException("Unsupported encoder format.");
        }

        if (data is null || data.IsEmpty)
        {
            data?.Dispose();
            throw new InvalidOperationException("The encoder produced no output.");
        }

        return data;
    }

    public static void ValidateEncoded(string path, SKEncodedImageFormat expected, out int width, out int height)
    {
        using var data = SKData.Create(path) ?? throw new InvalidOperationException("The output file could not be reopened.");
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("The output file is not a valid image.");
        if (codec.EncodedFormat != expected)
        {
            throw new InvalidOperationException($"Expected {expected} output but decoded {codec.EncodedFormat}.");
        }

        width = codec.Info.Width;
        height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The output image has invalid dimensions.");
        }
    }

    public static SKEncodedImageFormat ToSkia(DetectedFormat format) => format switch
    {
        DetectedFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        DetectedFormat.Png => SKEncodedImageFormat.Png,
        DetectedFormat.Webp => SKEncodedImageFormat.Webp,
        _ => throw new InvalidOperationException("This file type cannot be processed as an image.")
    };

    public static string Extension(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ".jpg",
        SKEncodedImageFormat.Png => ".png",
        SKEncodedImageFormat.Webp => ".webp",
        _ => throw new InvalidOperationException("Unknown output format.")
    };

    public static DetectedFormat ToDetected(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => DetectedFormat.Jpeg,
        SKEncodedImageFormat.Png => DetectedFormat.Png,
        SKEncodedImageFormat.Webp => DetectedFormat.Webp,
        _ => DetectedFormat.Unknown
    };

    public static bool ContainsExifOrGpsSignature(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return StreamContainsAscii(stream, "Exif") ||
               StreamContainsAscii(stream, "GPS") ||
               StreamContainsAscii(stream, "http://ns.adobe.com/xap");
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

    internal static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return source;
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var dest = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(SKColors.Transparent);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(source.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(source.Width, source.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, source.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(0, 0);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(-90);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(source.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(source.Height, source.Width);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, source.Width);
                canvas.RotateDegrees(-90);
                break;
        }

        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        source.Dispose();
        return dest;
    }
}
