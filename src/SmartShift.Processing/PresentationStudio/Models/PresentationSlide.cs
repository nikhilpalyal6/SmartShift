namespace SmartShift.Processing.PresentationStudio.Models;

public sealed class PresentationSlide
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public PresentationLayoutType Layout { get; set; } = PresentationLayoutType.TitleAndContent;
    public List<SlideTextBlock> TextBlocks { get; set; } = [];
    public SlideImageReference? Image { get; set; }
    public string? SpeakerNotes { get; set; }
    public string? SourceProvenance { get; set; }
    public int? SourcePageNumber { get; set; }

    /// <summary>
    /// Helper to get or set primary bullets as a list of strings
    /// </summary>
    public List<string> PrimaryBullets
    {
        get
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 0 && b.Kind == SlideTextBlockKind.BulletList);
            return block?.Lines ?? [];
        }
        set
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 0 && b.Kind == SlideTextBlockKind.BulletList);
            if (block is null)
            {
                block = new SlideTextBlock { Kind = SlideTextBlockKind.BulletList, ColumnIndex = 0 };
                TextBlocks.Add(block);
            }
            block.Lines = value;
        }
    }

    /// <summary>
    /// Helper to get or set primary paragraph text
    /// </summary>
    public string? PrimaryParagraph
    {
        get
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 0 && b.Kind == SlideTextBlockKind.Paragraph);
            return block is not null && block.Lines.Count > 0 ? string.Join(Environment.NewLine, block.Lines) : null;
        }
        set
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 0 && b.Kind == SlideTextBlockKind.Paragraph);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (block is not null) TextBlocks.Remove(block);
            }
            else
            {
                if (block is null)
                {
                    block = new SlideTextBlock { Kind = SlideTextBlockKind.Paragraph, ColumnIndex = 0 };
                    TextBlocks.Add(block);
                }
                block.Lines = [value];
            }
        }
    }

    /// <summary>
    /// Helper to get or set secondary column bullets (for TwoColumn layout)
    /// </summary>
    public List<string> SecondaryBullets
    {
        get
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 1);
            return block?.Lines ?? [];
        }
        set
        {
            var block = TextBlocks.FirstOrDefault(b => b.ColumnIndex == 1);
            if (block is null)
            {
                block = new SlideTextBlock { Kind = SlideTextBlockKind.BulletList, ColumnIndex = 1 };
                TextBlocks.Add(block);
            }
            block.Lines = value;
        }
    }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) &&
        TextBlocks.All(b => b.Lines.Count == 0 || b.Lines.All(string.IsNullOrWhiteSpace)) &&
        Image is null;

    /// <summary>
    /// Estimated overflow heuristic: warns if total content exceeds typical single-slide capacity
    /// </summary>
    public bool HasLikelyOverflow(out string warning)
    {
        int totalBullets = TextBlocks.Where(b => b.Kind == SlideTextBlockKind.BulletList).Sum(b => b.Lines.Count);
        int totalWords = TextBlocks.Sum(b => b.Lines.Sum(CountWords));

        if (Layout == PresentationLayoutType.TitleAndContent && (totalBullets > 8 || totalWords > 120))
        {
            warning = $"Slide content is dense ({totalBullets} bullet points, {totalWords} words). Consider splitting across multiple slides.";
            return true;
        }

        if (Layout == PresentationLayoutType.TextWithImage && (totalBullets > 5 || totalWords > 80))
        {
            warning = $"Slide text with image is dense ({totalWords} words). Text may overlap or exceed bounds.";
            return true;
        }

        if (Layout == PresentationLayoutType.TwoColumn && (totalBullets > 12 || totalWords > 160))
        {
            warning = $"Two-column content is dense ({totalBullets} items). Text may exceed visible slide area.";
            return true;
        }

        warning = string.Empty;
        return false;
    }

    public PresentationSlide Clone()
    {
        return new PresentationSlide
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = Title,
            Layout = Layout,
            TextBlocks = TextBlocks.Select(b => b.Clone()).ToList(),
            Image = Image is not null ? Image with { } : null,
            SpeakerNotes = SpeakerNotes,
            SourceProvenance = SourceProvenance,
            SourcePageNumber = SourcePageNumber
        };
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

