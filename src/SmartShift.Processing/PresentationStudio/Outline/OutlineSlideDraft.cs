using SmartShift.Processing.PresentationStudio.Models;

namespace SmartShift.Processing.PresentationStudio.Outline;

public sealed class OutlineSlideDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public List<string> BulletPoints { get; set; } = [];
    public string? ParagraphText { get; set; }
    public PresentationLayoutType SuggestedLayout { get; set; } = PresentationLayoutType.TitleAndContent;
    public string? SourceProvenance { get; set; }
    public int? SourcePageNumber { get; set; }

    public OutlineSlideDraft Clone()
    {
        return new OutlineSlideDraft
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = Title,
            BulletPoints = [.. BulletPoints],
            ParagraphText = ParagraphText,
            SuggestedLayout = SuggestedLayout,
            SourceProvenance = SourceProvenance,
            SourcePageNumber = SourcePageNumber
        };
    }
}
