using SkiaSharp;
using SmartShift.Processing.Images;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class ImageCoordinateMapperTests
{
    [Fact]
    public void ComputeViewport_SquareContainerAndImage_ScalesEqually()
    {
        var viewport = ImageCoordinateMapper.ComputeViewport(500, 500, 1000, 1000);
        Assert.Equal(0.5, viewport.Scale);
        Assert.Equal(500, viewport.DisplayedWidth);
        Assert.Equal(500, viewport.DisplayedHeight);
        Assert.Equal(0, viewport.OffsetX);
        Assert.Equal(0, viewport.OffsetY);
    }

    [Fact]
    public void ComputeViewport_WiderContainerThanImage_CentersWithLetterboxX()
    {
        // Container: 1000x500 (2:1). Image: 500x500 (1:1).
        // Scale is limited by height: 500/500 = 1.0.
        // Displayed: 500x500. OffsetX = (1000 - 500) / 2 = 250. OffsetY = 0.
        var viewport = ImageCoordinateMapper.ComputeViewport(1000, 500, 500, 500);
        Assert.Equal(1.0, viewport.Scale);
        Assert.Equal(500, viewport.DisplayedWidth);
        Assert.Equal(500, viewport.DisplayedHeight);
        Assert.Equal(250, viewport.OffsetX);
        Assert.Equal(0, viewport.OffsetY);
    }

    [Fact]
    public void ComputeViewport_TallerContainerThanImage_CentersWithLetterboxY()
    {
        // Container: 500x1000 (1:2). Image: 500x500 (1:1).
        // Scale is limited by width: 500/500 = 1.0.
        // Displayed: 500x500. OffsetX = 0. OffsetY = (1000 - 500) / 2 = 250.
        var viewport = ImageCoordinateMapper.ComputeViewport(500, 1000, 500, 500);
        Assert.Equal(1.0, viewport.Scale);
        Assert.Equal(500, viewport.DisplayedWidth);
        Assert.Equal(500, viewport.DisplayedHeight);
        Assert.Equal(0, viewport.OffsetX);
        Assert.Equal(250, viewport.OffsetY);
    }

    [Fact]
    public void ContainerToImagePixel_WithinDisplayedImage_MapsAccurately()
    {
        // Container: 800x600. Image: 1600x1200 (scale 0.5, no letterboxing).
        var viewport = ImageCoordinateMapper.ComputeViewport(800, 600, 1600, 1200);
        var (px, py) = ImageCoordinateMapper.ContainerToImagePixel(400, 300, viewport, 1600, 1200);
        Assert.Equal(800, px);
        Assert.Equal(600, py);
    }

    [Fact]
    public void ContainerToImagePixel_LetterboxedRegion_ClampsToImageEdge()
    {
        // Container: 1000x500. Image: 500x500. OffsetX=250.
        var viewport = ImageCoordinateMapper.ComputeViewport(1000, 500, 500, 500);

        // Click in left black bar (x = 50)
        var (leftPx, leftPy) = ImageCoordinateMapper.ContainerToImagePixel(50, 250, viewport, 500, 500);
        Assert.Equal(0, leftPx);
        Assert.Equal(250, leftPy);

        // Click in right black bar (x = 950)
        var (rightPx, rightPy) = ImageCoordinateMapper.ContainerToImagePixel(950, 250, viewport, 500, 500);
        Assert.Equal(500, rightPx);
        Assert.Equal(250, rightPy);
    }

    [Fact]
    public void ImagePixelToContainer_MapsBackCorrectly()
    {
        var viewport = ImageCoordinateMapper.ComputeViewport(1000, 500, 500, 500); // Scale 1.0, OffsetX 250, OffsetY 0
        var cropRect = new SKRectI(100, 50, 300, 250); // w=200, h=200
        var (x, y, w, h) = ImageCoordinateMapper.ImagePixelToContainer(cropRect, viewport);

        Assert.Equal(350, x); // 250 + 100
        Assert.Equal(50, y);
        Assert.Equal(200, w);
        Assert.Equal(200, h);
    }

    [Fact]
    public void NormalizeRect_HandlesFlippedCoordinatesAndClamps()
    {
        var norm = ImageCoordinateMapper.NormalizeRect(800, 600, 200, 100, 1000, 1000);
        Assert.Equal(200, norm.Left);
        Assert.Equal(100, norm.Top);
        Assert.Equal(800, norm.Right);
        Assert.Equal(600, norm.Bottom);
        Assert.Equal(600, norm.Width);
        Assert.Equal(500, norm.Height);

        // Out of bounds clamping
        var clamped = ImageCoordinateMapper.NormalizeRect(-50, -50, 1500, 1500, 1000, 1000);
        Assert.Equal(0, clamped.Left);
        Assert.Equal(0, clamped.Top);
        Assert.Equal(1000, clamped.Right);
        Assert.Equal(1000, clamped.Bottom);
    }

    [Theory]
    [InlineData(1.0, 400, 400)]    // 1:1
    [InlineData(4.0 / 3.0, 400, 300)] // 4:3
    [InlineData(16.0 / 9.0, 400, 225)] // 16:9
    public void ConstrainToAspectRatio_AdjustsDimensionsAccurately(double ratio, int expectedWidth, int expectedHeight)
    {
        var input = new SKRectI(0, 0, 400, 400);
        var result = ImageCoordinateMapper.ConstrainToAspectRatio(input, ratio, 1920, 1080);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.True(result.Left >= 0 && result.Right <= 1920);
        Assert.True(result.Top >= 0 && result.Bottom <= 1080);
    }
}
