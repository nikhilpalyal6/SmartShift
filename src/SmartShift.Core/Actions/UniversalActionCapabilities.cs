namespace SmartShift.Core.Actions;

/// <summary>
/// Universal action capabilities for all verified formats across the application.
/// </summary>
public sealed class UniversalActionCapabilities : IActionCapabilities
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
            or ActionIds.PreviewPdf
            or ActionIds.MergePdf
            or ActionIds.SplitPdf
            or ActionIds.OrganizePdf
            or ActionIds.ShowDocumentInfo
            or ActionIds.ImportToPresentationStudio
            or ActionIds.ConvertDocxToPdf
            or ActionIds.ConvertDocxToPptx
            or ActionIds.CompressDocx
            or ActionIds.UnifiedConvert;
}
