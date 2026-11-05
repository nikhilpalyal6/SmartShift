using System.IO;
using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfPageOrganizerEngine : IPdfPageOrganizerService
{
    private readonly IOutputPathAllocator _allocator;

    public PdfPageOrganizerEngine(IOutputPathAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    public async Task<PdfOrganizeValidation> ValidateSourceAsync(string sourcePdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => ValidateSourceInternal(sourcePdfPath), cancellationToken).ConfigureAwait(false);
    }

    private static PdfOrganizeValidation ValidateSourceInternal(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new PdfOrganizeValidation(
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
                return new PdfOrganizeValidation(
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
            return new PdfOrganizeValidation(
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
                return new PdfOrganizeValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: true,
                    ErrorReason: "This PDF is encrypted or password-protected and cannot be organized.");
            }

            int pageCount = doc.PageCount;
            if (pageCount <= 0)
            {
                return new PdfOrganizeValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: false,
                    ErrorReason: "The PDF document contains no displayable pages.");
            }

            var rotations = new List<int>(pageCount);
            var dimensions = new List<(double WidthPt, double HeightPt)>(pageCount);

            for (int i = 0; i < pageCount; i++)
            {
                var page = doc.Pages[i];
                rotations.Add(page.Rotate);
                dimensions.Add((page.Width.Point, page.Height.Point));
            }

            return new PdfOrganizeValidation(
                filePath,
                fileName,
                size,
                pageCount,
                IsValid: true,
                IsEncrypted: false,
                ErrorReason: null,
                PageRotations: rotations,
                PageDimensions: dimensions);
        }
        catch (Exception ex)
        {
            return new PdfOrganizeValidation(
                filePath,
                fileName,
                size,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: $"Invalid or corrupt PDF structure: {ex.Message}");
        }
    }

    public async Task<PdfOrganizeResult> OrganizeAsync(
        PdfOrganizeRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourcePdfPath))
        {
            throw new ArgumentException("Source PDF path cannot be empty.", nameof(request));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: true,
                ErrorReason: "The organize operation was cancelled by the user.",
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: "Operation cancelled. Temporary files were cleaned up.");
        }

        try
        {
            return await Task.Run(() => ExecuteOrganizeInternal(request, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: true,
                ErrorReason: "The organize operation was cancelled by the user.",
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: "Operation cancelled. Temporary files were cleaned up.");
        }
    }

    private PdfOrganizeResult ExecuteOrganizeInternal(
        PdfOrganizeRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ProcessingProgress(5, "Validating source PDF and page configuration…"));

        var validation = ValidateSourceInternal(request.SourcePdfPath);
        if (!validation.IsValid)
        {
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: false,
                ErrorReason: validation.ErrorReason ?? "The source document is invalid or corrupt.",
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: $"Organize failed: {validation.ErrorReason}");
        }

        if (request.PageTransforms is null || request.PageTransforms.Count == 0)
        {
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: false,
                ErrorReason: "Cannot export an empty PDF. At least one page must remain.",
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: "Export failed: No pages selected for the output document.");
        }

        // Validate each transform
        for (int i = 0; i < request.PageTransforms.Count; i++)
        {
            var transform = request.PageTransforms[i];
            if (transform.OriginalPageIndex < 0 || transform.OriginalPageIndex >= validation.TotalPages)
            {
                return new PdfOrganizeResult(
                    Succeeded: false,
                    WasCancelled: false,
                    ErrorReason: $"Invalid page reference at position {i + 1}: original page index {transform.OriginalPageIndex} is out of bounds (0..{validation.TotalPages - 1}).",
                    OutputPath: string.Empty,
                    OutputPageCount: 0,
                    FileSizeBytes: 0,
                    SummaryMessage: "Export failed: Invalid page reference.");
            }

            if (transform.RotationDegrees % 90 != 0)
            {
                return new PdfOrganizeResult(
                    Succeeded: false,
                    WasCancelled: false,
                    ErrorReason: $"Invalid rotation angle {transform.RotationDegrees}° at position {i + 1}. Rotation must be a multiple of 90°.",
                    OutputPath: string.Empty,
                    OutputPageCount: 0,
                    FileSizeBytes: 0,
                    SummaryMessage: "Export failed: Invalid page rotation.");
            }
        }

        // Compute pre-export SHA-256 of source file to verify non-modification later
        string sourceShaBefore = ComputeSha256(request.SourcePdfPath);

        var outDir = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? _allocator.RootDirectory
            : request.OutputDirectory;

        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(request.SourcePdfPath);
        string desiredFileName = string.IsNullOrWhiteSpace(request.CustomOutputFileName)
            ? $"{sourceBaseName}_organized.pdf"
            : (request.CustomOutputFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? request.CustomOutputFileName
                : $"{request.CustomOutputFileName}.pdf");

        string finalPath = ResolveCollisionFreePath(outDir, desiredFileName);
        string tempPartial = finalPath + ".partial";

        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProcessingProgress(15, "Reading source document pages…"));

            using var inStream = new FileStream(request.SourcePdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sourceDoc = PdfReader.Open(inStream, PdfDocumentOpenMode.Import);

            using (var outputDoc = new PdfDocument())
            {
                outputDoc.Info.Title = Path.GetFileNameWithoutExtension(finalPath);
                outputDoc.Info.Creator = "SmartShift PDF Page Organizer";
                outputDoc.Options.CompressContentStreams = true;

                int totalTransforms = request.PageTransforms.Count;
                for (int i = 0; i < totalTransforms; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pct = 20 + (int)(60.0 * i / Math.Max(1, totalTransforms));
                    progress?.Report(new ProcessingProgress(pct, $"Arranging and orienting page {i + 1} of {totalTransforms}…"));

                    var transform = request.PageTransforms[i];
                    var sourcePage = sourceDoc.Pages[transform.OriginalPageIndex];

                    var newPage = outputDoc.AddPage(sourcePage);

                    // Compute normalized effective rotation
                    int effectiveRotate = ((sourcePage.Rotate + transform.RotationDegrees) % 360 + 360) % 360;
                    newPage.Rotate = effectiveRotate;
                }

                progress?.Report(new ProcessingProgress(85, "Writing output PDF…"));
                outputDoc.Save(tempPartial);
            }

            // Output verification
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProcessingProgress(90, "Verifying structural integrity of organized PDF…"));

            if (!File.Exists(tempPartial) || new FileInfo(tempPartial).Length == 0)
            {
                throw new InvalidOperationException("Failed to generate output PDF file (empty or missing).");
            }

            using (var verifyStream = new FileStream(tempPartial, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var verifyDoc = PdfReader.Open(verifyStream, PdfDocumentOpenMode.Import))
            {
                if (verifyDoc.PageCount != request.PageTransforms.Count)
                {
                    throw new InvalidOperationException(
                        $"Verification mismatch: expected {request.PageTransforms.Count} pages, but output contains {verifyDoc.PageCount}.");
                }

                for (int i = 0; i < verifyDoc.PageCount; i++)
                {
                    var transform = request.PageTransforms[i];
                    var srcPage = sourceDoc.Pages[transform.OriginalPageIndex];
                    var outPage = verifyDoc.Pages[i];

                    int expectedEffectiveRotate = ((srcPage.Rotate + transform.RotationDegrees) % 360 + 360) % 360;
                    if (outPage.Rotate != expectedEffectiveRotate)
                    {
                        throw new InvalidOperationException(
                            $"Verification mismatch on page {i + 1}: expected rotation {expectedEffectiveRotate}° but found {outPage.Rotate}°.");
                    }

                    bool isSwapped = (transform.RotationDegrees % 180 != 0);
                    double expectedWidth = isSwapped ? srcPage.Height.Point : srcPage.Width.Point;
                    double expectedHeight = isSwapped ? srcPage.Width.Point : srcPage.Height.Point;

                    if (Math.Abs(expectedWidth - outPage.Width.Point) > 1.0 ||
                        Math.Abs(expectedHeight - outPage.Height.Point) > 1.0)
                    {
                        throw new InvalidOperationException(
                            $"Dimension mismatch on page {i + 1} of organized PDF.");
                    }
                }
            }

            // Atomically promote
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(tempPartial, finalPath);

            // Verify source file SHA-256 remained completely untouched
            string sourceShaAfter = ComputeSha256(request.SourcePdfPath);
            if (!string.Equals(sourceShaBefore, sourceShaAfter, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Source PDF integrity check failed: original file was unexpectedly altered.");
            }

            var finalInfo = new FileInfo(finalPath);
            progress?.Report(new ProcessingProgress(100, $"Successfully exported organized PDF with {request.PageTransforms.Count} page(s)."));

            return new PdfOrganizeResult(
                Succeeded: true,
                WasCancelled: false,
                ErrorReason: null,
                OutputPath: finalPath,
                OutputPageCount: request.PageTransforms.Count,
                FileSizeBytes: finalInfo.Length,
                SummaryMessage: $"Successfully organized and saved {request.PageTransforms.Count} page(s) to '{Path.GetFileName(finalPath)}'.");
        }
        catch (OperationCanceledException)
        {
            CleanupFile(tempPartial);
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: true,
                ErrorReason: "The organize operation was cancelled by the user.",
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: "Operation cancelled. Temporary files were cleaned up.");
        }
        catch (Exception ex)
        {
            CleanupFile(tempPartial);
            return new PdfOrganizeResult(
                Succeeded: false,
                WasCancelled: false,
                ErrorReason: ex.Message,
                OutputPath: string.Empty,
                OutputPageCount: 0,
                FileSizeBytes: 0,
                SummaryMessage: $"PDF organize failed: {ex.Message}");
        }
    }

    private static void CleanupFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string ResolveCollisionFreePath(string directory, string desiredFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(desiredFileName);
        string ext = Path.GetExtension(desiredFileName);

        string candidate = Path.Combine(directory, desiredFileName);
        int counter = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({counter}){ext}");
            counter++;
        }

        return candidate;
    }
}
