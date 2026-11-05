using SmartShift.Core.Classification;

namespace SmartShift.Core.Capabilities;

/// <summary>
/// Source of truth for recognized formats, valid operations, preview modes, and real conversion routes.
/// </summary>
public interface IFileCapabilityRegistry
{
    IReadOnlyList<FileTypeDescriptor> GetAllDescriptors();
    FileTypeDescriptor GetDescriptor(DetectedFormat format);
    FileTypeDescriptor? FindDescriptorByExtension(string? extension);
    bool IsFormatSupported(DetectedFormat format);
    bool IsExtensionSupported(string? extension);
    PreviewCapability GetPreviewCapability(DetectedFormat format);
    IReadOnlyList<string> GetAvailableActionIds(DetectedFormat format);
    IReadOnlyList<DetectedFormat> GetSupportedOutputFormats(DetectedFormat sourceFormat);
    string GetFileFilter(bool includeAllFiles = true);
}
