using System.IO;
using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfSplitEngine : IPdfSplitService
{
    private readonly IOutputPathAllocator _allocator;

    public PdfSplitEngine(IOutputPathAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    public async Task<PdfSplitValidation> ValidateSourceAsync(string sourcePdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => ValidateSourceInternal(sourcePdfPath), cancellationToken).ConfigureAwait(false);
    }

    private static PdfSplitValidation ValidateSourceInternal(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new PdfSplitValidation(
                filePath,
                fileName,
                0,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: "The specified source PDF file does not exist or is inaccessible.");
        }

        long size;
        try
        {
            var info = new FileInfo(filePath);
            size = info.Length;
            if (size == 0)
            {
                return new PdfSplitValidation(
                    filePath,
                    fileName,
                    0,
                    0,
                    IsValid: false,
                    IsEncrypted: false,
                    ErrorReason: "The PDF file is empty (0 bytes).");
            }
        }
        catch (Exception ex)
        {
            return new PdfSplitValidation(
                filePath,
                fileName,
                0,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: $"File access error: {ex.Message}");
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

            if (doc.SecuritySettings.IsEncrypted)
            {
                return new PdfSplitValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: true,
                    ErrorReason: "This PDF is encrypted or password-protected and cannot be split.");
            }

            int pageCount = doc.PageCount;
            if (pageCount <= 0)
            {
                return new PdfSplitValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: false,
                    ErrorReason: "The PDF document contains no displayable pages.");
            }

            return new PdfSplitValidation(
                filePath,
                fileName,
                size,
                pageCount,
                IsValid: true,
                IsEncrypted: false,
                ErrorReason: null);
        }
        catch (Exception ex)
        {
            return new PdfSplitValidation(
                filePath,
                fileName,
                size,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: $"Invalid or corrupt PDF structure: {ex.Message}");
        }
    }

    public async Task<PdfSplitResult> SplitAsync(
        PdfSplitRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourcePdfPath))
        {
            throw new ArgumentException("Source PDF path cannot be empty.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() => ExecuteSplitInternal(request, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record SplitPlanItem(
        string TargetFileName,
        IReadOnlyList<int> SourcePages,
        string RangeDescription);

    private PdfSplitResult ExecuteSplitInternal(
        PdfSplitRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ProcessingProgress(5, "Validating source PDF document…"));

        var validation = ValidateSourceInternal(request.SourcePdfPath);
        if (!validation.IsValid)
        {
            return new PdfSplitResult(
                Succeeded: false,
                WasCancelled: false,
                ErrorReason: validation.ErrorReason ?? "The source document is invalid or corrupt.",
                GeneratedFiles: [],
                OutputDirectory: request.OutputDirectory ?? string.Empty,
                TotalSourcePages: 0,
                SummaryMessage: $"Split failed: {validation.ErrorReason}");
        }

        int totalPages = validation.TotalPages;

        // Build planned splits
        var plan = new List<SplitPlanItem>();
        var sourceBaseName = Path.GetFileNameWithoutExtension(request.SourcePdfPath);
        int padDigits = Math.Max(3, totalPages.ToString().Length);

        if (request.Mode == PdfSplitMode.SplitEveryPage)
        {
            for (int p = 1; p <= totalPages; p++)
            {
                string suffix = $"_page_{p.ToString($"D{padDigits}")}";
                plan.Add(new SplitPlanItem($"{sourceBaseName}{suffix}.pdf", [p], $"Page {p}"));
            }
        }
        else // ExtractRanges
        {
            if (request.Ranges is null || request.Ranges.Count == 0)
            {
                return new PdfSplitResult(
                    Succeeded: false,
                    WasCancelled: false,
                    ErrorReason: "No page ranges were provided for extraction.",
                    GeneratedFiles: [],
                    OutputDirectory: request.OutputDirectory ?? string.Empty,
                    TotalSourcePages: totalPages,
                    SummaryMessage: "Split failed: No page ranges specified.");
            }

            foreach (var r in request.Ranges)
            {
                string suffix = r.ToSuggestedFileSuffix(padDigits);
                plan.Add(new SplitPlanItem($"{sourceBaseName}{suffix}.pdf", r.Pages, r.ToString()));
            }
        }

        var outDir = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? _allocator.RootDirectory
            : request.OutputDirectory;

        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var newlyCreatedPartials = new List<string>();
        var newlyPromotedOutputs = new List<string>();
        var generatedItemResults = new List<PdfSplitItemResult>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var inStream = new FileStream(request.SourcePdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sourceDoc = PdfReader.Open(inStream, PdfDocumentOpenMode.Import);

            int totalPlans = plan.Count;

            for (int i = 0; i < totalPlans; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = plan[i];
                int pct = 10 + (int)(75.0 * i / Math.Max(1, totalPlans));
                progress?.Report(new ProcessingProgress(pct, $"Extracting {item.RangeDescription} ({i + 1}/{totalPlans})…"));

                // Resolve safe collision-free target path
                string finalPath = ResolveCollisionFreePath(outDir, item.TargetFileName, newlyPromotedOutputs);
                string tempPartial = finalPath + ".partial";

                if (File.Exists(tempPartial))
                {
                    try { File.Delete(tempPartial); } catch { }
                }

                newlyCreatedPartials.Add(tempPartial);

                // Build extracted PDF
                using (var outputDoc = new PdfDocument())
                {
                    outputDoc.Info.Title = Path.GetFileNameWithoutExtension(finalPath);
                    outputDoc.Info.Creator = "SmartShift PDF Split Studio";
                    outputDoc.Options.CompressContentStreams = true;

                    foreach (int pageNumber in item.SourcePages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int pageIndex = pageNumber - 1;
                        if (pageIndex < 0 || pageIndex >= sourceDoc.PageCount)
                        {
                            throw new InvalidOperationException($"Requested page {pageNumber} is out of bounds (1..{sourceDoc.PageCount}).");
                        }

                        var sourcePage = sourceDoc.Pages[pageIndex];
                        outputDoc.AddPage(sourcePage);
                    }

                    outputDoc.Save(tempPartial);
                }

                // Output validation
                progress?.Report(new ProcessingProgress(pct + 1, $"Validating output: {Path.GetFileName(finalPath)}…"));

                if (!File.Exists(tempPartial) || new FileInfo(tempPartial).Length == 0)
                {
                    throw new InvalidOperationException($"Failed to write extracted PDF file '{Path.GetFileName(finalPath)}'.");
                }

                using (var verifyStream = new FileStream(tempPartial, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var verifyDoc = PdfReader.Open(verifyStream, PdfDocumentOpenMode.Import))
                {
                    if (verifyDoc.PageCount != item.SourcePages.Count)
                    {
                        throw new InvalidOperationException(
                            $"Validation failure for '{Path.GetFileName(finalPath)}': expected {item.SourcePages.Count} pages but output contains {verifyDoc.PageCount}.");
                    }

                    // Verify page dimensions match source pages
                    for (int pIdx = 0; pIdx < verifyDoc.PageCount; pIdx++)
                    {
                        int srcPageIdx = item.SourcePages[pIdx] - 1;
                        var srcPage = sourceDoc.Pages[srcPageIdx];
                        var outPage = verifyDoc.Pages[pIdx];

                        if (Math.Abs(srcPage.Width.Point - outPage.Width.Point) > 1.0 ||
                            Math.Abs(srcPage.Height.Point - outPage.Height.Point) > 1.0)
                        {
                            throw new InvalidOperationException(
                                $"Dimension mismatch on page {pIdx + 1} of '{Path.GetFileName(finalPath)}'.");
                        }
                    }
                }

                // Atomically promote
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
                File.Move(tempPartial, finalPath);
                newlyCreatedPartials.Remove(tempPartial);
                newlyPromotedOutputs.Add(finalPath);

                var outInfo = new FileInfo(finalPath);
                generatedItemResults.Add(new PdfSplitItemResult(
                    finalPath,
                    item.SourcePages,
                    outInfo.Length,
                    item.SourcePages.Count));
            }

            progress?.Report(new ProcessingProgress(100, $"Successfully generated {generatedItemResults.Count} PDF document(s)."));

            string summaryMsg = request.Mode == PdfSplitMode.SplitEveryPage
                ? $"Split all {totalPages} pages into {generatedItemResults.Count} individual 1-page PDF files."
                : $"Extracted {generatedItemResults.Count} range selection(s) ({generatedItemResults.Sum(g => g.PageCount)} total pages) into separate PDF files.";

            return new PdfSplitResult(
                Succeeded: true,
                WasCancelled: false,
                ErrorReason: null,
                GeneratedFiles: generatedItemResults,
                OutputDirectory: outDir,
                TotalSourcePages: totalPages,
                SummaryMessage: summaryMsg);
        }
        catch (OperationCanceledException)
        {
            RollbackOutputs(newlyCreatedPartials, newlyPromotedOutputs);
            return new PdfSplitResult(
                Succeeded: false,
                WasCancelled: true,
                ErrorReason: "The operation was cancelled by the user.",
                GeneratedFiles: [],
                OutputDirectory: outDir,
                TotalSourcePages: totalPages,
                SummaryMessage: "Operation cancelled. Incomplete output files were cleaned up.");
        }
        catch (Exception ex)
        {
            RollbackOutputs(newlyCreatedPartials, newlyPromotedOutputs);
            return new PdfSplitResult(
                Succeeded: false,
                WasCancelled: false,
                ErrorReason: ex.Message,
                GeneratedFiles: [],
                OutputDirectory: outDir,
                TotalSourcePages: totalPages,
                SummaryMessage: $"PDF split failed: {ex.Message}");
        }
    }

    private static void RollbackOutputs(List<string> partials, List<string> promoted)
    {
        foreach (var p in partials)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        foreach (var f in promoted)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private static string ResolveCollisionFreePath(string directory, string desiredFileName, IReadOnlyCollection<string> alreadyClaimed)
    {
        string baseName = Path.GetFileNameWithoutExtension(desiredFileName);
        string ext = Path.GetExtension(desiredFileName);

        string candidate = Path.Combine(directory, desiredFileName);
        int counter = 1;

        while (File.Exists(candidate) || alreadyClaimed.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(directory, $"{baseName} ({counter}){ext}");
            counter++;
        }

        return candidate;
    }
}
