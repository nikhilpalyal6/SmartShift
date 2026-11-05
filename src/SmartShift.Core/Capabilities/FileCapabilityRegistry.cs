using System.Text;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;

namespace SmartShift.Core.Capabilities;

/// <summary>
/// Central registry of all file formats, their verified capabilities, and existing operation routes.
/// </summary>
public sealed class FileCapabilityRegistry : IFileCapabilityRegistry
{
    private readonly Dictionary<DetectedFormat, FileTypeDescriptor> _descriptors;
    private readonly Dictionary<string, FileTypeDescriptor> _extensionLookup;

    public FileCapabilityRegistry()
    {
        var descriptors = BuildDescriptors();
        _descriptors = descriptors.ToDictionary(d => d.Format);
        _extensionLookup = new Dictionary<string, FileTypeDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var desc in descriptors)
        {
            foreach (var ext in desc.Extensions)
            {
                _extensionLookup[ext] = desc;
            }
        }
    }

    public IReadOnlyList<FileTypeDescriptor> GetAllDescriptors() => _descriptors.Values.ToList();

    public FileTypeDescriptor GetDescriptor(DetectedFormat format) =>
        _descriptors.TryGetValue(format, out var descriptor)
            ? descriptor
            : _descriptors[DetectedFormat.Unknown];

    public FileTypeDescriptor? FindDescriptorByExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return _extensionLookup.TryGetValue(ext, out var desc) ? desc : null;
    }

    public bool IsFormatSupported(DetectedFormat format) =>
        _descriptors.TryGetValue(format, out var desc) && desc.IsSupported;

    public bool IsExtensionSupported(string? extension)
    {
        var desc = FindDescriptorByExtension(extension);
        return desc is not null && desc.IsSupported;
    }

    public PreviewCapability GetPreviewCapability(DetectedFormat format) =>
        GetDescriptor(format).Preview;

    public IReadOnlyList<string> GetAvailableActionIds(DetectedFormat format) =>
        GetDescriptor(format).SupportedActionIds;

    public IReadOnlyList<DetectedFormat> GetSupportedOutputFormats(DetectedFormat sourceFormat) =>
        GetDescriptor(sourceFormat).SupportedConversionOutputs;

    public string GetFileFilter(bool includeAllFiles = true)
    {
        var supportedExts = _descriptors.Values
            .Where(d => d.IsSupported)
            .SelectMany(d => d.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e)
            .ToList();

        var pattern = string.Join(";", supportedExts.Select(e => "*" + e));
        var sb = new StringBuilder();
        sb.Append($"Supported Files ({pattern})|{pattern}");

        // Group filters
        var imgExts = string.Join(";", GetDescriptor(DetectedFormat.Png).Extensions
            .Concat(GetDescriptor(DetectedFormat.Jpeg).Extensions)
            .Concat(GetDescriptor(DetectedFormat.Webp).Extensions)
            .Select(e => "*" + e));
        sb.Append($"|Image Files ({imgExts})|{imgExts}");

        sb.Append("|PDF Documents (*.pdf)|*.pdf");
        sb.Append("|Word Documents (*.docx)|*.docx");
        sb.Append("|PowerPoint Presentations (*.pptx)|*.pptx");
        sb.Append("|Excel Spreadsheets (*.xlsx)|*.xlsx");
        sb.Append("|Text Documents (*.txt;*.md)|*.txt;*.text;*.md;*.markdown");

        if (includeAllFiles)
        {
            sb.Append("|All Files (*.*)|*.*");
        }

        return sb.ToString();
    }

    private static List<FileTypeDescriptor> BuildDescriptors() =>
    [
        new(
            Format: DetectedFormat.Png,
            Category: FileCategory.Image,
            DisplayName: "PNG Image",
            Description: "Portable Network Graphics with lossless compression and alpha channel support.",
            Extensions: [".png"],
            PrimaryExtension: ".png",
            Preview: PreviewCapability.Image,
            SupportedActionIds:
            [
                ActionIds.ShowImageInfo,
                ActionIds.CompressImage,
                ActionIds.ConvertToWebp,
                ActionIds.ConvertToJpeg,
                ActionIds.ResizeImage,
                ActionIds.RemoveImageMetadata,
                ActionIds.EditImage
            ],
            SupportedConversionOutputs: [DetectedFormat.Webp, DetectedFormat.Jpeg],
            CanEdit: true,
            CanImport: true,
            CanExport: true,
            IsSupported: true),

        new(
            Format: DetectedFormat.Jpeg,
            Category: FileCategory.Image,
            DisplayName: "JPEG Image",
            Description: "Joint Photographic Experts Group lossy image format with EXIF orientation metadata.",
            Extensions: [".jpg", ".jpeg"],
            PrimaryExtension: ".jpg",
            Preview: PreviewCapability.Image,
            SupportedActionIds:
            [
                ActionIds.ShowImageInfo,
                ActionIds.CompressImage,
                ActionIds.ConvertToWebp,
                ActionIds.ConvertToPng,
                ActionIds.ResizeImage,
                ActionIds.RemoveImageMetadata,
                ActionIds.EditImage
            ],
            SupportedConversionOutputs: [DetectedFormat.Webp, DetectedFormat.Png],
            CanEdit: true,
            CanImport: true,
            CanExport: true,
            IsSupported: true),

        new(
            Format: DetectedFormat.Webp,
            Category: FileCategory.Image,
            DisplayName: "WebP Image",
            Description: "Modern web image format supporting both lossy and lossless compression with transparency.",
            Extensions: [".webp"],
            PrimaryExtension: ".webp",
            Preview: PreviewCapability.Image,
            SupportedActionIds:
            [
                ActionIds.ShowImageInfo,
                ActionIds.CompressImage,
                ActionIds.ConvertToPng,
                ActionIds.ConvertToJpeg,
                ActionIds.ResizeImage,
                ActionIds.RemoveImageMetadata,
                ActionIds.EditImage
            ],
            SupportedConversionOutputs: [DetectedFormat.Png, DetectedFormat.Jpeg],
            CanEdit: true,
            CanImport: true,
            CanExport: true,
            IsSupported: true),

        new(
            Format: DetectedFormat.Pdf,
            Category: FileCategory.Document,
            DisplayName: "PDF Document",
            Description: "Portable Document Format with multi-page vector layout, text streams, and embedded media.",
            Extensions: [".pdf"],
            PrimaryExtension: ".pdf",
            Preview: PreviewCapability.Pdf,
            SupportedActionIds:
            [
                ActionIds.ShowPdfInfo,
                ActionIds.CompressPdf,
                ActionIds.ExportPdfPages,
                ActionIds.ExtractPdfText,
                ActionIds.ConvertPdfToDocx,
                ActionIds.ConvertPdfToPptx,
                ActionIds.PreviewPdf,
                ActionIds.MergePdf,
                ActionIds.SplitPdf,
                ActionIds.ImportToPresentationStudio
            ],
            SupportedConversionOutputs:
            [
                DetectedFormat.Docx,
                DetectedFormat.Pptx,
                DetectedFormat.Txt,
                DetectedFormat.Png,
                DetectedFormat.Jpeg
            ],
            CanEdit: false,
            CanImport: true,
            CanExport: true,
            IsSupported: true),

        new(
            Format: DetectedFormat.Docx,
            Category: FileCategory.Document,
            DisplayName: "Word Document",
            Description: "Microsoft OpenXML Word Document (.docx). Supports structured preview, PDF/PPTX conversion, compression, and Presentation Studio.",
            Extensions: [".docx"],
            PrimaryExtension: ".docx",
            Preview: PreviewCapability.Text,
            SupportedActionIds:
            [
                ActionIds.ShowDocumentInfo,
                ActionIds.ConvertDocxToPdf,
                ActionIds.ConvertDocxToPptx,
                ActionIds.CompressDocx,
                ActionIds.ImportToPresentationStudio
            ],
            SupportedConversionOutputs:
            [
                DetectedFormat.Pdf,
                DetectedFormat.Pptx
            ],
            CanEdit: false,
            CanImport: true,
            CanExport: true,
            IsSupported: true),

        new(
            Format: DetectedFormat.Txt,
            Category: FileCategory.Document,
            DisplayName: "Plain Text Document",
            Description: "Unformatted text document in UTF-8 or ASCII encoding. Can be imported into Presentation Studio.",
            Extensions: [".txt", ".text"],
            PrimaryExtension: ".txt",
            Preview: PreviewCapability.Text,
            SupportedActionIds:
            [
                ActionIds.ShowDocumentInfo,
                ActionIds.ImportToPresentationStudio
            ],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: true,
            CanExport: false,
            IsSupported: true),

        new(
            Format: DetectedFormat.Markdown,
            Category: FileCategory.Document,
            DisplayName: "Markdown Document",
            Description: "Structured lightweight markup document. Can be imported into Presentation Studio with heading hierarchy.",
            Extensions: [".md", ".markdown"],
            PrimaryExtension: ".md",
            Preview: PreviewCapability.Text,
            SupportedActionIds:
            [
                ActionIds.ShowDocumentInfo,
                ActionIds.ImportToPresentationStudio
            ],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: true,
            CanExport: false,
            IsSupported: true),

        new(
            Format: DetectedFormat.Pptx,
            Category: FileCategory.Presentation,
            DisplayName: "PowerPoint Presentation",
            Description: "Microsoft OpenXML PowerPoint Presentation (.pptx). Can be generated from PDF and Presentation Studio.",
            Extensions: [".pptx"],
            PrimaryExtension: ".pptx",
            Preview: PreviewCapability.None,
            SupportedActionIds: [ActionIds.ShowDocumentInfo],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: true,
            IsSupported: true,
            UnsupportedReason: "PowerPoint presentations can be generated and exported, but modifying an existing presentation is not currently supported."),

        new(
            Format: DetectedFormat.Xlsx,
            Category: FileCategory.Spreadsheet,
            DisplayName: "Excel Spreadsheet",
            Description: "Microsoft OpenXML Excel Workbook (.xlsx). Recognized format with safe inspection.",
            Extensions: [".xlsx"],
            PrimaryExtension: ".xlsx",
            Preview: PreviewCapability.None,
            SupportedActionIds: [ActionIds.ShowDocumentInfo],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "Excel spreadsheet processing and preview are not currently supported in this version."),

        new(
            Format: DetectedFormat.Gif,
            Category: FileCategory.Image,
            DisplayName: "GIF Image",
            Description: "Graphics Interchange Format. Recognized by signature; processing not supported in this version.",
            Extensions: [".gif"],
            PrimaryExtension: ".gif",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "SmartShift does not support GIF processing or conversion yet."),

        new(
            Format: DetectedFormat.Bmp,
            Category: FileCategory.Image,
            DisplayName: "BMP Image",
            Description: "Bitmap Image File. Recognized by signature; processing not supported in this version.",
            Extensions: [".bmp"],
            PrimaryExtension: ".bmp",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "SmartShift does not support BMP processing or conversion yet."),

        new(
            Format: DetectedFormat.Tiff,
            Category: FileCategory.Image,
            DisplayName: "TIFF Image",
            Description: "Tagged Image File Format. Recognized by signature; processing not supported in this version.",
            Extensions: [".tif", ".tiff"],
            PrimaryExtension: ".tif",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "SmartShift does not support TIFF processing or conversion yet."),

        new(
            Format: DetectedFormat.Zip,
            Category: FileCategory.Archive,
            DisplayName: "ZIP Archive",
            Description: "Standard ZIP Archive. Inspected for package headers; generic decompression is not supported.",
            Extensions: [".zip"],
            PrimaryExtension: ".zip",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "Generic ZIP archive extraction or conversion is not supported."),

        new(
            Format: DetectedFormat.Empty,
            Category: FileCategory.Other,
            DisplayName: "Empty File",
            Description: "A zero-byte file containing no data.",
            Extensions: [],
            PrimaryExtension: "",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "Empty files contain no data to process."),

        new(
            Format: DetectedFormat.Unknown,
            Category: FileCategory.Unknown,
            DisplayName: "Unknown Format",
            Description: "Unrecognized or unsupported binary format.",
            Extensions: [],
            PrimaryExtension: "",
            Preview: PreviewCapability.None,
            SupportedActionIds: [],
            SupportedConversionOutputs: [],
            CanEdit: false,
            CanImport: false,
            CanExport: false,
            IsSupported: false,
            UnsupportedReason: "The file format was not recognized or is not supported by SmartShift.")
    ];
}
