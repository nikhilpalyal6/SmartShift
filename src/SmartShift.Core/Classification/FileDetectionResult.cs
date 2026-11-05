namespace SmartShift.Core.Classification;

/// <summary>
/// Detailed result of format detection and content validation.
/// </summary>
public sealed record FileDetectionResult(
    DetectedFormat Format,
    FileCategory Category,
    DetectedFormat ExtensionHint,
    ClassificationIssue Issue,
    string Status,
    FileMetadata Metadata,
    bool IsSupported,
    bool HasContentMismatch);
