namespace SmartShift.Processing.PresentationStudio.Models;

public enum SlideTextBlockKind
{
    BulletList,
    Paragraph
}

public sealed class SlideTextBlock
{
    public SlideTextBlockKind Kind { get; set; } = SlideTextBlockKind.BulletList;
    public List<string> Lines { get; set; } = [];
    public int ColumnIndex { get; set; } = 0; // 0 = left/primary, 1 = right/secondary

    public SlideTextBlock Clone()
    {
        return new SlideTextBlock
        {
            Kind = Kind,
            Lines = [.. Lines],
            ColumnIndex = ColumnIndex
        };
    }
}

