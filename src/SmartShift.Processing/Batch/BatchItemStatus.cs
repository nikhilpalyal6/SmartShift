namespace SmartShift.Processing.Batch;

public enum BatchItemStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}
