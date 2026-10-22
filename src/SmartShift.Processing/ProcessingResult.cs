namespace SmartShift.Processing;

public sealed record ProcessingResult(
    bool Succeeded,
    bool WasCancelled,
    bool OriginalUnchanged,
    string Status,
    string? OutputPath,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> Details)
{
    public static ProcessingResult Fail(string status, IEnumerable<string>? errors = null, bool originalUnchanged = true)
        => new(false, false, originalUnchanged, status, null, (errors ?? [status]).ToList(), new Dictionary<string, string>());

    public static ProcessingResult Cancelled()
        => new(false, true, true, "The operation was cancelled. The original file was not changed.", null, ["Cancelled."], new Dictionary<string, string>());

    public static ProcessingResult Inspection(string status, IReadOnlyDictionary<string, string> details)
        => new(true, false, true, status, null, Array.Empty<string>(), details);

    public static ProcessingResult Success(string status, string outputPath, IReadOnlyDictionary<string, string> details)
        => new(true, false, true, status, outputPath, Array.Empty<string>(), details);
}
