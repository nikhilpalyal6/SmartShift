namespace SmartShift.Processing.PresentationStudio.Outline;

public sealed class PresentationOutline
{
    public string PresentationTitle { get; set; } = "Untitled Presentation";
    public string SourceFilePath { get; set; } = string.Empty;
    public List<OutlineSlideDraft> Slides { get; set; } = [];

    public PresentationOutline Clone()
    {
        return new PresentationOutline
        {
            PresentationTitle = PresentationTitle,
            SourceFilePath = SourceFilePath,
            Slides = Slides.Select(s => s.Clone()).ToList()
        };
    }
}

