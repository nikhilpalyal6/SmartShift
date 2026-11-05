using SmartShift.Core.Classification;

namespace SmartShift.Core.Capabilities;

/// <summary>
/// Exposes verified application capabilities, preview behavior, and supported operations for a recognized format.
/// </summary>
public sealed record FileTypeDescriptor(
    DetectedFormat Format,
    FileCategory Category,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Extensions,
    string PrimaryExtension,
    PreviewCapability Preview,
    IReadOnlyList<string> SupportedActionIds,
    IReadOnlyList<DetectedFormat> SupportedConversionOutputs,
    bool CanEdit,
    bool CanImport,
    bool CanExport,
    bool IsSupported,
    string? UnsupportedReason = null);
