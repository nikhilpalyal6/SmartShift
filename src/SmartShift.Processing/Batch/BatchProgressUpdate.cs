namespace SmartShift.Processing.Batch;

public sealed record BatchProgressUpdate(
    int CompletedCount,
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    int OverallPercent,
    string? CurrentFileName,
    string StatusMessage);
