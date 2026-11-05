using SmartShift.Core.History;

namespace SmartShift.Processing.History;

public sealed record OperationRetryResult(
    bool Succeeded,
    OperationRecord? NewRecord,
    string Message,
    string? OutputPath);

public interface IOperationRetryService
{
    bool CanRetry(OperationRecord record, out string reason);

    Task<OperationRetryResult> RetryAsync(
        OperationRecord record,
        CancellationToken cancellationToken = default);
}
