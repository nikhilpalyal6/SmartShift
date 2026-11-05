using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed class PdfMergeEngine : IPdfMergeService
{
    private readonly IOutputPathAllocator _allocator;

    public PdfMergeEngine(IOutputPathAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    public async Task<PdfMergeItemValidation> ValidateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() => ValidateFileInternal(filePath), cancellationToken).ConfigureAwait(false);
    }

    private static PdfMergeItemValidation ValidateFileInternal(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new PdfMergeItemValidation(
                filePath,
                fileName,
                0,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: "File does not exist or is inaccessible.");
        }

        long size;
        try
        {
            var info = new FileInfo(filePath);
            size = info.Length;
            if (size == 0)
            {
                return new PdfMergeItemValidation(
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
            return new PdfMergeItemValidation(
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
                return new PdfMergeItemValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: true,
                    ErrorReason: "This PDF is encrypted or password-protected and cannot be merged.");
            }

            int pageCount = doc.PageCount;
            if (pageCount <= 0)
            {
                return new PdfMergeItemValidation(
                    filePath,
                    fileName,
                    size,
                    0,
                    IsValid: false,
                    IsEncrypted: false,
                    ErrorReason: "The PDF document contains 0 pages.");
            }

            return new PdfMergeItemValidation(
                filePath,
                fileName,
                size,
                pageCount,
                IsValid: true,
                IsEncrypted: false,
                ErrorReason: null);
        }
        catch (PdfReaderException ex)
        {
            return new PdfMergeItemValidation(
                filePath,
                fileName,
                size,
                0,
                IsValid: false,
                IsEncrypted: true,
                ErrorReason: $"Encrypted or password-protected PDF: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new PdfMergeItemValidation(
                filePath,
                fileName,
                size,
                0,
                IsValid: false,
                IsEncrypted: false,
                ErrorReason: $"Corrupt or invalid PDF structure: {ex.Message}");
        }
    }

    public async Task<PdfMergeQueueValidation> ValidateQueueAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            var pathList = filePaths?.ToList() ?? [];
            if (pathList.Count == 0)
            {
                return new PdfMergeQueueValidation(
                    [],
                    TotalValidFiles: 0,
                    TotalPages: 0,
                    CanMerge: false,
                    OverallStatus: "Queue is empty. Please add at least 2 PDF documents to merge.");
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<PdfMergeItemValidation>();
            int totalPages = 0;
            int totalValid = 0;

            foreach (var path in pathList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = ValidateFileInternal(path);
                bool isDuplicate = !seenPaths.Add(path);

                if (isDuplicate)
                {
                    item = item with { IsDuplicate = true };
                }

                if (item.IsValid)
                {
                    totalValid++;
                    totalPages += item.PageCount;
                }

                items.Add(item);
            }

            bool allValid = items.All(i => i.IsValid);
            bool hasMinFiles = items.Count >= 2;
            bool canMerge = allValid && hasMinFiles;

            string status = !hasMinFiles
                ? $"Queue contains only {items.Count} file(s). At least 2 PDF documents are required to merge."
                : (!allValid
                    ? "Some files in the queue are invalid, corrupt, or password-protected. Remove or resolve them before merging."
                    : $"Ready to merge {items.Count} documents ({totalPages} total pages).");

            return new PdfMergeQueueValidation(
                items,
                totalValid,
                totalPages,
                canMerge,
                status);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> MergeAsync(
        IReadOnlyList<string> orderedFilePaths,
        string outputPath,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedFilePaths);
        if (orderedFilePaths.Count == 0)
        {
            throw new InvalidOperationException("Cannot merge an empty queue. At least 2 PDF documents are required.");
        }

        if (orderedFilePaths.Count < 2)
        {
            throw new InvalidOperationException("At least 2 PDF documents are required to perform a merge.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path must be specified.", nameof(outputPath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() => MergeInternal(orderedFilePaths, outputPath, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string MergeInternal(
        IReadOnlyList<string> orderedFilePaths,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Pre-validate every file
        progress?.Report(new ProcessingProgress(5, "Validating input PDF documents…"));
        int expectedTotalPages = 0;
        for (int i = 0; i < orderedFilePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = orderedFilePaths[i];
            var validation = ValidateFileInternal(filePath);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"Cannot merge: File '{validation.FileName}' is invalid ({validation.ErrorReason ?? "unreadable"}). Original files were not modified.");
            }
            expectedTotalPages += validation.PageCount;
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        try
        {
            progress?.Report(new ProcessingProgress(15, "Initializing merged PDF document…"));

            using (var outputDoc = new PdfDocument())
            {
                outputDoc.Info.Title = Path.GetFileNameWithoutExtension(outputPath);
                outputDoc.Info.Creator = "SmartShift PDF Merge Studio";
                outputDoc.Options.CompressContentStreams = true;

                int mergedPagesSoFar = 0;
                for (int i = 0; i < orderedFilePaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var filePath = orderedFilePaths[i];
                    var fileName = Path.GetFileName(filePath);

                    int pct = 20 + (int)(65.0 * (i + 1) / orderedFilePaths.Count);
                    progress?.Report(new ProcessingProgress(pct, $"Importing {fileName} ({i + 1} of {orderedFilePaths.Count})…"));

                    using var inStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var inputDoc = PdfReader.Open(inStream, PdfDocumentOpenMode.Import);

                    for (int pageIdx = 0; pageIdx < inputDoc.PageCount; pageIdx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = inputDoc.Pages[pageIdx];
                        outputDoc.AddPage(page);
                        mergedPagesSoFar++;
                    }
                }

                progress?.Report(new ProcessingProgress(88, "Saving merged PDF document…"));
                outputDoc.Save(tempPartial);
            }

            // 2. Validate the generated output document before reporting success
            progress?.Report(new ProcessingProgress(95, "Validating output PDF integrity…"));
            if (!File.Exists(tempPartial) || new FileInfo(tempPartial).Length == 0)
            {
                throw new InvalidOperationException("Merged output file was not generated or is empty (0 bytes).");
            }

            using (var verifyStream = new FileStream(tempPartial, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var verifyDoc = PdfReader.Open(verifyStream, PdfDocumentOpenMode.Import))
            {
                if (verifyDoc.PageCount != expectedTotalPages)
                {
                    throw new InvalidOperationException(
                        $"Output validation failure: expected {expectedTotalPages} pages but generated PDF has {verifyDoc.PageCount} pages.");
                }
            }

            // 3. Atomically commit
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(tempPartial, outputPath);

            progress?.Report(new ProcessingProgress(100, "PDF merge completed successfully."));
            return outputPath;
        }
        catch
        {
            if (File.Exists(tempPartial))
            {
                try { File.Delete(tempPartial); } catch { }
            }
            throw;
        }
    }
}
