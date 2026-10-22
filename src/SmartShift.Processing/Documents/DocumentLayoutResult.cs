using SkiaSharp;

namespace SmartShift.Processing.Documents;

public sealed record DocumentLayoutResult(
    double TargetWidth,
    double TargetHeight,
    double ImageX,
    double ImageY,
    double ImageWidth,
    double ImageHeight,
    SKRectI? SourceCropRect = null);
