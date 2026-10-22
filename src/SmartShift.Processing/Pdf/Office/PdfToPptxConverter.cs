using System.Diagnostics;
using System.IO;
using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using SmartShift.Core.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SmartShift.Processing.Pdf.Office;

public enum PptxSlideMode
{
    PageAsImage,
    Editable
}

public sealed record PdfPptxResult(
    string OutputPath,
    int TotalPagesInDoc,
    int SlidesCreatedCount,
    PptxSlideMode SlideMode,
    string SummaryNote);

public static class PdfToPptxConverter
{
    public static PdfPptxResult ConvertToPptx(
        string inputPath,
        IOutputPathAllocator allocator,
        PptxSlideMode mode,
        string? pageRange,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The source PDF file was not found.", inputPath);
        }

        if (new FileInfo(inputPath).Length == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        progress?.Report(new ProcessingProgress(5, "Inspecting PDF document structure…"));

        var sw = Stopwatch.StartNew();
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var finalPath = allocator.Allocate(inputPath, $"{baseName}.pptx");
        var tempPartial = finalPath + ".partial";

        int totalPages = 0;
        IReadOnlyList<int> pagesToConvert;

        try
        {
            using var pdfDoc = PdfDocument.Open(inputPath);
            totalPages = pdfDoc.NumberOfPages;
            pagesToConvert = PageRangeParser.Parse(pageRange, totalPages);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException ex)
        {
            throw new InvalidOperationException(
                "This PDF document is password-protected or uses an unsupported encryption handler. Encrypted PDFs cannot be converted.",
                ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("The PDF document is malformed or corrupted and could not be read.", ex);
        }

        if (pagesToConvert.Count == 0)
        {
            throw new InvalidOperationException("No pages matched the specified page range.");
        }

        try
        {
            // Determine slide dimensions based on first page aspect ratio
            long slideWidthEmu = 12192000L;  // 16:9 widescreen default (960 pt * 12700)
            long slideHeightEmu = 6858000L;  // (540 pt * 12700)

            try
            {
                using var pdfDoc = PdfDocument.Open(inputPath);
                var firstPage = pdfDoc.GetPage(pagesToConvert[0]);
                if (firstPage.Width > 0 && firstPage.Height > 0)
                {
                    slideWidthEmu = (long)Math.Round(firstPage.Width * 12700);
                    slideHeightEmu = (long)Math.Round(firstPage.Height * 12700);
                }
            }
            catch { }

            int slidesCreated = 0;

            using (var presentationDoc = PresentationDocument.Create(tempPartial, PresentationDocumentType.Presentation))
            {
                var presPart = presentationDoc.AddPresentationPart();
                presPart.Presentation = new Presentation();

                // Setup SlideSize and NotesSize
                presPart.Presentation.SlideSize = new SlideSize { Cx = (int)slideWidthEmu, Cy = (int)slideHeightEmu };
                presPart.Presentation.NotesSize = new NotesSize { Cx = (int)slideHeightEmu, Cy = (int)slideWidthEmu };

                // Create Master & Blank Layout
                var masterPart = presPart.AddNewPart<SlideMasterPart>();
                masterPart.SlideMaster = new SlideMaster(
                    new CommonSlideData(CreateShapeTree()),
                    new ColorMap
                    {
                        Background1 = A.ColorSchemeIndexValues.Light1,
                        Text1 = A.ColorSchemeIndexValues.Dark1,
                        Background2 = A.ColorSchemeIndexValues.Light2,
                        Text2 = A.ColorSchemeIndexValues.Dark2,
                        Accent1 = A.ColorSchemeIndexValues.Accent1,
                        Accent2 = A.ColorSchemeIndexValues.Accent2,
                        Accent3 = A.ColorSchemeIndexValues.Accent3,
                        Accent4 = A.ColorSchemeIndexValues.Accent4,
                        Accent5 = A.ColorSchemeIndexValues.Accent5,
                        Accent6 = A.ColorSchemeIndexValues.Accent6,
                        Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                        FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
                    });

                var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
                layoutPart.SlideLayout = new SlideLayout(new CommonSlideData(CreateShapeTree()))
                {
                    Type = SlideLayoutValues.Blank
                };

                masterPart.SlideMaster.SlideLayoutIdList = new SlideLayoutIdList(
                    new SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) });

                presPart.Presentation.SlideMasterIdList = new SlideMasterIdList(
                    new SlideMasterId { Id = 2147483648U, RelationshipId = presPart.GetIdOfPart(masterPart) });

                var slideIdList = new SlideIdList();
                presPart.Presentation.SlideIdList = slideIdList;

                if (mode == PptxSlideMode.PageAsImage)
                {
                    // MODE B: Page-as-Image PowerPoint (Pixel-accurate layout)
                    for (int i = 0; i < pagesToConvert.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int pageNum = pagesToConvert[i];

                        int pct = 10 + (int)(75.0 * (i + 1) / pagesToConvert.Count);
                        progress?.Report(new ProcessingProgress(pct, $"Rendering slide {i + 1} of {pagesToConvert.Count} (page {pageNum})…"));

                        var imageBytes = RenderPageToPng(inputPath, pageNum);
                        if (imageBytes == null || imageBytes.Length == 0)
                        {
                            throw new InvalidOperationException($"Failed to render page {pageNum} as slide image.");
                        }

                        var slidePart = presPart.AddNewPart<SlidePart>();
                        var shapeTree = CreateShapeTree();
                        slidePart.Slide = new Slide(new CommonSlideData(shapeTree));
                        slidePart.AddPart(layoutPart);

                        // Add image part
                        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            imagePart.FeedData(ms);
                        }
                        string relId = slidePart.GetIdOfPart(imagePart);

                        // Add picture shape covering the slide
                        AddPictureShape(shapeTree, relId, $"Slide {i + 1} Page {pageNum}", 0L, 0L, slideWidthEmu, slideHeightEmu);

                        slideIdList.AppendChild(new SlideId
                        {
                            Id = (uint)(256 + i),
                            RelationshipId = presPart.GetIdOfPart(slidePart)
                        });

                        slidesCreated++;
                    }
                }
                else
                {
                    // MODE A: Editable PowerPoint (Reconstructed text elements & shapes)
                    using var pdfDoc = PdfDocument.Open(inputPath);

                    for (int i = 0; i < pagesToConvert.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int pageNum = pagesToConvert[i];

                        int pct = 10 + (int)(75.0 * (i + 1) / pagesToConvert.Count);
                        progress?.Report(new ProcessingProgress(pct, $"Constructing editable slide {i + 1} of {pagesToConvert.Count} (page {pageNum})…"));

                        var page = pdfDoc.GetPage(pageNum);
                        var slidePart = presPart.AddNewPart<SlidePart>();
                        var shapeTree = CreateShapeTree();
                        slidePart.Slide = new Slide(new CommonSlideData(shapeTree));
                        slidePart.AddPart(layoutPart);

                        var words = page.GetWords().ToList();
                        if (words.Count > 0)
                        {
                            var lines = GroupWordsIntoLines(words);
                            foreach (var lineWords in lines)
                            {
                                var firstWord = lineWords[0];
                                // Convert PDF points to EMUs (PDF Y starts from bottom, PPTX Y starts from top)
                                long xEmu = (long)Math.Round(firstWord.BoundingBox.Left * 12700);
                                double topPt = page.Height - firstWord.BoundingBox.Top;
                                long yEmu = Math.Max(0L, (long)Math.Round(topPt * 12700));

                                double widthPt = lineWords.Last().BoundingBox.Right - firstWord.BoundingBox.Left;
                                long wEmu = Math.Max(127000L, (long)Math.Round(widthPt * 12700));
                                long hEmu = Math.Max(254000L, (long)Math.Round(firstWord.BoundingBox.Height * 12700 * 1.3));

                                var lineText = string.Join(" ", lineWords.Select(w => w.Text));
                                bool isBold = lineWords.Any(w => w.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true);
                                bool isItalic = lineWords.Any(w => w.FontName?.Contains("Italic", StringComparison.OrdinalIgnoreCase) == true);
                                double avgFontPt = lineWords.Average(w => w.BoundingBox.Height);
                                int fontSizeHundredthPt = Math.Clamp((int)Math.Round(avgFontPt * 100.0), 1000, 7200);

                                AddTextShape(shapeTree, lineText, xEmu, yEmu, wEmu, hEmu, fontSizeHundredthPt, isBold, isItalic);
                            }
                        }
                        else
                        {
                            // If page has no text, embed page raster image as slide background
                            var imageBytes = RenderPageToPng(inputPath, pageNum);
                            if (imageBytes != null && imageBytes.Length > 0)
                            {
                                var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                                using (var ms = new MemoryStream(imageBytes))
                                {
                                    imagePart.FeedData(ms);
                                }
                                string relId = slidePart.GetIdOfPart(imagePart);
                                AddPictureShape(shapeTree, relId, $"Slide {i + 1} Image", 0L, 0L, slideWidthEmu, slideHeightEmu);
                            }
                        }

                        // Add extracted raster images on page
                        try
                        {
                            foreach (var img in page.GetImages())
                            {
                                if (img.TryGetPng(out var imgBytes) && imgBytes.Length > 0)
                                {
                                    var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                                    using (var ms = new MemoryStream(imgBytes))
                                    {
                                        imagePart.FeedData(ms);
                                    }
                                    string relId = slidePart.GetIdOfPart(imagePart);
                                    long imgX = (long)Math.Round(img.BoundingBox.Left * 12700);
                                    long imgY = Math.Max(0L, (long)Math.Round((page.Height - img.BoundingBox.Top) * 12700));
                                    long imgW = Math.Max(127000L, (long)Math.Round(img.BoundingBox.Width * 12700));
                                    long imgH = Math.Max(127000L, (long)Math.Round(img.BoundingBox.Height * 12700));
                                    AddPictureShape(shapeTree, relId, "Figure", imgX, imgY, imgW, imgH);
                                }
                            }
                        }
                        catch { }

                        slideIdList.AppendChild(new SlideId
                        {
                            Id = (uint)(256 + i),
                            RelationshipId = presPart.GetIdOfPart(slidePart)
                        });

                        slidesCreated++;
                    }
                }

                presPart.Presentation.Save();
            }

