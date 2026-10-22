using SkiaSharp;

namespace SmartShift.Processing.Images;

public enum CropHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

public sealed record ViewportBounds(double OffsetX, double OffsetY, double DisplayedWidth, double DisplayedHeight, double Scale)
{
    public bool ContainsContainerPoint(double x, double y)
        => x >= OffsetX && x <= OffsetX + DisplayedWidth &&
           y >= OffsetY && y <= OffsetY + DisplayedHeight;
}

public static class ImageCoordinateMapper
{
    public static ViewportBounds ComputeViewport(double containerWidth, double containerHeight, int imageWidth, int imageHeight)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return new ViewportBounds(0, 0, 0, 0, 1.0);
        }

        var scale = Math.Min(containerWidth / imageWidth, containerHeight / imageHeight);
        var displayedWidth = imageWidth * scale;
        var displayedHeight = imageHeight * scale;
        var offsetX = (containerWidth - displayedWidth) / 2.0;
        var offsetY = (containerHeight - displayedHeight) / 2.0;

        return new ViewportBounds(offsetX, offsetY, displayedWidth, displayedHeight, scale);
    }

    public static (int PixelX, int PixelY) ContainerToImagePixel(
        double containerX,
        double containerY,
        ViewportBounds viewport,
        int imageWidth,
        int imageHeight)
    {
        if (viewport.Scale <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return (0, 0);
        }

        var clampedX = Math.Clamp(containerX, viewport.OffsetX, viewport.OffsetX + viewport.DisplayedWidth);
        var clampedY = Math.Clamp(containerY, viewport.OffsetY, viewport.OffsetY + viewport.DisplayedHeight);

        var px = (int)Math.Round((clampedX - viewport.OffsetX) / viewport.Scale);
        var py = (int)Math.Round((clampedY - viewport.OffsetY) / viewport.Scale);

        return (Math.Clamp(px, 0, imageWidth), Math.Clamp(py, 0, imageHeight));
    }

    public static (double X, double Y, double Width, double Height) ImagePixelToContainer(
        SKRectI cropRect,
        ViewportBounds viewport)
    {
        var x = viewport.OffsetX + (cropRect.Left * viewport.Scale);
        var y = viewport.OffsetY + (cropRect.Top * viewport.Scale);
        var w = cropRect.Width * viewport.Scale;
        var h = cropRect.Height * viewport.Scale;
        return (x, y, w, h);
    }

    public static SKRectI NormalizeRect(int x1, int y1, int x2, int y2, int imageWidth, int imageHeight)
    {
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var right = Math.Max(x1, x2);
        var bottom = Math.Max(y1, y2);

        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);

        return new SKRectI(left, top, right, bottom);
    }

    public static SKRectI ConstrainToAspectRatio(SKRectI rect, double? targetAspectRatio, int imageWidth, int imageHeight, CropHandle anchorHandle = CropHandle.None)
    {
        if (!targetAspectRatio.HasValue || targetAspectRatio.Value <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return rect;
        }

        var ratio = targetAspectRatio.Value;
        var currentWidth = rect.Width;
        var currentHeight = rect.Height;

        int newWidth;
        int newHeight;

        // If ratio > currentAspect, width should increase or height decrease
        if (currentWidth / (double)currentHeight > ratio)
        {
            // Width is too wide relative to ratio; reduce width to match height
            newHeight = currentHeight;
            newWidth = (int)Math.Round(newHeight * ratio);
            if (newWidth <= 0) newWidth = 1;
        }
        else
        {
            // Height is too tall relative to ratio; reduce height to match width
            newWidth = currentWidth;
            newHeight = (int)Math.Round(newWidth / ratio);
            if (newHeight <= 0) newHeight = 1;
        }

        int newLeft = rect.Left;
        int newTop = rect.Top;

        switch (anchorHandle)
        {
            case CropHandle.TopLeft:
                // Anchored at bottom-right
                newLeft = rect.Right - newWidth;
                newTop = rect.Bottom - newHeight;
                break;
            case CropHandle.TopRight:
                // Anchored at bottom-left
                newLeft = rect.Left;
                newTop = rect.Bottom - newHeight;
                break;
            case CropHandle.BottomLeft:
                // Anchored at top-right
                newLeft = rect.Right - newWidth;
                newTop = rect.Top;
                break;
            case CropHandle.BottomRight:
            case CropHandle.None:
            default:
                // Anchored at top-left
                newLeft = rect.Left;
                newTop = rect.Top;
                break;
        }

        // Clamp to image boundaries
        if (newLeft < 0)
        {
            newLeft = 0;
            if (newLeft + newWidth > imageWidth)
            {
                newWidth = imageWidth;
                newHeight = (int)Math.Round(newWidth / ratio);
            }
        }
        if (newTop < 0)
        {
            newTop = 0;
            if (newTop + newHeight > imageHeight)
            {
                newHeight = imageHeight;
                newWidth = (int)Math.Round(newHeight * ratio);
            }
        }
        if (newLeft + newWidth > imageWidth)
        {
            newLeft = Math.Max(0, imageWidth - newWidth);
        }
        if (newTop + newHeight > imageHeight)
        {
            newTop = Math.Max(0, imageHeight - newHeight);
        }

        return new SKRectI(newLeft, newTop, newLeft + newWidth, newTop + newHeight);
    }
}
