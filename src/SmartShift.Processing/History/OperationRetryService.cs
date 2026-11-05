using System.Diagnostics;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.IO;

namespace SmartShift.Processing.History;

public sealed class OperationRetryService : IOperationRetryService
{
    private readonly ProcessorCatalog _catalog;
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;
    private readonly IProcessingHistoryStore _historyStore;

    public OperationRetryService(
        ProcessorCatalog catalog,
        IOutputPathAllocator allocator,
        FileClassifier classifier,
        IProcessingHistoryStore historyStore)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
    }

    public bool CanRetry(OperationRecord record, out string reason)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Status == OperationStatus.Cancelled)
        {
            reason = "Cancelled operations cannot be retried automatically.";
            return false;
        }

        if (record.InputPaths is null || record.InputPaths.Count == 0)
        {
            reason = "Operation contains no input files.";
            return false;
        }

        foreach (var path in record.InputPaths)
        {
            if (!File.Exists(path))
            {
                reason = $"Input file '{Path.GetFileName(path)}' no longer exists on disk.";
                return false;
            }
        }

        var processor = _catalog.Find(record.ActionId);
        if (processor is null || !processor.IsAvailable)
        {
            reason = $"No processor registered or available for action '{record.ActionId}'.";
            return false;
        }

        var optionErrors = ActionOptionValidator.Validate(record.ActionId, record.Options);
        if (optionErrors.Count > 0)
        {
            reason = $"Configuration is invalid: {string.Join(" ", optionErrors)}";
            return false;
        }

        reason = "Ready to retry.";
        return true;
    }

    public async Task<OperationRetryResult> RetryAsync(
        OperationRecord record,
        CancellationToken cancellationToken = default)
    {
        if (!CanRetry(record, out var reason))
        {
            return new OperationRetryResult(false, null, reason, null);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var primaryInput = record.InputPaths[0];
        var classified = _classifier.Classify(primaryInput);

        var processor = _catalog.Find(record.ActionId)!;
        var request = new ProcessingRequest(
            Input: classified,
            ActionId: record.ActionId,
            Options: new ProcessingOptions(record.Options),
            OutputDirectory: _allocator.RootDirectory);

        try
        {
            var result = await processor.ProcessAsync(request, null, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var completedAt = DateTimeOffset.UtcNow;

            var newRecord = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: record.ActionId,
                ActionName: record.ActionName,
                Category: record.Category,
                InputPaths: record.InputPaths,
                OutputPaths: result.OutputPath is not null ? [result.OutputPath] : [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                Duration: stopwatch.Elapsed,
                Status: result.WasCancelled
                    ? OperationStatus.Cancelled
                    : (result.Succeeded ? OperationStatus.Completed : OperationStatus.Failed),
                Options: record.Options,
                PresetName: record.PresetName,
                FailureReason: result.Succeeded ? null : result.Status,
                RetryOfOperationId: record.Id,
                Details: result.Details);

            _historyStore.RecordOperation(newRecord);

            if (result.Succeeded && result.OutputPath is not null)
            {
                return new OperationRetryResult(
                    true,
                    newRecord,
                    $"Retry succeeded: {Path.GetFileName(result.OutputPath)} created.",
                    result.OutputPath);
            }

            return new OperationRetryResult(
                false,
                newRecord,
                $"Retry failed: {result.Status}",
                null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var cancelledRecord = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: record.ActionId,
                ActionName: record.ActionName,
                Category: record.Category,
                InputPaths: record.InputPaths,
                OutputPaths: [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: stopwatch.Elapsed,
                Status: OperationStatus.Cancelled,
                Options: record.Options,
                PresetName: record.PresetName,
                FailureReason: "Retry cancelled by user.",
                RetryOfOperationId: record.Id,
                Details: new Dictionary<string, string>());

            _historyStore.RecordOperation(cancelledRecord);
            return new OperationRetryResult(false, cancelledRecord, "Retry was cancelled.", null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failedRecord = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: record.ActionId,
                ActionName: record.ActionName,
                Category: record.Category,
                InputPaths: record.InputPaths,
                OutputPaths: [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: stopwatch.Elapsed,
                Status: OperationStatus.Failed,
                Options: record.Options,
                PresetName: record.PresetName,
                FailureReason: ex.Message,
                RetryOfOperationId: record.Id,
                Details: new Dictionary<string, string>());

            _historyStore.RecordOperation(failedRecord);
            return new OperationRetryResult(false, failedRecord, $"Retry error: {ex.Message}", null);
        }
    }
}