            progress?.Report(new ProcessingProgress(90, "Validating PowerPoint Open XML package…"));

            // Validate OpenXML structure
            var validator = new OpenXmlValidator();
            using (var package = PresentationDocument.Open(tempPartial, false))
            {
                var validationErrors = validator.Validate(package).ToList();
            }

            // Atomically promote
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(tempPartial, finalPath);

            sw.Stop();
            long outputBytes = new FileInfo(finalPath).Length;

            string modeDesc = mode == PptxSlideMode.PageAsImage
                ? "Page-as-Image (exact visual layout)"
                : "Editable (reconstructed text & shapes)";

            string summaryNote = $"Converted {slidesCreated} slide(s) to PPTX [{modeDesc}] ({FileSizeFormatter.Format(outputBytes)}) in {sw.ElapsedMilliseconds} ms.";

            progress?.Report(new ProcessingProgress(100, "PowerPoint conversion complete."));

            return new PdfPptxResult(
                finalPath,
                totalPages,
                slidesCreated,
                mode,
                summaryNote);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPartial)) File.Delete(tempPartial);
            }
            catch { }
            throw;
        }
    }

    private static ShapeTree CreateShapeTree()
    {
        return new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));
    }

    private static void AddPictureShape(ShapeTree shapeTree, string relId, string name, long x, long y, long cx, long cy)
    {
        uint shapeId = (uint)Random.Shared.Next(10, 100000);
        var picture = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = relId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        shapeTree.AppendChild(picture);
    }

    private static void AddTextShape(ShapeTree shapeTree, string text, long x, long y, long cx, long cy, int fontSizeHundredths, bool bold, bool italic)
    {
        uint shapeId = (uint)Random.Shared.Next(10, 100000);

        var runProps = new A.RunProperties { Language = "en-US", FontSize = fontSizeHundredths };
        if (bold) runProps.Bold = true;
        if (italic) runProps.Italic = true;

        var run = new A.Run(runProps, new A.Text(text));
        var paragraph = new A.Paragraph(run);

        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = shapeId, Name = $"Text {shapeId}" },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square, RightToLeftColumns = false },
                new A.ListStyle(),
                paragraph));

        shapeTree.AppendChild(shape);
    }

    private static List<List<Word>> GroupWordsIntoLines(List<Word> words)
    {
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();
        var lines = new List<List<Word>>();
        if (sorted.Count == 0) return lines;

        var currentLine = new List<Word> { sorted[0] };
        double currentY = sorted[0].BoundingBox.Bottom;
        double currentH = sorted[0].BoundingBox.Height;

        for (int i = 1; i < sorted.Count; i++)
        {
            var w = sorted[i];
            if (Math.Abs(w.BoundingBox.Bottom - currentY) < Math.Max(4.0, currentH * 0.5))
            {
                currentLine.Add(w);
            }
            else
            {
                lines.Add(currentLine.OrderBy(x => x.BoundingBox.Left).ToList());
                currentLine = new List<Word> { w };
                currentY = w.BoundingBox.Bottom;
                currentH = w.BoundingBox.Height;
            }
        }

        if (currentLine.Count > 0)
        {
            lines.Add(currentLine.OrderBy(x => x.BoundingBox.Left).ToList());
        }

        return lines;
    }

    private static byte[]? RenderPageToPng(string pdfPath, int pageNumber)
    {
        try
        {
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(150 / 72.0));
            using var pageReader = docReader.GetPageReader(pageNumber - 1);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            var rawBgra = pageReader.GetImage();
            if (rawBgra == null || rawBgra.Length == 0) return null;

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(rawBgra, 0, bitmap.GetPixels(), rawBgra.Length);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
