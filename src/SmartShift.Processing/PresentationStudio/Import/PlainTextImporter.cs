using System.Text;

namespace SmartShift.Processing.PresentationStudio.Import;

public sealed class PlainTextImporter : IDocumentImporter
{
    private const int MaxFileSize = 15 * 1024 * 1024; // 15MB safety limit for presentations

    public IReadOnlyList<string> SupportedExtensions { get; } = [".txt", ".text"];

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
            throw new FileNotFoundException("The source text file was not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The source text file is empty (0 bytes).");
        }

        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"The file exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB.");
        }

        var text = await ReadTextWithEncodingDetectionAsync(filePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return ParseTextContent(filePath, text);
    }

    private static async Task<string> ReadTextWithEncodingDetectionAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) return string.Empty;

        // Detect BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        }

        // Try UTF-8 strict validation
        try
        {
            var utf8Strict = new UTF8Encoding(false, true);
            return utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Fallback to ANSI / Latin1 (ISO-8859-1)
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static ExtractedDocumentContent ParseTextContent(string filePath, string text)
    {
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var docTitle = Path.GetFileNameWithoutExtension(filePath);
        var sections = new List<ExtractedContentSection>();

        string? currentHeading = null;
        int currentHeadingLevel = 1;
        var currentBlocks = new List<ExtractedContentBlock>();
        var paragraphBuffer = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraphBuffer.Length > 0)
            {
                var p = paragraphBuffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(p))
                {
                    currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.Paragraph, p));
                }
                paragraphBuffer.Clear();
            }
        }

        void FlushSection()
        {
            FlushParagraph();
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

        for (int i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].TrimEnd();
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                continue;
            }

            // Check for underline style heading (=== or ---)
            if (i + 1 < rawLines.Length)
            {
                var nextLine = rawLines[i + 1].Trim();
                if (nextLine.Length >= 3 && (nextLine.All(c => c == '=') || nextLine.All(c => c == '-')))
                {
                    FlushSection();
                    currentHeading = trimmed;
                    currentHeadingLevel = nextLine.StartsWith('=') ? 1 : 2;
                    if (sections.Count == 0 && string.IsNullOrWhiteSpace(docTitle))
                    {
                        docTitle = trimmed;
                    }
                    i++; // skip the underline
                    continue;
                }
            }

            // Check for bullet list item
            if (IsBulletLine(trimmed, out var bulletText))
            {
                FlushParagraph();
                currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.BulletItem, bulletText));
                continue;
            }

            // Check for numbered list item
            if (IsNumberedLine(trimmed, out var numText))
            {
                FlushParagraph();
                currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.NumberedItem, numText));
                continue;
            }

            // Check for heading heuristic: short line, uppercase or ending in colon, or "Chapter X:" / "Section X:"
            if (IsHeadingCandidate(trimmed, line.Length == trimmed.Length))
            {
                FlushSection();
                currentHeading = trimmed.TrimEnd(':');
                currentHeadingLevel = 1;
                if (sections.Count == 0 && docTitle == Path.GetFileNameWithoutExtension(filePath))
                {
                    docTitle = currentHeading;
                }
                continue;
            }

            // Regular paragraph line
            if (paragraphBuffer.Length > 0)
            {
                paragraphBuffer.Append(' ');
            }
            paragraphBuffer.Append(trimmed);
        }

        FlushSection();

        int totalChars = sections.Sum(s =>
            (s.Heading?.Length ?? 0) + s.Blocks.Sum(b => b.Text.Length));

        return new ExtractedDocumentContent(
            SourceFilePath: filePath,
            DocumentTitle: docTitle,
            Sections: sections,
            TotalCharacters: totalChars);
    }

    private static bool IsBulletLine(string line, out string content)
    {
        string[] prefixes = ["* ", "- ", "+ ", "• ", "◦ ", "▪ "];
        foreach (var p in prefixes)
        {
            if (line.StartsWith(p, StringComparison.Ordinal))
            {
                content = line[p.Length..].Trim();
                return true;
            }
        }
        content = string.Empty;
        return false;
    }

    private static bool IsNumberedLine(string line, out string content)
    {
        content = string.Empty;
        int dotIndex = line.IndexOf('.');
        if (dotIndex > 0 && dotIndex <= 3 && char.IsDigit(line[0]))
        {
            if (int.TryParse(line[..dotIndex], out _))
            {
                content = line[(dotIndex + 1)..].Trim();
                return true;
            }
        }
        int parenIndex = line.IndexOf(')');
        if (parenIndex > 0 && parenIndex <= 3 && char.IsDigit(line[0]))
        {
            if (int.TryParse(line[..parenIndex], out _))
            {
                content = line[(parenIndex + 1)..].Trim();
                return true;
            }
        }
        return false;
    }

    private static bool IsHeadingCandidate(string trimmed, bool isNotIndented)
    {
        if (!isNotIndented) return false;
        if (trimmed.Length < 3 || trimmed.Length > 80) return false;

        if (trimmed.EndsWith(':')) return true;

        if (trimmed.Length <= 50 && trimmed.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return true;
        }

        if (trimmed.StartsWith("Section ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Part ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

