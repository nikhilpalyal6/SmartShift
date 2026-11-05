using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Pdf;

public sealed record PdfPageTransform(
    int OriginalPageIndex,
    int RotationDegrees);

public sealed record PdfOrganizeValidation(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    int TotalPages,
    bool IsValid,
    bool IsEncrypted,
    string? ErrorReason,
    IReadOnlyList<int>? PageRotations = null,
    IReadOnlyList<(double WidthPt, double HeightPt)>? PageDimensions = null);

public sealed record PdfOrganizeRequest(
    string SourcePdfPath,
    string? OutputDirectory,
    IReadOnlyList<PdfPageTransform> PageTransforms,
    string? CustomOutputFileName = null);

public sealed record PdfOrganizeResult(
    bool Succeeded,
    bool WasCancelled,
    string? ErrorReason,
    string OutputPath,
    int OutputPageCount,
    long FileSizeBytes,
    string SummaryMessage);

public interface IPdfPageOrganizerService
{
    Task<PdfOrganizeValidation> ValidateSourceAsync(string sourcePdfPath, CancellationToken cancellationToken = default);

    Task<PdfOrganizeResult> OrganizeAsync(
        PdfOrganizeRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
