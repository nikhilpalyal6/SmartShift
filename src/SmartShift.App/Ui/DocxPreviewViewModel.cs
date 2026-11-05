using System.Windows;
using SmartShift.Processing.PresentationStudio.Import;

namespace SmartShift.App.Ui;

public sealed record DocxPreviewBlockItem(
    ContentBlockKind Kind,
    string Text,
    bool IsBullet)
{
    public Visibility BulletVisibility => IsBullet ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record DocxPreviewSectionItem(
    string? Heading,
    int HeadingLevel,
    bool HasHeading,
    IReadOnlyList<DocxPreviewBlockItem> Blocks)
{
    public Visibility HeadingVisibility => HasHeading ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class DocxPreviewViewModel
{
    public DocxPreviewViewModel(ExtractedDocumentContent content)
    {
        DocumentTitle = string.IsNullOrWhiteSpace(content.DocumentTitle) ? "Untitled Document" : content.DocumentTitle;
        TotalCharacters = content.TotalCharacters;
        TotalSections = content.Sections.Count;
        TotalHeadings = content.Sections.Count(s => !string.IsNullOrWhiteSpace(s.Heading));

        var wordCount = 0;
        var sectionList = new List<DocxPreviewSectionItem>();

        foreach (var sec in content.Sections)
        {
            var blocks = new List<DocxPreviewBlockItem>();
            if (!string.IsNullOrWhiteSpace(sec.Heading))
            {
                wordCount += CountWords(sec.Heading);
            }

            foreach (var b in sec.Blocks)
            {
                wordCount += CountWords(b.Text);
                blocks.Add(new DocxPreviewBlockItem(b.Kind, b.Text, b.Kind == ContentBlockKind.BulletItem));
            }

            sectionList.Add(new DocxPreviewSectionItem(
                sec.Heading,
                sec.Level,
                !string.IsNullOrWhiteSpace(sec.Heading),
                blocks));
        }

        TotalWords = wordCount;
        Sections = sectionList;
    }

    public string DocumentTitle { get; }
    public int TotalWords { get; }
    public int TotalCharacters { get; }
    public int TotalSections { get; }
    public int TotalHeadings { get; }
    public IReadOnlyList<DocxPreviewSectionItem> Sections { get; }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
