using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Images;

namespace SmartShift.Processing.Documents;

public static class MultiImagePdfGenerator
{
    public static string GeneratePdf(
        IReadOnlyList<ClassifiedFile> files,
        DocumentPageSettings settings,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
        {
            throw new ArgumentException("At least one image is required to generate a PDF.", nameof(files));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        try
        {
            progress?.Report(new ProcessingProgress(5, "Initializing PDF document…"));

            using var document = new PdfDocument();
            document.Info.Title = Path.GetFileNameWithoutExtension(outputPath);
            document.Info.Creator = "SmartShift Multi-Image Document Builder";

            // Parse background color
            var bgXColor = ParseXColor(settings.BackgroundHex);
            var bgBrush = new XSolidBrush(bgXColor);

            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[i];

                int pct = 10 + (int)(80.0 * (i + 1) / files.Count);
                progress?.Report(new ProcessingProgress(pct, $"Rendering page {i + 1} of {files.Count} ({file.FileName})…"));

                if (!File.Exists(file.Path))
                {
                    throw new FileNotFoundException($"Source image was not found: {file.Path}", file.Path);
                }

                using var decoded = SkiaImagePipeline.Decode(file.Path, cancellationToken);
                var workingBitmap = decoded.Bitmap;

                var layout = DocumentLayoutEngine.ComputePdfPageLayout(workingBitmap.Width, workingBitmap.Height, settings);

                var page = document.AddPage();
                page.Width = XUnit.FromPoint(layout.TargetWidth);
                page.Height = XUnit.FromPoint(layout.TargetHeight);

                using var gfx = XGraphics.FromPdfPage(page);

                // Draw background
                gfx.DrawRectangle(bgBrush, 0, 0, layout.TargetWidth, layout.TargetHeight);

                // Prepare image to draw
                SKBitmap? cropped = null;
                try
                {
                    SKBitmap bitmapToDraw = workingBitmap;
                    if (layout.SourceCropRect.HasValue)
                    {
                        var rect = layout.SourceCropRect.Value;
                        cropped = new SKBitmap(rect.Width, rect.Height, workingBitmap.ColorType, workingBitmap.AlphaType);
                        if (!workingBitmap.ExtractSubset(cropped, rect))
                        {
                            cropped.Dispose();
                            cropped = null;
                        }
                        else
                        {
                            bitmapToDraw = cropped;
                        }
                    }

                    using var ms = new MemoryStream();
                    bool hasAlpha = bitmapToDraw.AlphaType != SKAlphaType.Opaque;
                    var format = hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                    bitmapToDraw.Encode(ms, format, 95);
                    ms.Position = 0;

                    using var xImage = XImage.FromStream(ms);
                    gfx.DrawImage(xImage, layout.ImageX, layout.ImageY, layout.ImageWidth, layout.ImageHeight);
                }
                finally
                {
                    cropped?.Dispose();
                }
            }

            progress?.Report(new ProcessingProgress(90, "Writing PDF document to disk…"));
            cancellationToken.ThrowIfCancellationRequested();

            using (var fs = new FileStream(tempPartial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                document.Save(fs, false);
            }

            progress?.Report(new ProcessingProgress(95, "Validating generated PDF document…"));
            cancellationToken.ThrowIfCancellationRequested();

            // Validate PDF structure and page count
            using (var verifyDoc = PdfReader.Open(tempPartial, PdfDocumentOpenMode.Import))
            {
                if (verifyDoc.PageCount != files.Count)
                {
                    throw new InvalidOperationException($"PDF validation failed: Expected {files.Count} pages, but generated document has {verifyDoc.PageCount}.");
                }
            }

            // Atomic promotion
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(tempPartial, outputPath);

            progress?.Report(new ProcessingProgress(100, "PDF generation complete."));
            return outputPath;
        }
        catch
        {
            if (File.Exists(tempPartial))
            {
                try { File.Delete(tempPartial); } catch { }
            }
            throw;
        }
    }

    private static XColor ParseXColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return XColors.White;
        hex = hex.Trim().TrimStart('#');

        try
        {
            if (hex.Length == 6)
            {
                byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                return XColor.FromArgb(255, r, g, b);
            }
            else if (hex.Length == 8)
            {
                byte a = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte r = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                return XColor.FromArgb(a, r, g, b);
            }
        }
        catch { }

        return XColors.White;
    }
}
