namespace SmartShift.Processing;

/// <summary>
/// Explicitly unavailable transformation. Never reports a successful conversion.
/// </summary>
public sealed class UnavailableTransformationProcessor : IFileProcessor
{
    public UnavailableTransformationProcessor(string actionId, string engineName)
    {
        ActionId = actionId;
        EngineName = engineName;
    }

    public string ActionId { get; }

    public string EngineName { get; }

    public bool IsAvailable => false;

    public Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ProcessingResult.Cancelled());
        }

        progress?.Report(new ProcessingProgress(0, "This action is not implemented."));
        return Task.FromResult(ProcessingResult.Fail(
            $"{EngineName} is not implemented in Phase 1. No output was written.",
            [$"Action '{ActionId}' cannot run until {EngineName} is added."]));
    }
}
