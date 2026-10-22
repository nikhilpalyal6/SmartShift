namespace SmartShift.Processing.Documents;

public sealed record DocumentPageSettings(
    PageSizeOption PageSize = PageSizeOption.A4,
    PageOrientationOption Orientation = PageOrientationOption.Portrait,
    ImagePlacementOption Placement = ImagePlacementOption.Contain,
    double MarginPoints = 36.0,
    string BackgroundHex = "#FFFFFF");

public sealed record DocumentSlideSettings(
    SlideSizeOption SlideSize = SlideSizeOption.Widescreen16x9,
    ImagePlacementOption Placement = ImagePlacementOption.Contain,
    string BackgroundHex = "#FFFFFF");
