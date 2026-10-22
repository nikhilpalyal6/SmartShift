using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Images;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class ImageProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartshift-img-" + Guid.NewGuid().ToString("N"));
    private readonly FileClassifier _classifier = new();
    private readonly List<string> _inputs = [];

    public ImageProcessorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Compress_Jpeg_WritesValidJpegAndLeavesSourceHash()
    {
        var input = Save("photo.jpg", Encode(8, 6, SKEncodedImageFormat.Jpeg, opaqueRed: true));
        var before = FileHasher.Sha256Hex(input);
        var result = await Run(ActionIds.CompressImage, input, Quality(40));
        Assert.True(result.Succeeded, result.Status);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(".jpg", Path.GetExtension(result.OutputPath), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before, FileHasher.Sha256Hex(input));
        SkiaImagePipeline.ValidateEncoded(result.OutputPath!, SKEncodedImageFormat.Jpeg, out var w, out var h);
        Assert.Equal(8, w);
        Assert.Equal(6, h);
        Assert.Equal(DetectedFormat.Jpeg, _classifier.Classify(result.OutputPath!).Format);
    }

    [Fact]
    public async Task Compress_Png_PreservesAlpha()
    {
        var input = Save("alpha.png", EncodeRgbaPng());
        var before = File.ReadAllBytes(input);
        var result = await Run(ActionIds.CompressImage, input, Quality(80));
        Assert.True(result.Succeeded, result.Status);
        Assert.Equal(before, File.ReadAllBytes(input));
        using var decoded = SkiaImagePipeline.Decode(result.OutputPath!, CancellationToken.None);
        Assert.Equal(SKEncodedImageFormat.Png, decoded.SourceFormat);
        var pixel = decoded.Bitmap.GetPixel(0, 0);
        Assert.True(pixel.Alpha < 255);
        Assert.Contains("lossless", result.Details["encoder"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compress_Webp_WritesValidWebpAndLeavesSourceHash()
    {
        var input = Save("graphic.webp", Encode(12, 10, SKEncodedImageFormat.Webp, opaqueRed: false));
        var before = FileHasher.Sha256Hex(input);
        var result = await Run(ActionIds.CompressImage, input, Quality(60));
        Assert.True(result.Succeeded, result.Status);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(".webp", Path.GetExtension(result.OutputPath), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before, FileHasher.Sha256Hex(input));
        SkiaImagePipeline.ValidateEncoded(result.OutputPath!, SKEncodedImageFormat.Webp, out var w, out var h);
        Assert.Equal(12, w);
        Assert.Equal(10, h);
        Assert.Equal(DetectedFormat.Webp, _classifier.Classify(result.OutputPath!).Format);
        using var decoded = SkiaImagePipeline.Decode(result.OutputPath!, CancellationToken.None);
        Assert.True(decoded.Bitmap.GetPixel(0, 0).Alpha < 255);
    }

    [Fact]
    public async Task ConvertToWebp_FromPngAndJpeg_IsValidWebp()
    {
        var png = Save("src.png", EncodeRgbaPng());
        var jpeg = Save("src.jpg", Encode(4, 4, SKEncodedImageFormat.Jpeg, opaqueRed: true));
        var pngResult = await Run(ActionIds.ConvertToWebp, png, Quality(70));
        var jpegResult = await Run(ActionIds.ConvertToWebp, jpeg, Quality(70));
        Assert.True(pngResult.Succeeded, pngResult.Status);
        Assert.True(jpegResult.Succeeded, jpegResult.Status);
        Assert.Equal(".webp", Path.GetExtension(pngResult.OutputPath), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DetectedFormat.Webp, _classifier.Classify(pngResult.OutputPath!).Format);
        Assert.Equal(DetectedFormat.Webp, _classifier.Classify(jpegResult.OutputPath!).Format);
        using var decoded = SkiaImagePipeline.Decode(pngResult.OutputPath!, CancellationToken.None);
        Assert.True(decoded.Bitmap.GetPixel(0, 0).Alpha < 255);
    }

    [Fact]
    public async Task ConvertToWebp_FromWebp_ProducesConvertedSuffix()
    {
        var webp = Save("orig.webp", Encode(6, 6, SKEncodedImageFormat.Webp, opaqueRed: true));
        var before = FileHasher.Sha256Hex(webp);
        var result = await Run(ActionIds.ConvertToWebp, webp, Quality(85));
        Assert.True(result.Succeeded, result.Status);
        Assert.Equal(Path.Combine(_root, "orig-converted.webp"), result.OutputPath);
        Assert.Equal(before, FileHasher.Sha256Hex(webp));
        Assert.Equal(DetectedFormat.Webp, _classifier.Classify(result.OutputPath!).Format);
    }

    [Fact]
    public async Task Resize_KeepAspect_AndExactDimensions()
    {
        var input = Save("box.png", Encode(10, 5, SKEncodedImageFormat.Png, opaqueRed: true));
        var keep = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "4",
            ["keepAspect"] = "true",
            ["height"] = "99"
        }));
        Assert.True(keep.Succeeded, keep.Status);
        SkiaImagePipeline.ValidateEncoded(keep.OutputPath!, SKEncodedImageFormat.Png, out var w, out var h);
        Assert.Equal(4, w);
        Assert.Equal(2, h);

        var exact = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "3",
            ["height"] = "7",
            ["keepAspect"] = "false"
        }));
        Assert.True(exact.Succeeded, exact.Status);
        SkiaImagePipeline.ValidateEncoded(exact.OutputPath!, SKEncodedImageFormat.Png, out w, out h);
        Assert.Equal(3, w);
        Assert.Equal(7, h);
    }

    [Fact]
    public async Task Resize_PreservesAlphaTransparency()
    {
        var input = Save("trans.png", Encode(16, 16, SKEncodedImageFormat.Png, opaqueRed: false));
        var result = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "8",
            ["height"] = "8",
            ["keepAspect"] = "false"
        }));
        Assert.True(result.Succeeded, result.Status);
        using var decoded = SkiaImagePipeline.Decode(result.OutputPath!, CancellationToken.None);
        Assert.Equal(8, decoded.Bitmap.Width);
        Assert.Equal(8, decoded.Bitmap.Height);
        var corner = decoded.Bitmap.GetPixel(0, 0);
        Assert.True(corner.Alpha < 255);
    }

    [Fact]
    public async Task StripMetadata_RemovesExifGpsSignatures()
    {
        var jpeg = Encode(6, 4, SKEncodedImageFormat.Jpeg, opaqueRed: true);
        var tagged = WithExifAndGps(jpeg);
        var input = Save("tagged.jpg", tagged);
        Assert.True(SkiaImagePipeline.ContainsExifOrGpsSignature(input));
        var before = FileHasher.Sha256Hex(input);
        var result = await Run(ActionIds.RemoveImageMetadata, input, new ProcessingOptions());
        Assert.True(result.Succeeded, result.Status);
        Assert.Equal(before, FileHasher.Sha256Hex(input));
        Assert.False(SkiaImagePipeline.ContainsExifOrGpsSignature(result.OutputPath!));
        Assert.Equal(DetectedFormat.Jpeg, _classifier.Classify(result.OutputPath!).Format);
    }

    [Fact]
    public void Orientation_AllEightOriginsHandledCorrectly()
    {
        var testOrigins = new[]
        {
            (SKEncodedOrigin.TopLeft, 4, 2, 0, 0),
            (SKEncodedOrigin.TopRight, 4, 2, 3, 0),
            (SKEncodedOrigin.BottomRight, 4, 2, 3, 1),
            (SKEncodedOrigin.BottomLeft, 4, 2, 0, 1),
            (SKEncodedOrigin.LeftTop, 2, 4, 0, 0),
            (SKEncodedOrigin.RightTop, 2, 4, 1, 0),
            (SKEncodedOrigin.RightBottom, 2, 4, 1, 3),
            (SKEncodedOrigin.LeftBottom, 2, 4, 0, 3)
        };

        foreach (var (origin, expectedWidth, expectedHeight, expectedRedX, expectedRedY) in testOrigins)
        {
            var src = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            src.Erase(SKColors.Blue);
            src.SetPixel(0, 0, SKColors.Red);

            using var oriented = SkiaImagePipeline.ApplyOrigin(src, origin);
            Assert.Equal(expectedWidth, oriented.Width);
            Assert.Equal(expectedHeight, oriented.Height);

            var pixel = oriented.GetPixel(expectedRedX, expectedRedY);
            Assert.True(pixel.Red > 200, $"Origin {origin} failed at expected red pos ({expectedRedX},{expectedRedY}).");
        }
    }

    [Fact]
    public async Task CorruptUnsupportedInvalidOptions_FailWithoutOutput()
    {
        var corrupt = Save("bad.png", "this is not an image"u8.ToArray());
        var gif = Save("anim.gif", "GIF89a"u8.ToArray().Concat(new byte[20]).ToArray());
        var ok = Save("ok.png", Encode(2, 2, SKEncodedImageFormat.Png, opaqueRed: true));

        var bad = await Run(ActionIds.CompressImage, corrupt, Quality(80));
        Assert.False(bad.Succeeded);
        Assert.Null(bad.OutputPath);

        var gifResult = await Run(ActionIds.ConvertToWebp, gif, Quality(80));
        Assert.False(gifResult.Succeeded);

        var quality = await Run(ActionIds.CompressImage, ok, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "0" }));
        Assert.False(quality.Succeeded);

        var dims = await Run(ActionIds.ResizeImage, ok, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "0",
            ["height"] = "10",
            ["keepAspect"] = "false"
        }));
        Assert.False(dims.Succeeded);
        Assert.True(File.Exists(ok));
        Assert.DoesNotContain(Directory.GetFiles(_root), p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OversizedDecodeAndResize_AreRejected()
    {
        var hugeHeader = Save("huge.png", OversizedPngHeader(ImageSafetyLimits.MaxDimension + 1, 10));
        var huge = await Run(ActionIds.CompressImage, hugeHeader, Quality(80));
        Assert.False(huge.Succeeded);

        var input = Save("n.png", Encode(2, 2, SKEncodedImageFormat.Png, opaqueRed: true));
        var tooWide = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = (ImageSafetyLimits.MaxDimension + 1).ToString(),
            ["keepAspect"] = "true"
        }));
        Assert.False(tooWide.Succeeded);

        var excessivePixels = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "7000",
            ["height"] = "7000",
            ["keepAspect"] = "false"
        }));
        Assert.False(excessivePixels.Succeeded);
    }

    [Fact]
    public async Task Collision_AllocatesNumberedName_AndCancelCleansPartial()
    {
        var input = Save("col.png", Encode(3, 3, SKEncodedImageFormat.Png, opaqueRed: true));
        var taken = Path.Combine(_root, "col-compressed.png");
        File.WriteAllText(taken, "occupied");
        var result = await Run(ActionIds.CompressImage, input, Quality(80));
        Assert.True(result.Succeeded, result.Status);
        Assert.Equal(Path.Combine(_root, "col-compressed (1).png"), result.OutputPath);
        Assert.Equal("occupied", File.ReadAllText(taken));

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await Catalog().Find(ActionIds.ConvertToWebp)!.ProcessAsync(
            new ProcessingRequest(_classifier.Classify(input), ActionIds.ConvertToWebp, Quality(80), _root),
            null,
            cts.Token);
        Assert.True(cancelled.WasCancelled);
        Assert.DoesNotContain(Directory.GetFiles(_root), p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourceFile_RemainsByteForByteUnchanged_AcrossAllImageOperations()
    {
        var input = Save("immutable.png", Encode(8, 8, SKEncodedImageFormat.Png, opaqueRed: true));
        var beforeHash = FileHasher.Sha256Hex(input);

        var compress = await Run(ActionIds.CompressImage, input, Quality(80));
        Assert.True(compress.Succeeded);
        Assert.Equal(beforeHash, FileHasher.Sha256Hex(input));

        var convert = await Run(ActionIds.ConvertToWebp, input, Quality(80));
        Assert.True(convert.Succeeded);
        Assert.Equal(beforeHash, FileHasher.Sha256Hex(input));

        var resize = await Run(ActionIds.ResizeImage, input, new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "4",
            ["height"] = "4",
            ["keepAspect"] = "false"
        }));
        Assert.True(resize.Succeeded);
        Assert.Equal(beforeHash, FileHasher.Sha256Hex(input));

        var strip = await Run(ActionIds.RemoveImageMetadata, input, new ProcessingOptions());
        Assert.True(strip.Succeeded);
        Assert.Equal(beforeHash, FileHasher.Sha256Hex(input));
    }

    [Fact]
    public async Task ImageActions_AreRegisteredAndAvailable_PdfTransformsAreNot()
    {
        var catalog = Catalog();
        Assert.True(catalog.Find(ActionIds.CompressImage)!.IsAvailable);
        Assert.True(catalog.Find(ActionIds.ConvertToWebp)!.IsAvailable);
        Assert.True(catalog.Find(ActionIds.ResizeImage)!.IsAvailable);
        Assert.True(catalog.Find(ActionIds.RemoveImageMetadata)!.IsAvailable);
        Assert.True(catalog.Find(ActionIds.CompressPdf)!.IsAvailable);
        Assert.True(catalog.Find(ActionIds.PreviewPdf)!.IsAvailable);
    }

    public void Dispose()
    {
        foreach (var path in _inputs)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                var dir = Path.GetDirectoryName(path);
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ProcessorCatalog Catalog() => new(_classifier, new OutputPathAllocator(_root));

    private async Task<ProcessingResult> Run(string actionId, string path, ProcessingOptions options)
    {
        var processor = Catalog().Find(actionId)!;
        return await processor.ProcessAsync(
            new ProcessingRequest(_classifier.Classify(path), actionId, options, _root),
            null,
            CancellationToken.None);
    }

    private static ProcessingOptions Quality(int quality)
        => new(new Dictionary<string, string> { ["quality"] = quality.ToString() });

    private string Save(string name, byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartshift-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        _inputs.Add(path);
        return path;
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, bool opaqueRed)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bitmap.Erase(opaqueRed ? new SKColor(200, 30, 30, 255) : SKColors.Transparent);
        bitmap.SetPixel(0, 0, opaqueRed ? new SKColor(10, 200, 40, 255) : new SKColor(10, 200, 40, 90));
        using var pixmap = bitmap.PeekPixels() ?? throw new InvalidOperationException("pixels");
        using var data = format switch
        {
            SKEncodedImageFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(80)),
            SKEncodedImageFormat.Png => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
            SKEncodedImageFormat.Webp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80)),
            _ => throw new InvalidOperationException()
        };
        return data!.ToArray();
    }

    private static byte[] EncodeRgbaPng() => Encode(4, 4, SKEncodedImageFormat.Png, opaqueRed: false);

    private static byte[] WithExifAndGps(byte[] jpeg)
    {
        var payload = "Exif\0\0II*\0\x08\0\0\0GPS"u8.ToArray();
        var app1 = new byte[4 + payload.Length];
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        var length = payload.Length + 2;
        app1[2] = (byte)(length >> 8);
        app1[3] = (byte)length;
        Buffer.BlockCopy(payload, 0, app1, 4, payload.Length);
        var output = new byte[jpeg.Length + app1.Length];
        output[0] = 0xFF;
        output[1] = 0xD8;
        Buffer.BlockCopy(app1, 0, output, 2, app1.Length);
        Buffer.BlockCopy(jpeg, 2, output, 2 + app1.Length, jpeg.Length - 2);
        return output;
    }

    private static byte[] OversizedPngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24);
        ihdr[1] = (byte)(width >> 16);
        ihdr[2] = (byte)(width >> 8);
        ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24);
        ihdr[5] = (byte)(height >> 16);
        ihdr[6] = (byte)(height >> 8);
        ihdr[7] = (byte)height;
        ihdr[8] = 8;
        ihdr[9] = 2;
        var chunkType = "IHDR"u8.ToArray();
        var crc = Crc32(chunkType.Concat(ihdr).ToArray());
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        ms.Write([(byte)0, 0, 0, 13]);
        ms.Write(chunkType);
        ms.Write(ihdr);
        ms.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        var iend = "IEND"u8.ToArray();
        var iendCrc = Crc32(iend);
        ms.Write([0, 0, 0, 0]);
        ms.Write(iend);
        ms.Write([(byte)(iendCrc >> 24), (byte)(iendCrc >> 16), (byte)(iendCrc >> 8), (byte)iendCrc]);
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var mask = (crc & 1) != 0 ? 0xEDB88320 : 0u;
                crc = (crc >> 1) ^ mask;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
