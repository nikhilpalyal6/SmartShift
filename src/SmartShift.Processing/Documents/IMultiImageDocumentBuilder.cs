using SkiaSharp;
using SmartShift.Core.Classification;

namespace SmartShift.Processing.Documents;

public interface IMultiImageDocumentBuilder
{
    IReadOnlyList<string> ValidateRequest(MultiImageDocumentRequest request);

    Task<MultiImageDocumentResult> BuildDocumentAsync(
        MultiImageDocumentRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);

    SKBitmap? RenderPreview(
        ClassifiedFile file,
        DocumentOutputMode mode,
        DocumentPageSettings pageSettings,
        DocumentSlideSettings slideSettings,
        int viewportWidth,
        int viewportHeight);
}
