using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SmartShift.Processing.PresentationStudio.Import;

public sealed class DocxImporter : IDocumentImporter
{
    private const int MaxFileSize = 50 * 1024 * 1024; // 50MB limit

    public IReadOnlyList<string> SupportedExtensions { get; } = [".docx"];

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
            throw new FileNotFoundException("The source Word document was not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The source Word document is empty (0 bytes).");
        }

        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"The Word document exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB.");
        }

        return await Task.Run(() => ExtractFromDocx(filePath, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static ExtractedDocumentContent ExtractFromDocx(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WordprocessingDocument package;
        try
        {
            package = WordprocessingDocument.Open(filePath, false);
        }
        catch (OpenXmlPackageException ex)
        {
            throw new InvalidOperationException("The Word document is corrupted or not a valid DOCX package.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to open Word document: {ex.Message}", ex);
        }

        using (package)
        {
            var mainPart = package.MainDocumentPart;
            if (mainPart?.Document?.Body is null)
            {
                throw new InvalidOperationException("The Word document contains no document body.");
            }

            var docTitle = Path.GetFileNameWithoutExtension(filePath);
            var sections = new List<ExtractedContentSection>();

            string? currentHeading = null;
            int currentHeadingLevel = 1;
            var currentBlocks = new List<ExtractedContentBlock>();

            void FlushSection()
            {
                if (currentBlocks.Count > 0 || !string.IsNullOrWhiteSpace(currentHeading))
                {
                    sections.Add(new ExtractedContentSection(
                        currentHeading,
                        currentHeadingLevel,
                        currentBlocks.ToList()));
                    currentBlocks.Clear();
                    currentHeading = null;
                    currentHeadingLevel = 1;
                }
            }

            // Read all paragraph and table elements in reading order
            foreach (var element in mainPart.Document.Body.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (element is Paragraph para)
                {
                    var text = para.InnerText?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var (isHeading, level) = DetermineHeading(styleId);

                    if (isHeading)
                    {
                        FlushSection();
                        currentHeading = text;
                        currentHeadingLevel = level;

                        if (level == 1 && (sections.Count == 0 || docTitle == Path.GetFileNameWithoutExtension(filePath)))
                        {
                            docTitle = text;
                        }
                    }
                    else if (para.ParagraphProperties?.NumberingProperties is not null)
                    {
                        // List item (bullet or numbered)
                        currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.BulletItem, text));
                    }
                    else
                    {
                        // Standard paragraph
                        currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.Paragraph, text));
                    }
                }
                else if (element is Table table)
                {
                    // Convert table rows into readable paragraph/bullet representations
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cellTexts = row.Elements<TableCell>()
                            .Select(c => c.InnerText?.Trim())
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .ToList();

                        if (cellTexts.Count > 0)
                        {
                            currentBlocks.Add(new ExtractedContentBlock(
                                ContentBlockKind.Paragraph,
                                string.Join(" | ", cellTexts)));
                        }
                    }
                }
            }

            FlushSection();

            int totalChars = sections.Sum(s =>
                (s.Heading?.Length ?? 0) + s.Blocks.Sum(b => b.Text.Length));

            if (totalChars == 0)
            {
                throw new InvalidOperationException("The Word document contains no readable text content.");
            }

            return new ExtractedDocumentContent(
                SourceFilePath: filePath,
                DocumentTitle: docTitle,
                Sections: sections,
                TotalCharacters: totalChars);
        }
    }

    private static (bool IsHeading, int Level) DetermineHeading(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId)) return (false, 0);

        var normalized = styleId.Replace(" ", "").ToLowerInvariant();

        if (normalized.Contains("title"))
        {
            return (true, 1);
        }

        if (normalized.StartsWith("heading") || normalized.StartsWith("head"))
        {
            for (int i = 1; i <= 6; i++)
            {
                if (normalized.Contains(i.ToString()))
                {
                    return (true, i);
                }
            }
            return (true, 1);
        }

        return (false, 0);
    }
}

