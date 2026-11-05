using System.Text;
using System.Text.RegularExpressions;

namespace SmartShift.Processing.PresentationStudio.Import;

/// <summary>
/// Deterministic Markdown importer. Supports:
/// - ATX headings (# through ######)
/// - Setext headings (underlined with = or -)
/// - Unordered bullet lists (*, -, +)
/// - Ordered lists (1., 2., etc.)
/// - Paragraphs and line breaks
/// - Fenced code blocks (converted to code-formatted text blocks)
/// - Markdown image references (![alt](path))
/// 
/// Unsupported features:
/// - Raw HTML/JS execution (sanitized/stripped for security)
/// - Complex nested tables with merged cells (parsed as textual paragraphs)
/// - Embedded CSS / mathjax scripts
/// </summary>
public sealed class MarkdownImporter : IDocumentImporter
{
    private const int MaxFileSize = 15 * 1024 * 1024; // 15MB limit
    private static readonly Regex HtmlTagRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(@"!\[(.*?)\]\((.*?)\)", RegexOptions.Compiled);

    public IReadOnlyList<string> SupportedExtensions { get; } = [".md", ".markdown", ".mdown"];

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
            throw new FileNotFoundException("The source Markdown file was not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The source Markdown file is empty (0 bytes).");
        }

        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"The file exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB.");
        }

        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return ParseMarkdown(filePath, text);
    }

    private static ExtractedDocumentContent ParseMarkdown(string filePath, string text)
    {
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var docTitle = Path.GetFileNameWithoutExtension(filePath);
        var sections = new List<ExtractedContentSection>();

        string? currentHeading = null;
        int currentHeadingLevel = 1;
        var currentBlocks = new List<ExtractedContentBlock>();
        var paragraphBuffer = new StringBuilder();
        bool inCodeBlock = false;
        var codeBlockBuffer = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraphBuffer.Length > 0)
            {
                var p = Sanitize(paragraphBuffer.ToString().Trim());
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

            // Handle code block fences
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    // closing code block
                    var codeText = codeBlockBuffer.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(codeText))
                    {
                        currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.CodeBlock, codeText));
                    }
                    codeBlockBuffer.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    FlushParagraph();
                    inCodeBlock = true;
                    codeBlockBuffer.Clear();
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                continue;
            }

            // ATX headings (# Heading)
            if (trimmed.StartsWith('#'))
            {
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#')
                {
                    hashCount++;
                }

                if (hashCount <= 6 && (hashCount == trimmed.Length || char.IsWhiteSpace(trimmed[hashCount])))
                {
                    FlushSection();
                    var headingText = Sanitize(trimmed[hashCount..].Trim());
                    currentHeading = headingText;
                    currentHeadingLevel = hashCount;

                    if (hashCount == 1 && (sections.Count == 0 || docTitle == Path.GetFileNameWithoutExtension(filePath)))
                    {
                        docTitle = headingText;
                    }
                    continue;
                }
            }

            // Setext headings (Heading\n=== or Heading\n---)
            if (i + 1 < rawLines.Length)
            {
                var nextLine = rawLines[i + 1].Trim();
                if (nextLine.Length >= 3 && (nextLine.All(c => c == '=') || nextLine.All(c => c == '-')))
                {
                    FlushSection();
                    var headingText = Sanitize(trimmed);
                    currentHeading = headingText;
                    currentHeadingLevel = nextLine.StartsWith('=') ? 1 : 2;

                    if (currentHeadingLevel == 1 && (sections.Count == 0 || docTitle == Path.GetFileNameWithoutExtension(filePath)))
                    {
                        docTitle = headingText;
                    }
                    i++; // skip underline
                    continue;
                }
            }

            // Check for image syntax: ![alt](path)
            var imgMatch = ImageRegex.Match(trimmed);
            if (imgMatch.Success && imgMatch.Index == 0)
            {
                FlushParagraph();
                var alt = imgMatch.Groups[1].Value;
                var imgPath = imgMatch.Groups[2].Value;
                currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.ImageRef, imgPath));
                continue;
            }

            // Bullet lists (*, -, +)
            if (IsMarkdownBullet(trimmed, out var bulletText))
            {
                FlushParagraph();
                currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.BulletItem, Sanitize(bulletText)));
                continue;
            }

            // Numbered lists (1., 2.)
            if (IsMarkdownNumber(trimmed, out var numText))
            {
                FlushParagraph();
                currentBlocks.Add(new ExtractedContentBlock(ContentBlockKind.NumberedItem, Sanitize(numText)));
                continue;
            }

            // Paragraph continuation
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

    private static bool IsMarkdownBullet(string line, out string content)
    {
        string[] prefixes = ["* ", "- ", "+ "];
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

    private static bool IsMarkdownNumber(string line, out string content)
    {
        content = string.Empty;
        int dot = line.IndexOf('.');
        if (dot > 0 && dot <= 3 && char.IsDigit(line[0]))
        {
            if (int.TryParse(line[..dot], out _))
            {
                content = line[(dot + 1)..].Trim();
                return true;
            }
        }
        return false;
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // Strip HTML tags to avoid executable or malicious injection
        var stripped = HtmlTagRegex.Replace(input, string.Empty);
        // Strip basic markdown bold/italic formatting marks while keeping text
        stripped = stripped.Replace("**", "").Replace("__", "");
        return stripped.Trim();
    }
}

