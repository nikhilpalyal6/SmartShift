using System.Text;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using UglyToad.PdfPig;

namespace SmartShift.Processing.PresentationStudio.Import;

public sealed class PdfImporter : IDocumentImporter
{
    private const int MaxFileSize = 100 * 1024 * 1024; // 100MB limit

    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf"];

    public bool CanImport(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ExtractedDocumentContent> ImportAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The source PDF file was not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"The PDF document exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB.");
        }

        return await Task.Run(() => ExtractFromPdf(filePath, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static ExtractedDocumentContent ExtractFromPdf(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Verify accessibility with Docnet (handles password & corrupt checks)
        int totalPages = 0;
        try
        {
            using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1.0));
            totalPages = docReader.GetPageCount();
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw new InvalidOperationException(
                "This PDF is password-protected or uses unsupported encryption. Encrypted PDFs cannot be imported.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The PDF file is corrupted or could not be read.", ex);
        }

        if (totalPages == 0)
        {
            throw new InvalidOperationException("The PDF document contains no pages.");
        }

        var sections = new List<ExtractedContentSection>();
        int totalChars = 0;
        var docTitle = Path.GetFileNameWithoutExtension(filePath);

        // 2. Extract text page-by-page using UglyToad.PdfPig for rich layout and character extraction
        try
        {
            using var pdfDoc = PdfDocument.Open(filePath);

            for (int pageNum = 1; pageNum <= pdfDoc.NumberOfPages; pageNum++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pdfDoc.GetPage(pageNum);
                var rawText = page.Text;

                if (string.IsNullOrWhiteSpace(rawText))
                {
                    continue;
                }

                totalChars += rawText.Length;

                // Process lines within the page
                var lines = rawText.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count == 0) continue;

                // Candidate heading for the page: first prominent line
                string pageHeading = $"Page {pageNum}";
                int headingIndex = 0;

                if (lines[0].Length <= 70 && !lines[0].EndsWith('.'))
                {
                    pageHeading = lines[0];
                    headingIndex = 1;
                    if (pageNum == 1 && docTitle == Path.GetFileNameWithoutExtension(filePath))
                    {
                        docTitle = pageHeading;
                    }
                }

                var pageBlocks = new List<ExtractedContentBlock>();
                var currentParagraph = new StringBuilder();

                void FlushPara()
                {
                    if (currentParagraph.Length > 0)
                    {
                        var text = currentParagraph.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            pageBlocks.Add(new ExtractedContentBlock(ContentBlockKind.Paragraph, text, pageNum));
                        }
                        currentParagraph.Clear();
                    }
                }

                for (int i = headingIndex; i < lines.Count; i++)
                {
                    var line = lines[i];

                    // Bullet heuristic
                    if (line.StartsWith("• ") || line.StartsWith("- ") || line.StartsWith("* "))
                    {
                        FlushPara();
                        pageBlocks.Add(new ExtractedContentBlock(ContentBlockKind.BulletItem, line[2..].Trim(), pageNum));
                        continue;
                    }

                    // Numbered heuristic
                    if (line.Length > 2 && char.IsDigit(line[0]) && (line[1] == '.' || line[1] == ')'))
                    {
                        FlushPara();
                        pageBlocks.Add(new ExtractedContentBlock(ContentBlockKind.NumberedItem, line[2..].Trim(), pageNum));
                        continue;
                    }

                    if (currentParagraph.Length > 0)
                    {
                        currentParagraph.Append(' ');
                    }
                    currentParagraph.Append(line);
                }

                FlushPara();

                sections.Add(new ExtractedContentSection(
                    Heading: pageHeading,
                    Level: 1,
                    Blocks: pageBlocks,
                    SourcePageNumber: pageNum));
            }
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException ex)
        {
            throw new InvalidOperationException(
                "This PDF is password-protected or encrypted. Encrypted PDFs cannot be imported.",
                ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to extract content from PDF: {ex.Message}", ex);
        }

        // 3. Detect scanned PDF with no extractable text
        if (totalChars == 0)
        {
            return new ExtractedDocumentContent(
                SourceFilePath: filePath,
                DocumentTitle: docTitle,
                Sections: [],
                TotalCharacters: 0,
                HasScannedContentOnly: true,
                WarningMessage: "This PDF appears to be a scanned document containing only raster images. Text extraction requires Optical Character Recognition (OCR), which is not currently active.");
        }

        return new ExtractedDocumentContent(
            SourceFilePath: filePath,
            DocumentTitle: docTitle,
            Sections: sections,
            TotalCharacters: totalChars);
    }
}

