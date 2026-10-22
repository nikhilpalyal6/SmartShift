using SkiaSharp;

namespace SmartShift.Processing.Documents;

public static class DocumentLayoutEngine
{
    // Standard PDF page sizes in points (72 points per inch)
    public const double A4WidthPoints = 595.28;
    public const double A4HeightPoints = 841.89;

    public const double LetterWidthPoints = 612.0;
    public const double LetterHeightPoints = 792.0;

    // Standard PowerPoint slide dimensions in EMUs (1 inch = 914,400 EMUs)
    public const long Slide16x9WidthEmu = 12_192_000L;   // 13.333 inches
    public const long Slide16x9HeightEmu = 6_858_000L;    // 7.5 inches

    public const long Slide4x3WidthEmu = 9_144_000L;      // 10.0 inches
    public const long Slide4x3HeightEmu = 6_858_000L;     // 7.5 inches

    /// <summary>
    /// Computes the exact page layout dimensions, placement coordinates, and crop rectangle for a PDF page.
    /// All values are in points (1/72 inch).
    /// </summary>
    public static DocumentLayoutResult ComputePdfPageLayout(
        int imageWidth,
        int imageHeight,
        DocumentPageSettings settings)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentException("Image dimensions must be positive integers.");
        }

        double rawWidth;
        double rawHeight;

        if (settings.PageSize == PageSizeOption.ImageMatch || settings.Placement == ImagePlacementOption.ImageSize)
        {
            // Direct points matching image dimensions (assuming 72 pt/inch reference)
            rawWidth = imageWidth;
            rawHeight = imageHeight;
            return new DocumentLayoutResult(
                TargetWidth: rawWidth,
                TargetHeight: rawHeight,
                ImageX: 0,
                ImageY: 0,
                ImageWidth: rawWidth,
                ImageHeight: rawHeight,
                SourceCropRect: null);
        }
        else if (settings.PageSize == PageSizeOption.Letter)
        {
            rawWidth = LetterWidthPoints;
            rawHeight = LetterHeightPoints;
        }
        else
        {
            rawWidth = A4WidthPoints;
            rawHeight = A4HeightPoints;
        }

        // Determine orientation
        bool isLandscape;
        if (settings.Orientation == PageOrientationOption.Landscape)
        {
            isLandscape = true;
        }
        else if (settings.Orientation == PageOrientationOption.Portrait)
        {
            isLandscape = false;
        }
        else // Auto orientation
        {
            isLandscape = imageWidth > imageHeight;
        }

        double pageWidth = isLandscape ? Math.Max(rawWidth, rawHeight) : Math.Min(rawWidth, rawHeight);
        double pageHeight = isLandscape ? Math.Min(rawWidth, rawHeight) : Math.Max(rawWidth, rawHeight);

        double margin = Math.Max(0, settings.MarginPoints);
        double contentX = margin;
        double contentY = margin;
        double contentWidth = Math.Max(10.0, pageWidth - (2 * margin));
        double contentHeight = Math.Max(10.0, pageHeight - (2 * margin));

        if (settings.Placement == ImagePlacementOption.FillCrop)
        {
            // Content aspect ratio vs source image aspect ratio
            double contentRatio = contentWidth / contentHeight;
            double sourceRatio = (double)imageWidth / imageHeight;

            SKRectI cropRect;
            if (sourceRatio > contentRatio)
            {
                // Image is wider than content area: crop left and right
                int cropW = Math.Max(1, (int)Math.Round(imageHeight * contentRatio));
                int cropX = Math.Max(0, (imageWidth - cropW) / 2);
                cropRect = new SKRectI(cropX, 0, Math.Min(imageWidth, cropX + cropW), imageHeight);
            }
            else
            {
                // Image is taller than content area: crop top and bottom
                int cropH = Math.Max(1, (int)Math.Round(imageWidth / contentRatio));
                int cropY = Math.Max(0, (imageHeight - cropH) / 2);
                cropRect = new SKRectI(0, cropY, imageWidth, Math.Min(imageHeight, cropY + cropH));
            }

            return new DocumentLayoutResult(
                TargetWidth: pageWidth,
                TargetHeight: pageHeight,
                ImageX: contentX,
                ImageY: contentY,
                ImageWidth: contentWidth,
                ImageHeight: contentHeight,
                SourceCropRect: cropRect);
        }
        else // Contain / Fit (Default)
        {
            double scale = Math.Min(contentWidth / imageWidth, contentHeight / imageHeight);
            double fittedWidth = Math.Round(imageWidth * scale, 2);
            double fittedHeight = Math.Round(imageHeight * scale, 2);
            double imageX = Math.Round(contentX + (contentWidth - fittedWidth) / 2.0, 2);
            double imageY = Math.Round(contentY + (contentHeight - fittedHeight) / 2.0, 2);

            return new DocumentLayoutResult(
                TargetWidth: pageWidth,
                TargetHeight: pageHeight,
                ImageX: imageX,
                ImageY: imageY,
                ImageWidth: fittedWidth,
                ImageHeight: fittedHeight,
                SourceCropRect: null);
        }
    }

    /// <summary>
    /// Computes slide dimensions, picture coordinates, and crop rectangle for a PowerPoint slide.
    /// Dimensions and offsets are returned in EMUs (1 inch = 914,400 EMUs).
    /// </summary>
    public static DocumentLayoutResult ComputePptxSlideLayout(
        int imageWidth,
        int imageHeight,
        DocumentSlideSettings settings)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentException("Image dimensions must be positive integers.");
        }

        long slideWidthEmu;
        long slideHeightEmu;

        if (settings.SlideSize == SlideSizeOption.Standard4x3)
        {
            slideWidthEmu = Slide4x3WidthEmu;
            slideHeightEmu = Slide4x3HeightEmu;
        }
        else if (settings.SlideSize == SlideSizeOption.ImageMatch)
        {
            // Preserve image aspect ratio, standard height of 7.5 inches
            slideHeightEmu = Slide16x9HeightEmu;
            slideWidthEmu = (long)Math.Round(slideHeightEmu * ((double)imageWidth / imageHeight));
        }
        else // Widescreen 16:9 (Default)
        {
            slideWidthEmu = Slide16x9WidthEmu;
            slideHeightEmu = Slide16x9HeightEmu;
        }

        if (settings.Placement == ImagePlacementOption.FillCrop)
        {
            double slideRatio = (double)slideWidthEmu / slideHeightEmu;
            double imageRatio = (double)imageWidth / imageHeight;

            SKRectI cropRect;
            if (imageRatio > slideRatio)
            {
                int cropW = Math.Max(1, (int)Math.Round(imageHeight * slideRatio));
                int cropX = Math.Max(0, (imageWidth - cropW) / 2);
                cropRect = new SKRectI(cropX, 0, Math.Min(imageWidth, cropX + cropW), imageHeight);
            }
            else
            {
                int cropH = Math.Max(1, (int)Math.Round(imageWidth / slideRatio));
                int cropY = Math.Max(0, (imageHeight - cropH) / 2);
                cropRect = new SKRectI(0, cropY, imageWidth, Math.Min(imageHeight, cropY + cropH));
            }

            return new DocumentLayoutResult(
                TargetWidth: slideWidthEmu,
                TargetHeight: slideHeightEmu,
                ImageX: 0,
                ImageY: 0,
                ImageWidth: slideWidthEmu,
                ImageHeight: slideHeightEmu,
                SourceCropRect: cropRect);
        }
        else // Contain / Fit (Default)
        {
            double scale = Math.Min((double)slideWidthEmu / imageWidth, (double)slideHeightEmu / imageHeight);
            long fittedWidth = (long)Math.Round(imageWidth * scale);
            long fittedHeight = (long)Math.Round(imageHeight * scale);
            long imageX = (slideWidthEmu - fittedWidth) / 2;
            long imageY = (slideHeightEmu - fittedHeight) / 2;

            return new DocumentLayoutResult(
                TargetWidth: slideWidthEmu,
                TargetHeight: slideHeightEmu,
                ImageX: imageX,
                ImageY: imageY,
                ImageWidth: fittedWidth,
                ImageHeight: fittedHeight,
                SourceCropRect: null);
        }
    }
}
