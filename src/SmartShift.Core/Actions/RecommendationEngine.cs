using SmartShift.Core.Classification;

namespace SmartShift.Core.Actions;

public sealed class RecommendationEngine
{
    private readonly IActionCapabilities _capabilities;

    public RecommendationEngine(IActionCapabilities capabilities)
    {
        _capabilities = capabilities;
    }

    public IReadOnlyList<ActionRecommendation> Recommend(ClassifiedFile file)
    {
        if (file.Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp
            && file.Issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch)
        {
            return ImageActions(file.Format);
        }

        if (file.Format == DetectedFormat.Pdf
            && file.Issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch)
        {
            return PdfActions();
        }

        return [];
    }

    private IReadOnlyList<ActionRecommendation> ImageActions(DetectedFormat format)
    {
        var formats = new[] { DetectedFormat.Png, DetectedFormat.Jpeg, DetectedFormat.Webp };
        return
        [
            Create(
                ActionIds.ShowImageInfo,
                "Show image information",
                "Display detected format, size, and dimensions from the file header. The original file is not changed.",
                GoalIds.Inspect,
                "Inspect this file",
                formats,
                [],
                availableNote: "Inspection only. No new file is written."),
            Create(
                ActionIds.CompressImage,
                "Compress image",
                "Re-encode JPEG, PNG, or WebP locally. JPEG/WebP quality is lossy. PNG stays lossless (quality maps to zlib effort). Output size is measured after encoding — not guaranteed in advance.",
                GoalIds.ReduceImageForUpload,
                "Reduce image size for uploading",
                formats,
                QualityOptions()),
            Create(
                ActionIds.ConvertToWebp,
                "Convert to WebP",
                "Create a WebP copy for web use. Alpha is preserved when present. The original stays in place.",
                GoalIds.ConvertImageForWeb,
                "Convert an image for web use",
                formats,
                QualityOptions()),
            Create(
                ActionIds.ResizeImage,
                "Resize image",
                "Create a resized copy at the dimensions you choose (max 8192px per side). Mitchell resampling is used.",
                GoalIds.ResizeImage,
                "Resize an image to specific dimensions",
                formats,
                ResizeOptions()),
            Create(
                ActionIds.RemoveImageMetadata,
                "Remove metadata",
                "Write a re-encoded copy so source EXIF/GPS/XMP is not copied. Orientation is applied to pixels first. Not a guarantee that every metadata channel is gone.",
                GoalIds.Inspect,
                "Inspect this file",
                formats,
                []),
            Create(
                ActionIds.EditImage,
                "Edit image",
                "Interactive image editor to crop, rotate, flip, and resize before saving. Original file remains untouched.",
                GoalIds.EditImage,
                "Edit image interactively",
                formats,
                [])
        ];
    }

    private IReadOnlyList<ActionRecommendation> PdfActions()
    {
        var formats = new[] { DetectedFormat.Pdf };
        return
        [
            Create(
                ActionIds.ShowPdfInfo,
                "Show PDF information",
                "Display detected PDF version, page count, dimensions, encryption status, and metadata. The original file is not changed.",
                GoalIds.Inspect,
                "Inspect this file",
                formats,
                [],
                availableNote: "Inspection only. No new file is written."),
            Create(
                ActionIds.CompressPdf,
                "Compress PDF",
                "Reduce PDF file size toward a target size (500 KB, 1 MB, 2 MB, 5 MB, or custom). Employs lossless stream deflation and optional embedded image optimization. Originals are never modified.",
                GoalIds.ReducePdfForSharing,
                "Reduce PDF size for sharing",
                formats,
                PdfCompressOptions()),
            Create(
                ActionIds.ExportPdfPages,
                "Export Pages (PNG/JPEG)",
                "Render and export document pages as individual PNG or JPEG images at 100, 150, or 300 DPI. Original file is never modified.",
                GoalIds.ExportPdfPages,
                "Export PDF pages as images",
                formats,
                PdfExportOptions()),
            Create(
                ActionIds.ExtractPdfText,
                "Extract Text (TXT)",
                "Extract all selectable text in document order with page separators into a UTF-8 text file. Original file is never modified.",
                GoalIds.ExtractPdfText,
                "Extract text from PDF",
                formats,
                PdfTextOptions()),
            Create(
                ActionIds.ConvertPdfToDocx,
                "Convert to Word (DOCX)",
                "Convert document into editable Microsoft Word (.docx) format, reconstructing paragraphs, text runs, and embedded graphics. Original file is never modified.",
                GoalIds.ConvertPdfToDocx,
                "Convert PDF to Word",
                formats,
                PdfDocxOptions()),
            Create(
                ActionIds.ConvertPdfToPptx,
                "Convert to PowerPoint (PPTX)",
                "Convert document into Microsoft PowerPoint (.pptx) presentation. Supports high-fidelity Page-as-Image and Editable element slide modes. Original file is never modified.",
                GoalIds.ConvertPdfToPptx,
                "Convert PDF to PowerPoint",
                formats,
                PdfPptxOptions()),
            Create(
                ActionIds.PreviewPdf,
                "Preview PDF",
                "Open an interactive, native preview of the document with page navigation, zoom, and fit controls.",
                GoalIds.Inspect,
                "Preview document pages",
                formats,
                [],
                availableNote: "Interactive preview with page navigation and zoom.")
        ];
    }

