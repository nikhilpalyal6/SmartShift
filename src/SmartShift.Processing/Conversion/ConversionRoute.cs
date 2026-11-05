using SmartShift.Core.Classification;

namespace SmartShift.Processing.Conversion;

/// <summary>
/// Represents a fully resolved and validated conversion path from a source format to a target format.
/// </summary>
public sealed record ConversionRoute(
    DetectedFormat SourceFormat,
    DetectedFormat TargetFormat,
    string ActionId,
    IReadOnlyDictionary<string, string> NormalizedOptions,
    string TargetDisplayName,
    string Description);
