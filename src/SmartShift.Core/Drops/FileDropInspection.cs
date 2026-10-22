namespace SmartShift.Core.Drops;

public sealed record FileDropInspection(
    bool IsValidPayload,
    string Status,
    IReadOnlyList<string> AvailableFormats,
    IReadOnlyList<InspectedFile> Files,
    IReadOnlyList<string> RejectedReasons)
{
    public int FileCount => Files.Count;

    public int SupportedCount => Files.Count(f => f.Kind != SupportedFileKind.Unsupported);

    public int UnsupportedCount => Files.Count(f => f.Kind == SupportedFileKind.Unsupported);

    public IEnumerable<string> FileNames => Files.Select(f => f.FileName);

    public static FileDropInspection Invalid(string status, IReadOnlyList<string>? formats = null)
        => new(
            IsValidPayload: false,
            Status: status,
            AvailableFormats: formats ?? Array.Empty<string>(),
            Files: Array.Empty<InspectedFile>(),
            RejectedReasons: new[] { status });
}
