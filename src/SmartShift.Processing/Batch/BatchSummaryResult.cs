namespace SmartShift.Processing.Batch;

public sealed record BatchSummaryResult(
    bool AllSucceeded,
    bool AnySucceeded,
    bool WasCancelled,
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    IReadOnlyList<BatchItemResult> ItemResults,
    TimeSpan Elapsed,
    string SummaryMessage);
