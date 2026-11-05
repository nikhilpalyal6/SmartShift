using SkiaSharp;

namespace SmartShift.Processing.Images;

public enum JpegSubsampling
{
    Standard420,
    HighQuality444
}

public sealed record JpegProfile(int Quality = 85, JpegSubsampling Subsampling = JpegSubsampling.Standard420);

public sealed record PngProfile(int CompressionLevel = 6, SKPngEncoderFilterFlags FilterFlags = SKPngEncoderFilterFlags.AllFilters);

public sealed record WebpProfile(bool Lossless = false, int Quality = 80);

/// <summary>
/// Provides format-specific optimization profiles and encoder execution.
/// </summary>
public static class ImageOptimizationProfiles
{
    /// <summary>
    /// Checks whether AVIF encoding is supported by the current runtime stack.
    /// SkiaSharp on Windows does not bundle libavif encoder by default.
    /// </summary>
    public static bool IsAvifSupported => false;

    public static string AvifUnsupportedReason =>
        "AVIF encoding is not supported by the underlying SkiaSharp runtime on this platform. JPEG, PNG, and WebP are fully supported.";

    public static SKData EncodeJpeg(SKBitmap bitmap, JpegProfile profile, out string settingNote)
    {
        var quality = Math.Clamp(profile.Quality, 1, 100);
        var pixmap = bitmap.PeekPixels() ?? throw new InvalidOperationException("Bitmap has no pixel data to encode.");

        var options = new SKJpegEncoderOptions(
            quality: quality,
            downsample: profile.Subsampling == JpegSubsampling.HighQuality444 ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420,
            alphaOption: SKJpegEncoderAlphaOption.Ignore);

        var data = pixmap.Encode(options);
        if (data is null || data.IsEmpty)
        {
            throw new InvalidOperationException("JPEG encoder failed to produce output.");
        }

        settingNote = $"JPEG quality {quality} (subsampling: {profile.Subsampling}). Note: JPEG does not preserve transparency.";
        return data;
    }

    public static SKData EncodePng(SKBitmap bitmap, PngProfile profile, out string settingNote)
    {
        var compression = Math.Clamp(profile.CompressionLevel, 1, 9);
        var pixmap = bitmap.PeekPixels() ?? throw new InvalidOperationException("Bitmap has no pixel data to encode.");

        var options = new SKPngEncoderOptions(profile.FilterFlags, compression);
        var data = pixmap.Encode(options);
        if (data is null || data.IsEmpty)
        {
            throw new InvalidOperationException("PNG encoder failed to produce output.");
        }

        settingNote = $"PNG lossless (zlib compression {compression}, filters: {profile.FilterFlags}). Transparency preserved.";
        return data;
    }

    public static SKData EncodeWebp(SKBitmap bitmap, WebpProfile profile, out string settingNote)
    {
        var pixmap = bitmap.PeekPixels() ?? throw new InvalidOperationException("Bitmap has no pixel data to encode.");

        SKData? data;
        if (profile.Lossless)
        {
            data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100));
            settingNote = "WebP lossless encoding. Full pixel fidelity and transparency preserved.";
        }
        else
        {
            var quality = Math.Clamp(profile.Quality, 1, 100);
            data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality));
            settingNote = $"WebP lossy quality {quality}. Transparency preserved.";
        }

        if (data is null || data.IsEmpty)
        {
            throw new InvalidOperationException("WebP encoder failed to produce output.");
        }

        return data;
    }
}
