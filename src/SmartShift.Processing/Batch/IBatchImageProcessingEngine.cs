namespace SmartShift.Processing.Batch;

public interface IBatchImageProcessingEngine
{
    IReadOnlyList<string> ValidateRequest(BatchJobRequest request);

    Task<BatchSummaryResult> ProcessBatchAsync(
        BatchJobRequest request,
        IProgress<BatchProgressUpdate>? progress,
        CancellationToken cancellationToken);
}
