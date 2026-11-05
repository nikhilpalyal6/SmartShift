using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.Workflows;

namespace SmartShift.Processing.Workflows;

public sealed record WorkflowExecutionResult(
    bool Succeeded,
    string Message,
    string? OutputPath,
    OperationRecord? Record);

public interface IWorkflowExecutionService
{
    Task<WorkflowExecutionResult> ExecuteAsync(
        SavedWorkflow workflow,
        ClassifiedFile file,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
