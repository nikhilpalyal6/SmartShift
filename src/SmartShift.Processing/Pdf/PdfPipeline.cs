using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Images;

namespace SmartShift.Processing.Pdf;

public sealed record PdfInspectionResult(
    int PageCount,
    IReadOnlyList<(double Width, double Height)> PageDimensions,
    bool HasSignatures,
    bool IsEncrypted,
    string? PdfVersion,
    string? Title,
    string? Author,
    string? Subject,
    string? Creator);

public sealed record PdfImageOptimizationReport(
    int TotalDiscovered,
    int EligibleCount,
    int OptimizedCount,
    IReadOnlyDictionary<string, int> SkippedReasons,
    string Explanation);

public sealed record PdfCompressionResult(
    int PageCount,
    IReadOnlyList<(double Width, double Height)> PageDimensions,
    bool HasSignatures,
    bool IsEncrypted,
    string? PdfVersion,
    string? Title,
    string? Author,
    string? Subject,
    string? Creator,
    long? TargetSizeBytes,
    bool TargetReached,
    int ImagesOptimizedCount,
    string ProcessingStage,
    string TargetStatusNote,
    int TotalImagesDiscovered = 0,
    int ImagesEligibleCount = 0,
    IReadOnlyDictionary<string, int>? SkippedReasons = null,
    string? LimitsExplanation = null);

public static class PdfPipeline
{
    public static PdfInspectionResult Inspect(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return new PdfInspectionResult(0, [], false, false, null, null, null, null, null);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

            var pageDims = new List<(double Width, double Height)>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                pageDims.Add((page.Width.Point, page.Height.Point));
            }

            var hasSigs = doc.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .Any(d => d.Elements.GetName("/Type") == "/Sig" || d.Elements.ContainsKey("/ByteRange"));

            var isEncrypted = doc.SecuritySettings.IsEncrypted;
            var ver = $"PDF {doc.Version / 10.0:0.0}";

