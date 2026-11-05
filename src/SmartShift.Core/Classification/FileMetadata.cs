namespace SmartShift.Core.Classification;

/// <summary>
/// Immutable metadata describing an inspected file without loading full content into memory.
/// </summary>
public sealed record FileMetadata(
    string Path,
    string FileName,
    string Extension,
    long LengthBytes,
    DateTimeOffset? LastWriteTimeUtc,
    int? PixelWidth = null,
    int? PixelHeight = null,
    string? DocumentVersion = null,
    int? PageCount = null,
    IReadOnlyDictionary<string, string>? AdditionalProperties = null)
{
    public static FileMetadata Empty(string path, string fileName, string extension) =>
        new(path, fileName, extension, 0, null);
}
