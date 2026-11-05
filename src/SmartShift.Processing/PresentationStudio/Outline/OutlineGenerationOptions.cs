namespace SmartShift.Processing.PresentationStudio.Outline;

public enum SlideContentDensity
{
    Compact = 3,   // 3 bullets / ~35 words
    Standard = 5,  // 5 bullets / ~60 words
    Spacious = 8   // 8 bullets / ~100 words
}

public sealed record OutlineGenerationOptions
{
    public bool GenerateTitleSlide { get; init; } = true;
    public bool UseHeadingsAsSlideTitles { get; init; } = true;
    public SlideContentDensity ContentDensity { get; init; } = SlideContentDensity.Standard;
    public int MaxBulletsPerSlide => (int)ContentDensity;
    public int MaxWordsPerSlide => ContentDensity switch
    {
        SlideContentDensity.Compact => 40,
        SlideContentDensity.Standard => 70,
        SlideContentDensity.Spacious => 110,
        _ => 70
    };
}
