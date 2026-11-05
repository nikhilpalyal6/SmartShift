using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public enum PdfSplitMode
{
    ExtractRanges = 0,
    SplitEveryPage = 1
}

public sealed record PdfSplitValidation(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    int TotalPages,
    bool IsValid,
    bool IsEncrypted,
    string? ErrorReason);

public sealed record PdfSplitItemResult(
    string OutputPath,
    IReadOnlyList<int> SourcePages,
    long FileSizeBytes,
    int PageCount);

public sealed record PdfSplitRequest(
    string SourcePdfPath,
    string OutputDirectory,
    PdfSplitMode Mode,
    IReadOnlyList<PdfPageRangeItem>? Ranges = null);

public sealed record PdfSplitResult(
    bool Succeeded,
    bool WasCancelled,
    string? ErrorReason,
    IReadOnlyList<PdfSplitItemResult> GeneratedFiles,
    string OutputDirectory,
    int TotalSourcePages,
    string SummaryMessage);

public interface IPdfSplitService
{
    Task<PdfSplitValidation> ValidateSourceAsync(string sourcePdfPath, CancellationToken cancellationToken = default);

    Task<PdfSplitResult> SplitAsync(
        PdfSplitRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