            return new PdfInspectionResult(
                doc.PageCount,
                pageDims,
                hasSigs,
                isEncrypted,
                ver,
                doc.Info.Title,
                doc.Info.Author,
                doc.Info.Subject,
                doc.Info.Creator);
        }
        catch (PdfReaderException)
        {
            // Password-protected or encrypted document that could not be opened with empty password
            return new PdfInspectionResult(0, [], false, true, null, null, null, null, null);
        }
        catch
        {
            return new PdfInspectionResult(0, [], false, false, null, null, null, null, null);
        }
    }

    public static PdfInspectionResult Compress(
        string inputPath,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = Compress(inputPath, outputPath, null, null, progress, cancellationToken);
        return new PdfInspectionResult(
            result.PageCount,
            result.PageDimensions,
            result.HasSignatures,
            result.IsEncrypted,
            result.PdfVersion,
            result.Title,
            result.Author,
            result.Subject,
            result.Creator);
    }

    public static PdfCompressionResult Compress(
        string inputPath,
        string outputPath,
        long? targetSizeBytes,
        string? imageQualityPolicy,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The source PDF file was not found.", inputPath);
        }

        var inputInfo = new FileInfo(inputPath);
        if (inputInfo.Length == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        progress?.Report(new ProcessingProgress(15, "Opening PDF document…"));

        using var inStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PdfDocument doc;
        try
        {
            doc = PdfReader.Open(inStream, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException(
                "This PDF document is password-protected or uses an unsupported encryption handler. Encrypted PDFs cannot be compressed.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The PDF document is malformed or corrupted and could not be parsed.",
                ex);
        }

        using (doc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageCount = doc.PageCount;
            if (pageCount == 0)
            {
                throw new InvalidOperationException("The PDF document contains no pages.");
            }

            var pageDims = new List<(double Width, double Height)>();
            for (int i = 0; i < pageCount; i++)
            {
                var page = doc.Pages[i];
                pageDims.Add((page.Width.Point, page.Height.Point));
            }

            var hasSignatures = doc.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .Any(d => d.Elements.GetName("/Type") == "/Sig" || d.Elements.ContainsKey("/ByteRange"));

            progress?.Report(new ProcessingProgress(30, "Stage A: Deflating content streams and pruning structures…"));

            // Configure optimal compression options
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.EnableCcittCompressionForBilevelImages = true;

            // Zip any uncompressed or recompressible streams
            var objects = doc.Internals.GetAllObjects();
            int totalObjects = objects.Length;
            for (int i = 0; i < totalObjects; i++)
            {
                if (i % 50 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (objects[i] is PdfDictionary dict && dict.Stream is not null)
                {
                    dict.Stream.Zip();
                }
            }

            progress?.Report(new ProcessingProgress(60, "Writing Stage A document…"));
            cancellationToken.ThrowIfCancellationRequested();

            doc.Save(outputPath);

            progress?.Report(new ProcessingProgress(75, "Validating Stage A output…"));
            cancellationToken.ThrowIfCancellationRequested();

            ValidateOutput(outputPath, pageCount, pageDims);

            var stageASize = new FileInfo(outputPath).Length;
            var normalizedPolicy = string.IsNullOrWhiteSpace(imageQualityPolicy)
                ? "balanced"
                : imageQualityPolicy.Trim().ToLowerInvariant();

            bool isOff = normalizedPolicy is "off" or "lossless";
            bool targetAlreadyMet = targetSizeBytes.HasValue && stageASize <= targetSizeBytes.Value;

            // If target is already reached or no target specified or image optimization is off, Stage A is final!
            if (!targetSizeBytes.HasValue || targetAlreadyMet || isOff)
            {
                var inspectReport = InspectEmbeddedImages(doc, normalizedPolicy, cancellationToken);
                string note;
                if (targetAlreadyMet)
                {
                    note = $"Target {FileSizeFormatter.Format(targetSizeBytes!.Value)} met using lossless stream compression.";
                }
                else if (isOff && targetSizeBytes.HasValue)
                {
                    note = $"Target {FileSizeFormatter.Format(targetSizeBytes!.Value)} not reached ({FileSizeFormatter.Format(stageASize)}). Lossless stream compression applied; image optimization disabled.";
                }
                else
                {
                    note = "Lossless stream and structure optimization applied.";
                }

                progress?.Report(new ProcessingProgress(100, "Done."));

                return new PdfCompressionResult(
                    pageCount,
                    pageDims,
                    hasSignatures,
                    false,
                    $"PDF {doc.Version / 10.0:0.0}",
                    doc.Info.Title,
                    doc.Info.Author,
                    doc.Info.Subject,
                    doc.Info.Creator,
                    targetSizeBytes,
                    TargetReached: !targetSizeBytes.HasValue || targetAlreadyMet,
                    ImagesOptimizedCount: 0,
                    ProcessingStage: "Stage A: Lossless Stream Optimization",
                    TargetStatusNote: note,
                    TotalImagesDiscovered: inspectReport.TotalDiscovered,
                    ImagesEligibleCount: inspectReport.EligibleCount,
                    SkippedReasons: inspectReport.SkippedReasons,
                    LimitsExplanation: inspectReport.Explanation);
            }

            // Stage B: Embedded Image Optimization
            progress?.Report(new ProcessingProgress(80, "Stage B: Evaluating embedded images for recompression…"));
            cancellationToken.ThrowIfCancellationRequested();

            var stageBPath = outputPath + ".stageB.tmp";
            PdfImageOptimizationReport report;
            try
            {
                using (var stageStream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var stageDoc = PdfReader.Open(stageStream, PdfDocumentOpenMode.Modify))
                {
                    report = OptimizeEmbeddedImages(stageDoc, normalizedPolicy, cancellationToken);
                    if (report.OptimizedCount > 0)
                    {
                        progress?.Report(new ProcessingProgress(90, "Writing Stage B optimized document…"));
                        stageDoc.Save(stageBPath);
                    }
                }

                if (report.OptimizedCount > 0 && File.Exists(stageBPath))
                {
                    ValidateOutput(stageBPath, pageCount, pageDims);
                    var stageBSize = new FileInfo(stageBPath).Length;

                    // Preserve intermediate best valid output
                    if (stageBSize < stageASize)
                    {
                        File.Move(stageBPath, outputPath, overwrite: true);
                        var finalSize = stageBSize;
                        bool targetMet = finalSize <= targetSizeBytes.Value;
                        string note = targetMet
                            ? $"Target {FileSizeFormatter.Format(targetSizeBytes.Value)} met with lossless compression and {normalizedPolicy} image optimization ({report.OptimizedCount} image(s) optimized)."
                            : $"Target {FileSizeFormatter.Format(targetSizeBytes.Value)} not reached ({FileSizeFormatter.Format(finalSize)}). Safely optimized {report.OptimizedCount} image(s). {report.Explanation}";

                        progress?.Report(new ProcessingProgress(100, "Done."));

                        return new PdfCompressionResult(
                            pageCount,
                            pageDims,
                            hasSignatures,
                            false,
                            $"PDF {doc.Version / 10.0:0.0}",
                            doc.Info.Title,
                            doc.Info.Author,
                            doc.Info.Subject,
                            doc.Info.Creator,
                            targetSizeBytes,
                            TargetReached: targetMet,
                            ImagesOptimizedCount: report.OptimizedCount,
                            ProcessingStage: $"Stage B: Embedded Image Optimization ({normalizedPolicy})",
                            TargetStatusNote: note,
                            TotalImagesDiscovered: report.TotalDiscovered,
                            ImagesEligibleCount: report.EligibleCount,
                            SkippedReasons: report.SkippedReasons,
                            LimitsExplanation: report.Explanation);
                    }
                    else
                    {
                        // Image re-encoding did not reduce size; keep Stage A output
                        TryDelete(stageBPath);
                        string note = $"Target {FileSizeFormatter.Format(targetSizeBytes.Value)} not reached ({FileSizeFormatter.Format(stageASize)}). Image re-encoding did not reduce size; preserved lossless output. {report.Explanation}";

                        progress?.Report(new ProcessingProgress(100, "Done."));

                        return new PdfCompressionResult(
                            pageCount,
                            pageDims,
                            hasSignatures,
                            false,
                            $"PDF {doc.Version / 10.0:0.0}",
                            doc.Info.Title,
                            doc.Info.Author,
                            doc.Info.Subject,
                            doc.Info.Creator,
                            targetSizeBytes,
                            TargetReached: false,
                            ImagesOptimizedCount: 0,
                            ProcessingStage: "Stage A: Lossless Stream Optimization",
                            TargetStatusNote: note,
                            TotalImagesDiscovered: report.TotalDiscovered,
                            ImagesEligibleCount: report.EligibleCount,
                            SkippedReasons: report.SkippedReasons,
                            LimitsExplanation: report.Explanation);
                    }
                }
            }
            finally
            {
                TryDelete(stageBPath);
            }

            // No optimizable raster images produced a smaller file
            string noImagesNote = $"Target {FileSizeFormatter.Format(targetSizeBytes.Value)} not reached ({FileSizeFormatter.Format(stageASize)}). {report.Explanation}";

            progress?.Report(new ProcessingProgress(100, "Done."));

            return new PdfCompressionResult(
                pageCount,
                pageDims,
                hasSignatures,
                false,
                $"PDF {doc.Version / 10.0:0.0}",
                doc.Info.Title,
                doc.Info.Author,
                doc.Info.Subject,
                doc.Info.Creator,
                targetSizeBytes,
                TargetReached: false,
                ImagesOptimizedCount: 0,
                ProcessingStage: "Stage A: Lossless Stream Optimization",
                TargetStatusNote: noImagesNote,
                TotalImagesDiscovered: report.TotalDiscovered,
                ImagesEligibleCount: report.EligibleCount,
                SkippedReasons: report.SkippedReasons,
                LimitsExplanation: report.Explanation);
        }
    }

    public static PdfImageOptimizationReport OptimizeEmbeddedImages(
        PdfDocument doc,
        string qualityPolicy,
        CancellationToken cancellationToken) =>
        ScanOrOptimizeEmbeddedImages(doc, qualityPolicy, applyOptimization: true, cancellationToken);

    public static PdfImageOptimizationReport InspectEmbeddedImages(
        PdfDocument doc,
        string qualityPolicy,
        CancellationToken cancellationToken) =>
        ScanOrOptimizeEmbeddedImages(doc, qualityPolicy, applyOptimization: false, cancellationToken);

    public static PdfImageOptimizationReport ScanOrOptimizeEmbeddedImages(
        PdfDocument doc,
        string qualityPolicy,
        bool applyOptimization,
        CancellationToken cancellationToken)
    {
        var (quality, maxDim) = ResolveQualityPolicy(qualityPolicy);
        var objects = doc.Internals.GetAllObjects();
        int totalDiscovered = 0;
        int eligibleCount = 0;
        int optimizedCount = 0;
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < objects.Length; i++)
        {
            if (i % 20 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (objects[i] is not PdfDictionary dict)
            {
                continue;
            }

            if (dict.Elements.GetName("/Subtype") != "/Image" || dict.Stream is null)
            {
                continue;
            }

            totalDiscovered++;

            // 1. Check Filter
            bool isDct = string.Equals(dict.Elements.GetName("/Filter"), "/DCTDecode", StringComparison.OrdinalIgnoreCase);
            if (!isDct)
            {
                var filterArr = dict.Elements.GetArray("/Filter");
                if (filterArr is not null && filterArr.Elements.Count == 1)
                {
                    isDct = string.Equals(filterArr.Elements.GetName(0), "/DCTDecode", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!isDct)
            {
                var filter = dict.Elements.GetName("/Filter") ?? "Non-DCT/Uncompressed";
                IncrementSkip(skipped, $"Unsupported filter ({filter})");
                continue;
            }

            // 2. Check Transparency Masks
            if (dict.Elements.ContainsKey("/SMask") || dict.Elements.ContainsKey("/Mask"))
            {
                IncrementSkip(skipped, "Transparency mask (/SMask or /Mask)");
                continue;
            }

            // 3. Check Custom Decode Parameters
            if (dict.Elements.ContainsKey("/DecodeParms") || dict.Elements.ContainsKey("/Decode"))
            {
                IncrementSkip(skipped, "Custom decode parameters (/DecodeParms or /Decode)");
                continue;
            }

            // 4. Check Color Space
            if (dict.Elements.ContainsKey("/ColorSpace"))
            {
                var csName = dict.Elements.GetName("/ColorSpace");
                if (!string.Equals(csName, "/DeviceRGB", StringComparison.OrdinalIgnoreCase))
                {
                    var csDesc = string.IsNullOrWhiteSpace(csName) ? "Array/ICCBased/Special" : csName;
                    IncrementSkip(skipped, $"Non-RGB color space ({csDesc})");
                    continue;
                }
            }

            var rawBytes = dict.Stream.Value;
            if (rawBytes is null || rawBytes.Length < 128)
            {
                IncrementSkip(skipped, "Small or empty stream (<128 B)");
                continue;
            }

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(rawBytes));
                if (codec is null)
                {
                    IncrementSkip(skipped, "Codec decode failed");
                    continue;
                }

                if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
                {
                    IncrementSkip(skipped, "Embedded EXIF orientation tag");
                    continue;
                }

                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0 || ImageSafetyLimits.Exceeds(info.Width, info.Height))
                {
                    IncrementSkip(skipped, "Image dimension safety limit exceeded");
                    continue;
                }

                eligibleCount++;

                if (!applyOptimization)
                {
                    continue;
                }

                using var bitmap = SKBitmap.Decode(codec);
                if (bitmap is null)
                {
                    IncrementSkip(skipped, "Bitmap decode failed");
                    continue;
                }

                SKBitmap working = bitmap;
                SKBitmap? resized = null;
                try
                {
                    if (working.Width > maxDim || working.Height > maxDim)
                    {
                        var scale = Math.Min(maxDim / (double)working.Width, maxDim / (double)working.Height);
                        var newW = Math.Max(1, (int)Math.Round(working.Width * scale));
                        var newH = Math.Max(1, (int)Math.Round(working.Height * scale));
                        resized = SkiaImagePipeline.Resize(working, newW, newH, cancellationToken);
                        working = resized;
                    }

                    using var encoded = working.Encode(SKEncodedImageFormat.Jpeg, quality);
                    if (encoded is not null && encoded.Size > 0 && encoded.Size < rawBytes.Length)
                    {
                        var newBytes = encoded.ToArray();
                        dict.Stream.Value = newBytes;
                        dict.Elements.SetInteger("/Length", newBytes.Length);
                        dict.Elements.SetInteger("/Width", working.Width);
                        dict.Elements.SetInteger("/Height", working.Height);
                        optimizedCount++;
                    }
                    else
                    {
                        IncrementSkip(skipped, "Re-encoded stream not smaller");
                    }
                }
                finally
                {
                    resized?.Dispose();
                }
            }
            catch
            {
                IncrementSkip(skipped, "Processing exception");
            }
        }

        var explanation = BuildLimitsExplanation(totalDiscovered, eligibleCount, optimizedCount, skipped, qualityPolicy);
        return new PdfImageOptimizationReport(totalDiscovered, eligibleCount, optimizedCount, skipped, explanation);
    }

    private static void IncrementSkip(Dictionary<string, int> dict, string reason)
    {
        dict[reason] = dict.TryGetValue(reason, out int count) ? count + 1 : 1;
    }

    private static string FormatSkippedReasons(IReadOnlyDictionary<string, int> skipped)
    {
        if (skipped.Count == 0) return "none";
        return string.Join("; ", skipped.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    private static string BuildLimitsExplanation(
        int totalDiscovered,
        int eligibleCount,
        int optimizedCount,
        IReadOnlyDictionary<string, int> skipped,
        string policy)
    {
        if (totalDiscovered == 0)
        {
            return "Document contains no embedded raster images (composed of text, vector paths, or font streams). Stage A lossless deflation was applied.";
        }

        if (eligibleCount == 0)
        {
            var reasons = FormatSkippedReasons(skipped);
            return $"Found {totalDiscovered} embedded image(s), but none were eligible for safe JPEG optimization ({reasons}). Preserved untouched to prevent quality/color corruption.";
        }

        if (optimizedCount == 0)
        {
            var reasons = FormatSkippedReasons(skipped);
            return $"Found {totalDiscovered} embedded image(s) ({eligibleCount} eligible), but re-encoding did not yield a smaller file size ({reasons}). Preserved lossless output.";
        }

        return $"Optimized {optimizedCount} of {eligibleCount} eligible image(s) using {policy} quality ({totalDiscovered} total images). Further reduction would require lossy quality trade-offs or font subsetting.";
    }

    private static (int Quality, int MaxDimension) ResolveQualityPolicy(string policy) =>
        policy switch
        {
            "best" => (85, 2048),
            "smallest" => (60, 1200),
            _ => (75, 1600) // balanced
        };

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public static void ValidateOutput(
        string outputPath,
        int expectedPageCount,
        IReadOnlyList<(double Width, double Height)> expectedDimensions)
    {
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("The output PDF file was not created.");
        }

        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The generated PDF is empty.");
        }

        using var verifyStream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var verifyDoc = PdfReader.Open(verifyStream, PdfDocumentOpenMode.Import);

        if (verifyDoc.PageCount != expectedPageCount)
        {
            throw new InvalidOperationException(
                $"Output validation failed: page count mismatch (expected {expectedPageCount}, got {verifyDoc.PageCount}).");
        }

        for (int i = 0; i < expectedPageCount; i++)
        {
            var page = verifyDoc.Pages[i];
            var expected = expectedDimensions[i];
            if (Math.Abs(page.Width.Point - expected.Width) > 0.5 ||
                Math.Abs(page.Height.Point - expected.Height) > 0.5)
            {
                throw new InvalidOperationException(
                    $"Output validation failed: page {i + 1} dimensions altered (expected {expected.Width:F1}×{expected.Height:F1} pt, got {page.Width.Point:F1}×{page.Height.Point:F1} pt).");
            }
        }
    }
}
