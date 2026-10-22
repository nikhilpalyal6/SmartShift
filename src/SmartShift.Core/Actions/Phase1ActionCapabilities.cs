namespace SmartShift.Core.Actions;

/// <summary>
/// Phase 1 image transforms and local PDF compression are available. PDF preview is not registered.
/// </summary>
public sealed class Phase1ActionCapabilities : IActionCapabilities
{
    public bool IsAvailable(string actionId)
        => actionId is ActionIds.ShowImageInfo
            or ActionIds.ShowPdfInfo
            or ActionIds.CompressImage
            or ActionIds.ConvertToWebp
            or ActionIds.ConvertToPng
            or ActionIds.ConvertToJpeg
            or ActionIds.ResizeImage
            or ActionIds.RemoveImageMetadata
            or ActionIds.EditImage
            or ActionIds.CompressPdf
            or ActionIds.ExportPdfPages
            or ActionIds.ExtractPdfText
            or ActionIds.ConvertPdfToDocx
            or ActionIds.ConvertPdfToPptx
            or ActionIds.PreviewPdf;
}
