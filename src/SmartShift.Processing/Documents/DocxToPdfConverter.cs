using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.IO;
using SmartShift.Processing;
using A = DocumentFormat.OpenXml.Drawing;

namespace SmartShift.Processing.Documents;

public sealed record DocxPdfConversionOptions(
    bool PreferLocalWordEngine = false,
    string PageSize = "A4",
    double MarginPoints = 45.0,
    bool IncludePageNumbers = true);

public static class DocxToPdfConverter
{
    private const double PointsPerInch = 72.0;

    public static string Convert(
        string sourceDocxPath,
        string outputPath,
        DocxPdfConversionOptions? options = null,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceDocxPath) || !File.Exists(sourceDocxPath))
        {
            throw new FileNotFoundException("Source Word document does not exist.", sourceDocxPath);
        }

        var sourceInfo = new FileInfo(sourceDocxPath);
        if (sourceInfo.Length == 0)
        {
            throw new InvalidOperationException("Source Word document is empty (0 bytes).");
        }

        options ??= new DocxPdfConversionOptions();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        // 1. If PreferLocalWordEngine is requested, try Word COM if available
        if (options.PreferLocalWordEngine)
        {
            try
            {
                if (TryConvertWithWordCom(sourceDocxPath, tempPartial, progress, cancellationToken))
                {
                    FinalizeAndValidate(tempPartial, outputPath, progress);
                    return outputPath;
                }
            }
            catch (Exception)
            {
                // Fall back cleanly to built-in OpenXML renderer
            }
        }

        // 2. Built-in OpenXML -> PDF renderer
        ConvertWithBuiltinEngine(sourceDocxPath, tempPartial, options, progress, cancellationToken);
        FinalizeAndValidate(tempPartial, outputPath, progress);

        return outputPath;
    }

    private static void FinalizeAndValidate(string tempPartial, string outputPath, IProgress<ProcessingProgress>? progress)
    {
        progress?.Report(new ProcessingProgress(95, "Validating output PDF integrity…"));
        if (!File.Exists(tempPartial) || new FileInfo(tempPartial).Length == 0)
        {
            throw new InvalidOperationException("Output PDF document could not be generated.");
        }

        using (var verifyStream = new FileStream(tempPartial, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var verifyDoc = PdfReader.Open(verifyStream, PdfDocumentOpenMode.Import))
        {
            if (verifyDoc.PageCount <= 0)
            {
                throw new InvalidOperationException("Generated PDF contains no pages.");
            }
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPartial, outputPath);
        progress?.Report(new ProcessingProgress(100, "DOCX to PDF conversion completed."));
    }

    private static bool TryConvertWithWordCom(
        string sourceDocxPath,
        string targetPdfPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return false;
        Type? wordType = Type.GetTypeFromProgID("Word.Application");
        if (wordType is null) return false;

        progress?.Report(new ProcessingProgress(20, "Launching local Word engine for native conversion…"));
        dynamic? wordApp = null;
        dynamic? doc = null;
        try
        {
            wordApp = Activator.CreateInstance(wordType);
            if (wordApp is null) return false;

            wordApp.Visible = false;
            wordApp.DisplayAlerts = 0; // wdAlertsNone

            progress?.Report(new ProcessingProgress(45, "Opening document in Word engine…"));
            doc = wordApp.Documents.Open(
                FileName: Path.GetFullPath(sourceDocxPath),
                ReadOnly: true,
                ConfirmConversions: false,
                AddToRecentFiles: false,
                Visible: false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProcessingProgress(75, "Exporting native PDF from Word…"));

            // 17 = wdExportFormatPDF
            doc.ExportAsFixedFormat(
                OutputFileName: Path.GetFullPath(targetPdfPath),
                ExportFormat: 17,
                OpenAfterExport: false,
                OptimizeFor: 0); // wdExportOptimizeForPrint

            return File.Exists(targetPdfPath) && new FileInfo(targetPdfPath).Length > 0;
        }
        finally
        {
            try { doc?.Close(SaveChanges: 0); } catch { }
            try { wordApp?.Quit(SaveChanges: 0); } catch { }
        }
    }

    private static void ConvertWithBuiltinEngine(
        string sourceDocxPath,
        string tempPartial,
        DocxPdfConversionOptions options,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ProcessingProgress(15, "Reading Word document structure…"));

        using var wordDoc = WordprocessingDocument.Open(sourceDocxPath, false);
        var body = wordDoc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            throw new InvalidOperationException("The Word document does not contain a valid document body.");
        }

        var mainPart = wordDoc.MainDocumentPart!;

        using var pdf = new PdfDocument();
        pdf.Info.Title = Path.GetFileNameWithoutExtension(sourceDocxPath);
        pdf.Info.Creator = "SmartShift Document Engine (OpenXML to PDF)";
        pdf.Options.CompressContentStreams = true;

        double pageWidth = 595.0; // A4 pt
        double pageHeight = 842.0;
        double margin = options.MarginPoints;
        double printableWidth = pageWidth - (2 * margin);
        double printableHeight = pageHeight - (2 * margin);

        var fontTitle = new XFont("Arial", 20, XFontStyleEx.Bold);
        var fontH1 = new XFont("Arial", 16, XFontStyleEx.Bold);
        var fontH2 = new XFont("Arial", 13, XFontStyleEx.Bold);
        var fontH3 = new XFont("Arial", 11.5, XFontStyleEx.Bold);
        var fontBody = new XFont("Arial", 10.5, XFontStyleEx.Regular);
        var fontBodyBold = new XFont("Arial", 10.5, XFontStyleEx.Bold);
        var fontBodyItalic = new XFont("Arial", 10.5, XFontStyleEx.Italic);
        var fontCaption = new XFont("Arial", 8.5, XFontStyleEx.Regular);

        var brushText = new XSolidBrush(XColor.FromArgb(20, 25, 35));
        var brushHeading = new XSolidBrush(XColor.FromArgb(15, 23, 42));
        var brushSubtext = new XSolidBrush(XColor.FromArgb(100, 116, 139));
        var penBorder = new XPen(XColor.FromArgb(203, 213, 225), 0.8);
        var penTable = new XPen(XColor.FromArgb(226, 232, 240), 0.6);
        var brushTableCell = new XSolidBrush(XColor.FromArgb(248, 250, 252));

        var pages = new List<PdfPage>();
        var gfxList = new List<XGraphics>();

        PdfPage CurrentPage()
        {
            if (pages.Count == 0)
            {
                var p = pdf.AddPage();
                p.Width = XUnit.FromPoint(pageWidth);
                p.Height = XUnit.FromPoint(pageHeight);
                pages.Add(p);
                gfxList.Add(XGraphics.FromPdfPage(p));
            }
            return pages[^1];
        }

        XGraphics CurrentGfx()
        {
            CurrentPage();
            return gfxList[^1];
        }

        double cursorY = margin;

        void CheckNewPage(double neededHeight)
        {
            if (cursorY + neededHeight > pageHeight - margin - 20)
            {
                var newPage = pdf.AddPage();
                newPage.Width = XUnit.FromPoint(pageWidth);
                newPage.Height = XUnit.FromPoint(pageHeight);
                pages.Add(newPage);
                gfxList.Add(XGraphics.FromPdfPage(newPage));
                cursorY = margin;
            }
        }

        var elements = body.ChildElements.ToList();
        int totalElements = elements.Count;

        for (int elemIdx = 0; elemIdx < totalElements; elemIdx++)
        {
            if (elemIdx % 10 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pct = 20 + (int)(65.0 * elemIdx / Math.Max(1, totalElements));
                progress?.Report(new ProcessingProgress(pct, $"Rendering document elements ({elemIdx + 1}/{totalElements})…"));
            }

            var element = elements[elemIdx];

            if (element is Paragraph para)
            {
                // Check if paragraph contains drawings / images
                var blips = para.Descendants<A.Blip>().ToList();
                if (blips.Count > 0)
                {
                    foreach (var blip in blips)
                    {
                        var embedId = blip.Embed?.Value;
                        if (!string.IsNullOrEmpty(embedId) && mainPart.TryGetPartById(embedId, out var part) && part is ImagePart imgPart)
                        {
                            try
                            {
                                using var imgStream = imgPart.GetStream(FileMode.Open, FileAccess.Read);
                                using var memStream = new MemoryStream();
                                imgStream.CopyTo(memStream);
                                memStream.Position = 0;

                                using var xImg = XImage.FromStream(memStream);
                                double maxImgWidth = Math.Min(printableWidth, 380);
                                double scale = Math.Min(1.0, maxImgWidth / xImg.PixelWidth);
                                double drawWidth = xImg.PixelWidth * scale;
                                double drawHeight = xImg.PixelHeight * scale;

                                if (drawHeight > printableHeight * 0.7)
                                {
                                    scale = (printableHeight * 0.7) / xImg.PixelHeight;
                                    drawWidth = xImg.PixelWidth * scale;
                                    drawHeight = xImg.PixelHeight * scale;
                                }

                                CheckNewPage(drawHeight + 16);
                                var gfx = CurrentGfx();
                                double imgX = margin + ((printableWidth - drawWidth) / 2.0);
                                gfx.DrawImage(xImg, imgX, cursorY, drawWidth, drawHeight);
                                cursorY += drawHeight + 14;
                            }
                            catch
                            {
                                // If image decoding fails, skip image safely
                            }
                        }
                    }
                }

                var text = para.InnerText?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Small paragraph spacing
                    cursorY += 6;
                    continue;
                }

                var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var (isHeading, level) = DetermineHeading(styleId);
                bool isBullet = para.ParagraphProperties?.NumberingProperties is not null;

                XFont currentFont;
                XBrush currentBrush;
                double lineHeight;
                double spaceBefore;
                double spaceAfter;
                double leftIndent = 0;

                if (isHeading)
                {
                    if (level == 1)
                    {
                        currentFont = fontTitle;
                        currentBrush = brushHeading;
                        lineHeight = 24;
                        spaceBefore = 14;
                        spaceAfter = 8;
                    }
                    else if (level == 2)
                    {
                        currentFont = fontH1;
                        currentBrush = brushHeading;
                        lineHeight = 20;
                        spaceBefore = 12;
                        spaceAfter = 6;
                    }
                    else if (level == 3)
                    {
                        currentFont = fontH2;
                        currentBrush = brushHeading;
                        lineHeight = 17;
                        spaceBefore = 10;
                        spaceAfter = 4;
                    }
                    else
                    {
                        currentFont = fontH3;
                        currentBrush = brushHeading;
                        lineHeight = 15;
                        spaceBefore = 8;
                        spaceAfter = 4;
                    }
                }
                else if (isBullet)
                {
                    currentFont = fontBody;
                    currentBrush = brushText;
                    lineHeight = 14.5;
                    spaceBefore = 2;
                    spaceAfter = 3;
                    leftIndent = 16;
                }
                else
                {
                    currentFont = fontBody;
                    currentBrush = brushText;
                    lineHeight = 14.5;
                    spaceBefore = 3;
                    spaceAfter = 5;
                }

                cursorY += spaceBefore;

                // Word wrap lines
                var lines = WrapText(CurrentGfx(), text, currentFont, printableWidth - leftIndent);
                double blockHeight = lines.Count * lineHeight;
                CheckNewPage(blockHeight);

                var activeGfx = CurrentGfx();

                if (isBullet)
                {
                    activeGfx.DrawString("•", currentFont, currentBrush, margin + 4, cursorY + (lineHeight * 0.75));
                }

                for (int l = 0; l < lines.Count; l++)
                {
                    CheckNewPage(lineHeight);
                    activeGfx = CurrentGfx();
                    activeGfx.DrawString(lines[l], currentFont, currentBrush, margin + leftIndent, cursorY + (lineHeight * 0.75));
                    cursorY += lineHeight;
                }

                cursorY += spaceAfter;
            }
            else if (element is Table table)
            {
                var rows = table.Elements<TableRow>().ToList();
                if (rows.Count == 0) continue;

                // Calculate column count
                int colCount = rows.Max(r => r.Elements<TableCell>().Count());
                if (colCount == 0) continue;

                double colWidth = printableWidth / colCount;
                cursorY += 8;

                bool isFirstRow = true;
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cells = row.Elements<TableCell>().ToList();
                    double rowHeight = 22;

                    // Measure text height in cells
                    foreach (var cell in cells)
                    {
                        var cellText = cell.InnerText?.Trim() ?? "";
                        var cellLines = WrapText(CurrentGfx(), cellText, fontBody, colWidth - 10);
                        double cellHeight = Math.Max(22, (cellLines.Count * 13) + 8);
                        if (cellHeight > rowHeight) rowHeight = cellHeight;
                    }

                    CheckNewPage(rowHeight);
                    var gfx = CurrentGfx();

                    for (int c = 0; c < colCount; c++)
                    {
                        double cellX = margin + (c * colWidth);
                        var cellRect = new XRect(cellX, cursorY, colWidth, rowHeight);

                        if (isFirstRow)
                        {
                            gfx.DrawRectangle(brushTableCell, cellRect);
                        }
                        gfx.DrawRectangle(penTable, cellRect);

                        if (c < cells.Count)
                        {
                            var cellText = cells[c].InnerText?.Trim() ?? "";
                            var cellFont = isFirstRow ? fontBodyBold : fontBody;
                            var cellLines = WrapText(gfx, cellText, cellFont, colWidth - 10);

                            double textY = cursorY + 6;
                            foreach (var line in cellLines)
                            {
                                gfx.DrawString(line, cellFont, brushText, cellX + 5, textY + 9);
                                textY += 13;
                            }
                        }
                    }

                    cursorY += rowHeight;
                    isFirstRow = false;
                }

                cursorY += 10;
            }
        }

        // Draw page numbers and footers across all pages
        if (options.IncludePageNumbers && pages.Count > 0)
        {
            for (int p = 0; p < pages.Count; p++)
            {
                var gfx = gfxList[p];
                string footerText = $"Page {p + 1} of {pages.Count}";
                gfx.DrawLine(penBorder, margin, pageHeight - margin + 8, pageWidth - margin, pageHeight - margin + 8);
                gfx.DrawString(footerText, fontCaption, brushSubtext,
                    new XRect(margin, pageHeight - margin + 12, printableWidth, 14),
                    XStringFormats.Center);
            }
        }

        // Ensure at least one page is added if doc was empty of body elements
        if (pages.Count == 0)
        {
            var p = pdf.AddPage();
            p.Width = XUnit.FromPoint(pageWidth);
            p.Height = XUnit.FromPoint(pageHeight);
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString("(Empty Document)", fontBody, brushSubtext, margin, margin + 20);
        }

        // Dispose gfx handles before saving
        foreach (var gfx in gfxList)
        {
            gfx.Dispose();
        }

        progress?.Report(new ProcessingProgress(90, "Writing PDF document stream…"));
        pdf.Save(tempPartial);
    }

    private static (bool IsHeading, int Level) DetermineHeading(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId)) return (false, 0);
        var norm = styleId.Replace(" ", "").ToLowerInvariant();
        if (norm.Contains("title")) return (true, 1);
        if (norm.Contains("subtitle")) return (true, 2);
        if (norm.StartsWith("heading") || norm.StartsWith("head"))
        {
            for (int i = 1; i <= 6; i++)
            {
                if (norm.Contains(i.ToString())) return (true, i + 1);
            }
            return (true, 2);
        }
        return (false, 0);
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        var words = text.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        var currentLine = "";

        foreach (var word in words)
        {
            var testLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            var size = gfx.MeasureString(testLine, font);

            if (size.Width <= maxWidth)
            {
                currentLine = testLine;
            }
            else
            {
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(currentLine);
                }
                currentLine = word;
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }

        return lines.Count > 0 ? lines : [text];
    }
}
