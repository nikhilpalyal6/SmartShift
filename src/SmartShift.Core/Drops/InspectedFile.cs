namespace SmartShift.Core.Drops;

public sealed record InspectedFile(
    string Path,
    string FileName,
    SupportedFileKind Kind,
    bool Exists,
    long? LengthBytes,
    DateTimeOffset? LastWriteTimeUtc);
