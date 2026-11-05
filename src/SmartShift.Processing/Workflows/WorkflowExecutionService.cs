using System.Diagnostics;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.IO;
using SmartShift.Core.Workflows;
using SmartShift.Processing.History;

namespace SmartShift.Processing.Workflows;

public sealed class WorkflowExecutionService : IWorkflowExecutionService
{
    private readonly ProcessorCatalog _catalog;
    private readonly IOutputPathAllocator _allocator;
    private readonly IProcessingHistoryStore _historyStore;

    public WorkflowExecutionService(
        ProcessorCatalog catalog,
        IOutputPathAllocator allocator,
        IProcessingHistoryStore historyStore)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        SavedWorkflow workflow,
        ClassifiedFile file,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(file);

        if (!File.Exists(file.Path))
        {
            return new WorkflowExecutionResult(false, $"Input file '{file.FileName}' does not exist on disk.", null, null);
        }

        var validationErrors = workflow.Validate();
        if (validationErrors.Count > 0)
        {
            return new WorkflowExecutionResult(false, $"Invalid workflow configuration: {string.Join(" ", validationErrors)}", null, null);
        }

        var processor = _catalog.Find(workflow.ActionId);
        if (processor is null || !processor.IsAvailable)
        {
            return new WorkflowExecutionResult(false, $"No processor available for action '{workflow.ActionId}'.", null, null);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var request = new ProcessingRequest(
            Input: file,
            ActionId: workflow.ActionId,
            Options: new ProcessingOptions(workflow.Options),
            OutputDirectory: _allocator.RootDirectory);

        try
        {
            var result = await processor.ProcessAsync(request, progress, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var completedAt = DateTimeOffset.UtcNow;

            var record = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: workflow.ActionId,
                ActionName: $"{workflow.Name} ({processor.ActionId})",
                Category: workflow.Category,
                InputPaths: [file.Path],
                OutputPaths: result.OutputPath is not null ? [result.OutputPath] : [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                Duration: sw.Elapsed,
                Status: result.WasCancelled
                    ? OperationStatus.Cancelled
                    : (result.Succeeded ? OperationStatus.Completed : OperationStatus.Failed),
                Options: workflow.Options,
                PresetName: workflow.Name,
                FailureReason: result.Succeeded ? null : result.Status,
                RetryOfOperationId: null,
                Details: result.Details);

            _historyStore.RecordOperation(record);

            if (result.Succeeded && result.OutputPath is not null)
            {
                return new WorkflowExecutionResult(
                    true,
                    $"Workflow '{workflow.Name}' completed: {Path.GetFileName(result.OutputPath)} created.",
                    result.OutputPath,
                    record);
            }

            return new WorkflowExecutionResult(
                false,
                $"Workflow '{workflow.Name}' failed: {result.Status}",
                null,
                record);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var cancelledRecord = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: workflow.ActionId,
                ActionName: workflow.Name,
                Category: workflow.Category,
                InputPaths: [file.Path],
                OutputPaths: [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: sw.Elapsed,
                Status: OperationStatus.Cancelled,
                Options: workflow.Options,
                PresetName: workflow.Name,
                FailureReason: "Workflow execution cancelled.",
                RetryOfOperationId: null,
                Details: new Dictionary<string, string>());

            _historyStore.RecordOperation(cancelledRecord);
            return new WorkflowExecutionResult(false, "Workflow was cancelled.", null, cancelledRecord);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failedRecord = new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: workflow.ActionId,
                ActionName: workflow.Name,
                Category: workflow.Category,
                InputPaths: [file.Path],
                OutputPaths: [],
                StartedAtUtc: startedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: sw.Elapsed,
                Status: OperationStatus.Failed,
                Options: workflow.Options,
                PresetName: workflow.Name,
                FailureReason: ex.Message,
                RetryOfOperationId: null,
                Details: new Dictionary<string, string>());

            _historyStore.RecordOperation(failedRecord);
            return new WorkflowExecutionResult(false, $"Workflow error: {ex.Message}", null, failedRecord);
        }
    }
}
