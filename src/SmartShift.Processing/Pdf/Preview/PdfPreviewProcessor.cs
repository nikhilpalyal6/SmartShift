using SmartShift.Core.Actions;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Pdf.Preview;

public sealed class PdfPreviewProcessor : IFileProcessor
{
    public string ActionId => ActionIds.PreviewPdf;

    public bool IsAvailable => true;

    public Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ProcessingResult.Cancelled());
        }

        return Task.FromResult(ProcessingResult.Success(
            "Document inspection preview active. Original file was not modified.",
            request.Input.Path,
            new Dictionary<string, string>
            {
                ["actionId"] = ActionIds.PreviewPdf,
                ["originalUnchanged"] = "true"
            }));
    }
}
