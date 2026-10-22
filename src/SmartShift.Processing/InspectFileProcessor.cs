using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Processing.Pdf;

namespace SmartShift.Processing;

/// <summary>
/// Inspection-only processor. Does not write output files.
/// </summary>
public sealed class InspectFileProcessor : IFileProcessor
{
    private readonly FileClassifier _classifier;

    public InspectFileProcessor(string actionId, FileClassifier classifier)
    {
        ActionId = actionId;
        _classifier = classifier;
    }

    public string ActionId { get; }

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessingResult.Cancelled();
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActionId is not (ActionIds.ShowImageInfo or ActionIds.ShowPdfInfo))
            {
                return ProcessingResult.Fail("This inspector does not handle the requested action.");
            }

            progress?.Report(new ProcessingProgress(10, "Re-reading file header…"));
            var latest = _classifier.Classify(request.Input.Path);
            if (!File.Exists(request.Input.Path))
            {
                return ProcessingResult.Fail("The original file is no longer available.");
            }

            var details = new Dictionary<string, string>
            {
                ["fileName"] = latest.FileName,
                ["format"] = FileClassifier.FormatName(latest.Format),
                ["bytes"] = latest.LengthBytes.ToString(),
                ["status"] = latest.Status
            };
            if (latest.PixelWidth is int w && latest.PixelHeight is int h)
            {
                details["dimensions"] = $"{w} × {h}";
            }

            if (!string.IsNullOrWhiteSpace(latest.PdfVersion))
            {
                details["pdfHeader"] = latest.PdfVersion!;
            }

            if (latest.Format == DetectedFormat.Pdf)
            {
                progress?.Report(new ProcessingProgress(50, "Inspecting PDF document structure…"));
                var pdfInfo = PdfPipeline.Inspect(request.Input.Path);
                if (pdfInfo.PageCount > 0)
                {
                    details["pages"] = pdfInfo.PageCount.ToString();
                    if (pdfInfo.PageDimensions.Count > 0)
                    {
                        var p1 = pdfInfo.PageDimensions[0];
                        details["dimensions"] = $"{(int)Math.Round(p1.Width)} × {(int)Math.Round(p1.Height)} pt ({pdfInfo.PageCount} {(pdfInfo.PageCount == 1 ? "page" : "pages")})";
                    }
                    if (pdfInfo.HasSignatures)
                    {
                        details["signatures"] = "Contains digital signature(s)";
                    }
                    if (!string.IsNullOrWhiteSpace(pdfInfo.Title))
                    {
                        details["title"] = pdfInfo.Title!;
                    }
                    if (!string.IsNullOrWhiteSpace(pdfInfo.Author))
                    {
                        details["author"] = pdfInfo.Author!;
                    }
                }
                details["security"] = pdfInfo.IsEncrypted ? "Encrypted / Password Protected" : "None (Unencrypted)";
            }

            progress?.Report(new ProcessingProgress(100, "Inspection complete."));
            return ProcessingResult.Inspection("Inspection complete. The original file was not modified.", details);
        }, cancellationToken).ConfigureAwait(false);
    }
}
