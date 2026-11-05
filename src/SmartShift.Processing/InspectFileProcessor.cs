using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
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
            if (ActionId is not (ActionIds.ShowImageInfo or ActionIds.ShowPdfInfo or ActionIds.ShowDocumentInfo))
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
                ["size"] = FileSizeFormatter.Format(latest.LengthBytes),
                ["status"] = latest.Status
            };

            if (latest.PixelWidth is int w && latest.PixelHeight is int h)
            {
                details["dimensions"] = $"{w} × {h} px";
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
            else if (latest.Format is DetectedFormat.Txt or DetectedFormat.Markdown)
            {
                progress?.Report(new ProcessingProgress(50, "Inspecting text document…"));
                try
                {
                    if (latest.LengthBytes <= 10 * 1024 * 1024)
                    {
                        int lines = 0;
                        using var reader = new StreamReader(request.Input.Path);
                        while (reader.ReadLine() is not null)
                        {
                            lines++;
                        }
                        details["lines"] = lines.ToString();
                        details["dimensions"] = $"{lines} {(lines == 1 ? "line" : "lines")}";
                    }
                }
                catch { }
            }
            else if (latest.Format == DetectedFormat.Docx)
            {
                details["category"] = "Microsoft Word Document";
                details["importSupport"] = "Compatible with Presentation Studio";
            }
            else if (latest.Format == DetectedFormat.Pptx)
            {
                details["category"] = "Microsoft PowerPoint Presentation";
            }
            else if (latest.Format == DetectedFormat.Xlsx)
            {
                details["category"] = "Microsoft Excel Spreadsheet";
                details["note"] = "Inspection only in this version";
            }

            progress?.Report(new ProcessingProgress(100, "Inspection complete."));
            return ProcessingResult.Inspection("Inspection complete. The original file was not modified.", details);
        }, cancellationToken).ConfigureAwait(false);
    }
}
