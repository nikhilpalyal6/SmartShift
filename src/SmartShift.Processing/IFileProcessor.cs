namespace SmartShift.Processing;

public interface IFileProcessor
{
    string ActionId { get; }

    bool IsAvailable { get; }

    Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);
}