    private ActionRecommendation Create(
        string id,
        string title,
        string description,
        string goalId,
        string goalTitle,
        IReadOnlyList<DetectedFormat> formats,
        IReadOnlyList<ActionOptionDefinition> options,
        string? unavailableReason = null,
        string? availableNote = null,
        IReadOnlyList<string>? limitations = null)
    {
        var isAvailable = _capabilities.IsAvailable(id);
        var note = (isAvailable ? availableNote : unavailableReason) ?? "";

        return new ActionRecommendation(
            id,
            title,
            description,
            goalId,
            goalTitle,
            formats,
            isAvailable,
            note,
            options,
            limitations ?? []);
    }

    private static IReadOnlyList<ActionOptionDefinition> QualityOptions() =>
    [
        new(
            "quality",
            "Quality",
            "Quality value from 1 to 100",
            "integer",
            "80",
            "1-100")
    ];

    private static IReadOnlyList<ActionOptionDefinition> ResizeOptions() =>
    [
        new(
            "width",
            "Width",
            "Target width in pixels",
            "integer",
            "1920",
            "1-8192"),
        new(
            "height",
            "Height",
            "Target height in pixels",
            "integer",
            "1080",
            "1-8192"),
        new(
            "keepAspect",
            "Keep aspect ratio",
            "Preserve width-to-height ratio",
            "boolean",
            "true",
            "true/false")
    ];

    private static IReadOnlyList<ActionOptionDefinition> PdfCompressOptions() =>
    [
        new(
            "targetSize",
            "Target size",
            "Target output file size value",
            "number",
            "1",
            "Positive number"),
        new(
            "targetUnit",
            "Target unit",
            "Unit for target file size (KB or MB)",
            "string",
            "MB",
            "KB or MB"),
        new(
            "imageQuality",
            "Image quality policy",
            "Embedded image optimization policy (off, best, balanced, smallest)",
            "string",
            "balanced",
            "off, best, balanced, smallest")
    ];

    private static IReadOnlyList<ActionOptionDefinition> PdfExportOptions() =>
    [
        new("format", "Image Format", "Export page format (PNG or JPEG).", "enum", "png", "Must be 'png' or 'jpeg'."),
        new("dpi", "Resolution (DPI)", "Render resolution in dots per inch (100, 150, or 300 DPI).", "enum", "150", "Must be 100, 150, or 300."),
        new("quality", "JPEG Quality", "JPEG compression quality (1-100). Used only for JPEG format.", "int", "85", "Value between 1 and 100."),
        new("pageRange", "Page Range", "Pages to export ('all' or e.g. '1-5, 8').", "string", "all", "Valid page numbers or ranges.")
    ];

    private static IReadOnlyList<ActionOptionDefinition> PdfTextOptions() =>
    [
        new("pageRange", "Page Range", "Pages to extract ('all' or e.g. '1-5, 8').", "string", "all", "Valid page numbers or ranges.")
    ];

    private static IReadOnlyList<ActionOptionDefinition> PdfDocxOptions() =>
    [
        new("pageRange", "Page Range", "Pages to convert ('all' or e.g. '1-5, 8').", "string", "all", "Valid page numbers or ranges.")
    ];

    private static IReadOnlyList<ActionOptionDefinition> PdfPptxOptions() =>
    [
        new("slideMode", "Slide Mode", "Slide generation mode ('pageAsImage' for pixel-accurate layout or 'editable' for reconstructed text/shapes).", "enum", "pageAsImage", "Must be 'pageAsImage' or 'editable'."),
        new("pageRange", "Page Range", "Pages to convert ('all' or e.g. '1-5, 8').", "string", "all", "Valid page numbers or ranges.")
    ];
}
