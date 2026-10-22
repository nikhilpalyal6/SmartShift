namespace SmartShift.Core.Actions;

public static class ActionIds
{
    public const string ShowImageInfo = "image.info";
    public const string CompressImage = "image.compress";
    public const string ConvertToWebp = "image.convert.webp";
    public const string ConvertToPng = "image.convert.png";
    public const string ConvertToJpeg = "image.convert.jpeg";
    public const string ResizeImage = "image.resize";
    public const string RemoveImageMetadata = "image.metadata.strip";
    public const string EditImage = "image.edit";

    public const string ShowPdfInfo = "pdf.info";
    public const string CompressPdf = "pdf.compress";
    public const string ExportPdfPages = "pdf.export.pages";
    public const string ExtractPdfText = "pdf.extract.text";
    public const string ConvertPdfToDocx = "pdf.convert.docx";
    public const string ConvertPdfToPptx = "pdf.convert.pptx";
    public const string PreviewPdf = "pdf.preview";
}

public static class GoalIds
{
    public const string Inspect = "goal.inspect";
    public const string ReduceImageForUpload = "goal.image.reduce-upload";
    public const string ConvertImageForWeb = "goal.image.web";
    public const string ResizeImage = "goal.image.resize";
    public const string EditImage = "goal.image.edit";
    public const string ReducePdfForSharing = "goal.pdf.reduce-share";
    public const string ExportPdfPages = "goal.pdf.export-pages";
    public const string ExtractPdfText = "goal.pdf.extract-text";
    public const string ConvertPdfToDocx = "goal.pdf.convert-docx";
    public const string ConvertPdfToPptx = "goal.pdf.convert-pptx";
}
