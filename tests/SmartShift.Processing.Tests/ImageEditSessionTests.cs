using SkiaSharp;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing.Images;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class ImageEditSessionTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly string _outputRoot;
    private readonly OutputPathAllocator _allocator;

    public ImageEditSessionTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), "smartshift-edit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputRoot);
        _allocator = new OutputPathAllocator(_outputRoot);
    }

    private string CreateTestImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool withAlpha = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartshift-session-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ext = SkiaImagePipeline.Extension(format);
        var path = Path.Combine(dir, $"test-{width}x{height}{ext}");

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, withAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(withAlpha ? new SKColor(255, 0, 0, 128) : SKColors.Red);

            // Add distinctive markers in corners for pixel verification
            // Top-Left: Green
            bitmap.SetPixel(0, 0, SKColors.Green);
            // Top-Right: Blue
            bitmap.SetPixel(width - 1, 0, SKColors.Blue);
            // Bottom-Left: Yellow
            bitmap.SetPixel(0, height - 1, SKColors.Yellow);
            // Bottom-Right: White
            bitmap.SetPixel(width - 1, height - 1, SKColors.White);
        }

        using var pixmap = bitmap.PeekPixels()!;
        using var data = format switch
        {
            SKEncodedImageFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(90)),
            SKEncodedImageFormat.Png => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
            SKEncodedImageFormat.Webp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)),
            _ => throw new InvalidOperationException()
        };

        using var fs = File.Create(path);
        data!.SaveTo(fs);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Session_InitializesWithOriginalDimensions()
    {
        var path = CreateTestImage(800, 600);
        using var session = new ImageEditSession(path);

        Assert.Equal(800, session.OriginalWidth);
        Assert.Equal(600, session.OriginalHeight);
        Assert.Equal(800, session.CurrentWidth);
        Assert.Equal(600, session.CurrentHeight);
        Assert.False(session.HasUnsavedChanges);
    }

    [Fact]
    public void Crop_CenterRegion_UpdatesDimensionsCorrectly()
    {
        var path = CreateTestImage(1000, 800);
        using var session = new ImageEditSession(path);

        var cropRect = new SKRectI(200, 150, 800, 650); // 600 x 500
        session.Crop(cropRect);

        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(500, session.CurrentHeight);
        Assert.True(session.HasUnsavedChanges);
    }

    [Fact]
    public void Crop_BoundariesAndCorners_Succeeds()
    {
        var path = CreateTestImage(400, 300);
        using var session = new ImageEditSession(path);

        // Near top-left
        session.Crop(new SKRectI(0, 0, 100, 100));
        Assert.Equal(100, session.CurrentWidth);
        Assert.Equal(100, session.CurrentHeight);

        // Near bottom-right of current image
        session.Crop(new SKRectI(50, 50, 100, 100));
        Assert.Equal(50, session.CurrentWidth);
        Assert.Equal(50, session.CurrentHeight);
    }

    [Fact]
    public void Crop_InvalidOrOutOfBounds_ThrowsArgumentOutOfRangeException()
    {
        var path = CreateTestImage(400, 300);
        using var session = new ImageEditSession(path);

        // Negative left
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Crop(new SKRectI(-10, 0, 100, 100)));
        // Zero width
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Crop(new SKRectI(0, 0, 0, 100)));
        // Exceeds width
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Crop(new SKRectI(0, 0, 500, 300)));
    }

    [Fact]
    public void Rotate_90DegreesClockwise_SwapsDimensionsAndComposes()
    {
        var path = CreateTestImage(800, 600);
        using var session = new ImageEditSession(path);

        session.Rotate90Clockwise();
        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(800, session.CurrentHeight);

        session.Rotate90Clockwise();
        Assert.Equal(800, session.CurrentWidth);
        Assert.Equal(600, session.CurrentHeight);

        session.Rotate90Clockwise();
        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(800, session.CurrentHeight);

        session.Rotate90Clockwise();
        // 4 x 90 CW = 360, back to original dimensions
        Assert.Equal(800, session.CurrentWidth);
        Assert.Equal(600, session.CurrentHeight);
    }

    [Fact]
    public void Rotate_90DegreesCounterClockwise_SwapsDimensions()
    {
        var path = CreateTestImage(800, 600);
        using var session = new ImageEditSession(path);

        session.Rotate90CounterClockwise();
        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(800, session.CurrentHeight);
    }

    [Fact]
    public void Rotate_PixelLevelCorrectness_MovesCornerPixelsAccurately()
    {
        // 4x4 bitmap with distinct corners
        var path = CreateTestImage(4, 4);
        using var session = new ImageEditSession(path);

        // Initial:
        // (0,0) = Green, (3,0) = Blue, (0,3) = Yellow, (3,3) = White
        Assert.Equal(SKColors.Green, session.WorkingBitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, session.WorkingBitmap.GetPixel(3, 0));
        Assert.Equal(SKColors.Yellow, session.WorkingBitmap.GetPixel(0, 3));
        Assert.Equal(SKColors.White, session.WorkingBitmap.GetPixel(3, 3));

        // Rotate 90 CW:
        // Old (0,0) moves to (3,0) -> Green at (3,0)
        // Old (3,0) moves to (3,3) -> Blue at (3,3)
        // Old (0,3) moves to (0,0) -> Yellow at (0,0)
        // Old (3,3) moves to (0,3) -> White at (0,3)
        session.Rotate90Clockwise();

        Assert.Equal(SKColors.Yellow, session.WorkingBitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, session.WorkingBitmap.GetPixel(3, 0));
        Assert.Equal(SKColors.White, session.WorkingBitmap.GetPixel(0, 3));
        Assert.Equal(SKColors.Blue, session.WorkingBitmap.GetPixel(3, 3));
    }

    [Fact]
    public void Flip_HorizontalAndVertical_PixelLevelCorrectness()
    {
        var path = CreateTestImage(4, 4);
        using var session = new ImageEditSession(path);

        // Flip Horizontal:
        // Left corners swap with Right corners
        // (0,0) [Green] swaps with (3,0) [Blue]
        // (0,3) [Yellow] swaps with (3,3) [White]
        session.FlipHorizontal();

        Assert.Equal(SKColors.Blue, session.WorkingBitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, session.WorkingBitmap.GetPixel(3, 0));
        Assert.Equal(SKColors.White, session.WorkingBitmap.GetPixel(0, 3));
        Assert.Equal(SKColors.Yellow, session.WorkingBitmap.GetPixel(3, 3));

        // Flip Vertical:
        // Top corners swap with Bottom corners
        session.FlipVertical();

        Assert.Equal(SKColors.White, session.WorkingBitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Yellow, session.WorkingBitmap.GetPixel(3, 0));
        Assert.Equal(SKColors.Blue, session.WorkingBitmap.GetPixel(0, 3));
        Assert.Equal(SKColors.Green, session.WorkingBitmap.GetPixel(3, 3));
    }

    [Fact]
    public void Rotate_CombinedWithCrop_ProducesPredictableResult()
    {
        var path = CreateTestImage(800, 600);
        using var session = new ImageEditSession(path);

        // Rotate to 600x800
        session.Rotate90Clockwise();
        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(800, session.CurrentHeight);

        // Crop to 300x400
        session.Crop(new SKRectI(50, 50, 350, 450));
        Assert.Equal(300, session.CurrentWidth);
        Assert.Equal(400, session.CurrentHeight);
    }

    [Fact]
    public void Resize_ExplicitDimensionsAndPresets_Succeeds()
    {
        var path = CreateTestImage(1000, 800);
        using var session = new ImageEditSession(path);

        // Explicit resize
        session.Resize(500, 400);
        Assert.Equal(500, session.CurrentWidth);
        Assert.Equal(400, session.CurrentHeight);

        // Percentage preset: 50%
        session.ResizePercentage(50);
        Assert.Equal(250, session.CurrentWidth);
        Assert.Equal(200, session.CurrentHeight);

        // Longest edge preset: 1080px (maintaining aspect ratio 250:200 = 5:4)
        session.ResizeToLongestEdge(1080);
        Assert.Equal(1080, session.CurrentWidth);
        Assert.Equal((int)Math.Round(1080 * (200.0 / 250.0)), session.CurrentHeight);
    }

    [Fact]
    public void Resize_InvalidDimensions_Throws()
    {
        var path = CreateTestImage(400, 300);
        using var session = new ImageEditSession(path);

        Assert.Throws<ArgumentException>(() => session.Resize(0, 100));
        Assert.Throws<ArgumentException>(() => session.Resize(100, -5));
        // Exceeds safety limits (max dimension 8192)
        Assert.Throws<InvalidOperationException>(() => session.Resize(9000, 9000));
    }

    [Fact]
    public void AlphaTransparency_PreservedThroughTransformations()
    {
        var path = CreateTestImage(100, 100, SKEncodedImageFormat.Png, withAlpha: true);
        using var session = new ImageEditSession(path);

        session.Rotate90Clockwise();
        session.Crop(new SKRectI(10, 10, 60, 60));
        session.Resize(100, 100);

        // Center pixel should still have alpha < 255
        var centerPixel = session.WorkingBitmap.GetPixel(50, 50);
        Assert.True(centerPixel.Alpha < 255, $"Expected alpha < 255 but got {centerPixel.Alpha}");
    }

    [Fact]
    public void Reset_RestoresOriginalState()
    {
        var path = CreateTestImage(800, 600);
        using var session = new ImageEditSession(path);

        session.Rotate90Clockwise();
        session.Crop(new SKRectI(0, 0, 300, 300));
        session.Resize(100, 100);
        Assert.True(session.HasUnsavedChanges);

        session.Reset();

        Assert.Equal(800, session.CurrentWidth);
        Assert.Equal(600, session.CurrentHeight);
        Assert.False(session.HasUnsavedChanges);
    }

    [Fact]
    public void Save_SafeOutputPipeline_NeverOverwritesOriginalAndCreatesValidOutput()
    {
        var path = CreateTestImage(600, 400, SKEncodedImageFormat.Png);
        var originalHash = FileHasher.Sha256Hex(path);

        using (var session = new ImageEditSession(path))
        {
            session.Crop(new SKRectI(100, 50, 500, 350)); // 400x300
            session.Rotate90Clockwise(); // 300x400

            var result = session.Save(_allocator, _outputRoot);

            Assert.True(result.Succeeded, result.Status);
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.NotEqual(path, result.OutputPath);

            // Verify original file remained intact
            Assert.Equal(originalHash, FileHasher.Sha256Hex(path));

            // Verify output dimensions and format
            SkiaImagePipeline.ValidateEncoded(result.OutputPath, SKEncodedImageFormat.Png, out var outW, out var outH);
            Assert.Equal(300, outW);
            Assert.Equal(400, outH);
        }
    }

    [Fact]
    public void ExifOrientedImage_BakesOrientationOnDecode_CropIsConsistent()
    {
        // Create an unoriented 800x600 bitmap with known markers
        using var rawBmp = new SKBitmap(800, 600);
        rawBmp.Erase(SKColors.Blue);
        rawBmp.SetPixel(0, 0, SKColors.Green);

        // Apply RightTop (Exif orientation 6: 90 deg clockwise)
        using var orientedBmp = SkiaImagePipeline.ApplyOrigin(rawBmp.Copy(), SKEncodedOrigin.RightTop);
        Assert.Equal(600, orientedBmp.Width);
        Assert.Equal(800, orientedBmp.Height);

        // Green pixel was at top-left (0,0), after RightTop it is at top-right (599, 0)
        Assert.Equal(SKColors.Green, orientedBmp.GetPixel(599, 0));

        // Create edit session directly from oriented bitmap
        using var session = new ImageEditSession(orientedBmp, SKEncodedImageFormat.Jpeg);
        Assert.Equal(600, session.CurrentWidth);
        Assert.Equal(800, session.CurrentHeight);

        // Crop the top-right quadrant containing the green marker
        session.Crop(new SKRectI(300, 0, 600, 400));
        Assert.Equal(300, session.CurrentWidth);
        Assert.Equal(400, session.CurrentHeight);
        Assert.Equal(SKColors.Green, session.WorkingBitmap.GetPixel(299, 0));
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
                var dir = Path.GetDirectoryName(file);
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        if (Directory.Exists(_outputRoot))
        {
            try { Directory.Delete(_outputRoot, true); } catch { }
        }
    }
}
