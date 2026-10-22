using System.IO;
using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Images;

namespace SmartShift.Processing.Documents;

public sealed class MultiImageDocumentBuilder : IMultiImageDocumentBuilder
{
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public MultiImageDocumentBuilder(IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public IReadOnlyList<string> ValidateRequest(MultiImageDocumentRequest request)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Document request cannot be null.");
            return errors;
        }

        if (request.Files is null || request.Files.Count == 0)
        {
            errors.Add("At least one image is required to build a document.");
            return errors;
        }

        foreach (var file in request.Files)
        {
            if (!File.Exists(file.Path))
            {
                errors.Add($"File does not exist: {file.FileName}");
            }
            else if (file.Format is not (DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp))
            {
                errors.Add($"Unsupported image format for '{file.FileName}'. Only JPEG, PNG, and WebP are supported.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            errors.Add("Output directory must be specified.");
        }

        return errors;
    }

    public async Task<MultiImageDocumentResult> BuildDocumentAsync(
        MultiImageDocumentRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid document request: {string.Join(" ", validationErrors)}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            var files = request.Files;
            var ext = request.Mode == DocumentOutputMode.Pdf ? ".pdf" : ".pptx";
            var defaultBaseName = string.IsNullOrWhiteSpace(request.CustomFileName)
                ? $"Images-Document-{files.Count}Pages{ext}"
                : (request.CustomFileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? request.CustomFileName
                    : request.CustomFileName + ext);

            var firstFile = files[0];
            var finalOutputPath = _allocator.Allocate(firstFile.Path, defaultBaseName);

            // Compute pre-hashes of all source files to verify immutability
            var hashesBefore = files.Select(f => FileHasher.Sha256Hex(f.Path)).ToList();

            string createdPath;
            int count = files.Count;

            if (request.Mode == DocumentOutputMode.Pdf)
            {
                createdPath = MultiImagePdfGenerator.GeneratePdf(
                    files,
                    request.PageSettings,
                    finalOutputPath,
                    progress,
                    cancellationToken);
            }
            else
            {
                createdPath = MultiImagePptxGenerator.GeneratePptx(
                    files,
                    request.SlideSettings,
                    finalOutputPath,
                    progress,
                    cancellationToken);
            }

            // Verify source immutability
            for (int i = 0; i < files.Count; i++)
            {
                var hashAfter = FileHasher.Sha256Hex(files[i].Path);
                if (!string.Equals(hashesBefore[i], hashAfter, StringComparison.Ordinal))
                {
                    try { File.Delete(createdPath); } catch { }
                    throw new InvalidOperationException($"Safety violation: Source file '{files[i].FileName}' was modified during document generation. Operation aborted.");
                }
            }

            var fileInfo = new FileInfo(createdPath);
            var details = new Dictionary<string, string>
            {
                ["outputPath"] = createdPath,
                ["outputBytes"] = fileInfo.Length.ToString(),
                ["outputSize"] = FileSizeFormatter.Format(fileInfo.Length),
                ["pageCount"] = count.ToString(),
                ["documentMode"] = request.Mode.ToString(),
                ["originalUnchanged"] = "true"
            };

            string typeName = request.Mode == DocumentOutputMode.Pdf ? "PDF" : "PowerPoint";
            string statusMsg = $"Successfully exported {count}-page {typeName} document ({FileSizeFormatter.Format(fileInfo.Length)}). Originals were not modified.";

            return new MultiImageDocumentResult(
                Succeeded: true,
                OutputPath: createdPath,
                PageOrSlideCount: count,
                StatusMessage: statusMsg,
                Errors: Array.Empty<string>(),
                Details: details);
        }, cancellationToken).ConfigureAwait(false);
    }

    public SKBitmap? RenderPreview(
        ClassifiedFile file,
        DocumentOutputMode mode,
        DocumentPageSettings pageSettings,
        DocumentSlideSettings slideSettings,
        int viewportWidth,
        int viewportHeight)
    {
        if (file is null || !File.Exists(file.Path) || viewportWidth <= 50 || viewportHeight <= 50)
        {
            return null;
        }

        try
        {
            using var decoded = SkiaImagePipeline.Decode(file.Path, CancellationToken.None);
            var imgBitmap = decoded.Bitmap;

            DocumentLayoutResult layout;
            string bgHex;

            if (mode == DocumentOutputMode.Pdf)
            {
                layout = DocumentLayoutEngine.ComputePdfPageLayout(imgBitmap.Width, imgBitmap.Height, pageSettings);
                bgHex = pageSettings.BackgroundHex;
            }
            else
            {
                layout = DocumentLayoutEngine.ComputePptxSlideLayout(imgBitmap.Width, imgBitmap.Height, slideSettings);
                bgHex = slideSettings.BackgroundHex;
            }

            // Calculate preview container scaling to fit within viewport with margin
            double padding = 24.0;
            double availW = Math.Max(10.0, viewportWidth - (2 * padding));
            double availH = Math.Max(10.0, viewportHeight - (2 * padding));

            double fitScale = Math.Min(availW / layout.TargetWidth, availH / layout.TargetHeight);
            int pageDisplayW = Math.Max(1, (int)Math.Round(layout.TargetWidth * fitScale));
            int pageDisplayH = Math.Max(1, (int)Math.Round(layout.TargetHeight * fitScale));

            int pageStartX = (viewportWidth - pageDisplayW) / 2;
            int pageStartY = (viewportHeight - pageDisplayH) / 2;

            var previewBitmap = new SKBitmap(viewportWidth, viewportHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(previewBitmap);

            // Dark workbench background
            canvas.Clear(new SKColor(0x08, 0x0C, 0x14));

            // Draw subtle drop shadow behind page
            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8.0f)
            };
            canvas.DrawRect(new SKRect(pageStartX + 4, pageStartY + 4, pageStartX + pageDisplayW + 4, pageStartY + pageDisplayH + 4), shadowPaint);

            // Draw page / slide paper background
            SKColor bgColor = SKColor.TryParse(bgHex, out var parsedBg) ? parsedBg : SKColors.White;
            using var bgPaint = new SKPaint
            {
                Color = bgColor,
                Style = SKPaintStyle.Fill
            };
            var pageRect = new SKRect(pageStartX, pageStartY, pageStartX + pageDisplayW, pageStartY + pageDisplayH);
            canvas.DrawRect(pageRect, bgPaint);

            // Draw thin page border
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 40),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.0f
            };
            canvas.DrawRect(pageRect, borderPaint);

            // Calculate placed image rectangle on the rendered page
            float imgScreenX = (float)(pageStartX + (layout.ImageX * fitScale));
            float imgScreenY = (float)(pageStartY + (layout.ImageY * fitScale));
            float imgScreenW = (float)(layout.ImageWidth * fitScale);
            float imgScreenH = (float)(layout.ImageHeight * fitScale);

            var destRect = new SKRect(imgScreenX, imgScreenY, imgScreenX + imgScreenW, imgScreenY + imgScreenH);

            SKBitmap? cropped = null;
            try
            {
                SKBitmap bitmapToDraw = imgBitmap;
                if (layout.SourceCropRect.HasValue)
                {
                    var rect = layout.SourceCropRect.Value;
                    cropped = new SKBitmap(rect.Width, rect.Height, imgBitmap.ColorType, imgBitmap.AlphaType);
                    if (imgBitmap.ExtractSubset(cropped, rect))
                    {
                        bitmapToDraw = cropped;
                    }
                    else
                    {
                        cropped.Dispose();
                        cropped = null;
                    }
                }

                canvas.DrawBitmap(bitmapToDraw, destRect, new SKSamplingOptions(SKCubicResampler.Mitchell), null);
            }
            finally
            {
                cropped?.Dispose();
            }

            return previewBitmap;
        }
        catch
        {
            return null;
        }
    }
}
