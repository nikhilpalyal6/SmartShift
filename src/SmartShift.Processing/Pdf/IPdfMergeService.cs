using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed record PdfMergeItemValidation(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    int PageCount,
    bool IsValid,
    bool IsEncrypted,
    string? ErrorReason,
    bool IsDuplicate = false);

public sealed record PdfMergeQueueValidation(
    IReadOnlyList<PdfMergeItemValidation> Items,
    int TotalValidFiles,
    int TotalPages,
    bool CanMerge,
    string? OverallStatus);

public interface IPdfMergeService
{
    Task<PdfMergeItemValidation> ValidateFileAsync(string filePath, CancellationToken cancellationToken = default);

    Task<PdfMergeQueueValidation> ValidateQueueAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    Task<string> MergeAsync(
        IReadOnlyList<string> orderedFilePaths,
        string outputPath,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
