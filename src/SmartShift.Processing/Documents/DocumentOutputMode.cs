namespace SmartShift.Processing.Documents;

public enum DocumentOutputMode
{
    Pdf,
    Pptx
}

public enum PageSizeOption
{
    A4,
    Letter,
    ImageMatch
}

public enum PageOrientationOption
{
    Portrait,
    Landscape,
    Auto
}

public enum ImagePlacementOption
{
    Contain,
    FillCrop,
    ImageSize
}

public enum SlideSizeOption
{
    Widescreen16x9,
    Standard4x3,
    ImageMatch
}
