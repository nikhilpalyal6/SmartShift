using System.IO.Compression;
using System.Text;

namespace SmartShift.Core.Classification;

/// <summary>
/// Deterministic header-based and package-validated classification.
/// Reads a small prefix or package directory only; never loads entire large files into memory or executes files.
/// </summary>
public sealed class FileClassifier : IFileTypeDetector
{
    private const int HeaderSize = 64;
    private const int JpegScanLimit = 65_536;
    private const int TextInspectionLimit = 4096;

    public ClassifiedFile Classify(string path)
    {
        var result = Detect(path);
        return new ClassifiedFile(
            Path: result.Metadata.Path,
            FileName: result.Metadata.FileName,
            Extension: result.Metadata.Extension,
            LengthBytes: result.Metadata.LengthBytes,
            LastWriteTimeUtc: result.Metadata.LastWriteTimeUtc,
            Format: result.Format,
            ExtensionHint: result.ExtensionHint,
            Issue: result.Issue,
            Status: result.Status,
            PixelWidth: result.Metadata.PixelWidth,
            PixelHeight: result.Metadata.PixelHeight,
            PdfVersion: result.Metadata.DocumentVersion,
            ExplicitCategory: result.Category,
            ExplicitMetadata: result.Metadata);
    }

    public FileDetectionResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return MissingResult("(empty path)", "", "", ClassificationIssue.Missing, "No path was provided.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return MissingResult(path, Path.GetFileName(path), Path.GetExtension(path), ClassificationIssue.Inaccessible, $"The path could not be resolved. {ex.Message}");
        }

        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var hint = HintFromExtension(extension);

        if (Directory.Exists(fullPath))
        {
            var dirMeta = FileMetadata.Empty(fullPath, fileName, extension);
            return new FileDetectionResult(
                Format: DetectedFormat.Unknown,
                Category: FileCategory.Unknown,
                ExtensionHint: hint,
                Issue: ClassificationIssue.Directory,
                Status: "Folders cannot be dropped. Choose files instead.",
                Metadata: dirMeta,
                IsSupported: false,
                HasContentMismatch: false);
        }

        if (!File.Exists(fullPath))
        {
            return MissingResult(fullPath, fileName, extension, ClassificationIssue.Missing, "The file could not be found.");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
        }
        catch (Exception ex)
        {
            return MissingResult(fullPath, fileName, extension, ClassificationIssue.Inaccessible, $"The file could not be read. {ex.Message}");
        }

        if (info.Length == 0)
        {
            var emptyMeta = new FileMetadata(fullPath, fileName, extension, 0, info.LastWriteTimeUtc);
            return new FileDetectionResult(
                Format: DetectedFormat.Empty,
                Category: FileCategory.Other,
                ExtensionHint: hint,
                Issue: ClassificationIssue.Empty,
                Status: "The file is empty (0 bytes).",
                Metadata: emptyMeta,
                IsSupported: false,
                HasContentMismatch: false);
        }

        byte[] header;
        try
        {
            header = ReadPrefix(fullPath, HeaderSize);
        }
        catch (Exception ex)
        {
            var inaccMeta = new FileMetadata(fullPath, fileName, extension, info.Length, info.LastWriteTimeUtc);
            return new FileDetectionResult(
                Format: DetectedFormat.Unknown,
                Category: FileCategory.Unknown,
                ExtensionHint: hint,
                Issue: ClassificationIssue.Inaccessible,
                Status: $"The file could not be opened. {ex.Message}",
                Metadata: inaccMeta,
                IsSupported: false,
                HasContentMismatch: false);
        }

        var format = SniffBinaryHeader(header);
        var issue = ClassificationIssue.None;
        string? issueDetail = null;
        int? width = null;
        int? height = null;
        string? pdfVersion = null;

        // If binary signature is PK (Zip), inspect the package structure
        if (format == DetectedFormat.Zip)
        {
            format = InspectZipPackage(fullPath, out var zipIssue, out issueDetail);
            if (zipIssue != ClassificationIssue.None)
            {
                issue = zipIssue;
            }
        }
        else if (format == DetectedFormat.Png)
        {
            TryPngSize(header, out width, out height);
        }
        else if (format == DetectedFormat.Webp)
        {
            TryWebpSize(header, out width, out height);
        }
        else if (format == DetectedFormat.Jpeg)
        {
            TryJpegSize(fullPath, out width, out height);
        }
        else if (format == DetectedFormat.Pdf)
        {
            pdfVersion = ReadPdfVersion(header);
        }
        else if (format == DetectedFormat.Unknown && issue == ClassificationIssue.None)
        {
            // If not a known binary format, evaluate safe text heuristics
            var textCheck = InspectTextContent(fullPath, hint);
            if (textCheck.IsText)
            {
                format = textCheck.TextFormat;
            }
            else if (textCheck.IsBinaryWithTextExtension)
            {
                issue = ClassificationIssue.MalformedFile;
                issueDetail = "The file has a text extension but contains binary data.";
            }
        }

        var category = CategoryFromFormat(format);
        var status = issueDetail ?? $"Detected {FormatName(format)}.";

        if (issue == ClassificationIssue.None)
        {
            if (format == DetectedFormat.Unknown)
            {
                issue = ClassificationIssue.UnknownFormat;
                status = "The file format was not recognized.";
            }
            else if (hint != DetectedFormat.Unknown && hint != format)
            {
                issue = ClassificationIssue.ExtensionContentMismatch;
                status = $"The extension suggests {FormatName(hint)}, but the contents are {FormatName(format)}.";
            }
            else if (format is DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff)
            {
                issue = ClassificationIssue.FormatNotSupported;
                status = $"{FormatName(format)} was detected, but SmartShift does not support this format yet.";
            }
            else if (format == DetectedFormat.Zip)
            {
                issue = ClassificationIssue.FormatNotSupported;
                status = "ZIP archive was detected, but generic archive processing is not supported.";
            }
        }

        var metadata = new FileMetadata(
            Path: fullPath,
            FileName: fileName,
            Extension: extension,
            LengthBytes: info.Length,
            LastWriteTimeUtc: info.LastWriteTimeUtc,
            PixelWidth: width,
            PixelHeight: height,
            DocumentVersion: pdfVersion,
            PageCount: null);

        bool isSupported = (format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp
            or DetectedFormat.Pdf or DetectedFormat.Docx or DetectedFormat.Txt or DetectedFormat.Markdown or DetectedFormat.Pptx)
            && issue is ClassificationIssue.None or ClassificationIssue.ExtensionContentMismatch;

        bool hasMismatch = issue == ClassificationIssue.ExtensionContentMismatch;

        return new FileDetectionResult(
            Format: format,
            Category: category,
            ExtensionHint: hint,
            Issue: issue,
            Status: status,
            Metadata: metadata,
            IsSupported: isSupported,
            HasContentMismatch: hasMismatch);
    }

    public static DetectedFormat HintFromExtension(string? extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".png" => DetectedFormat.Png,
            ".jpg" or ".jpeg" => DetectedFormat.Jpeg,
            ".webp" => DetectedFormat.Webp,
            ".pdf" => DetectedFormat.Pdf,
            ".gif" => DetectedFormat.Gif,
            ".bmp" => DetectedFormat.Bmp,
            ".tif" or ".tiff" => DetectedFormat.Tiff,
            ".docx" => DetectedFormat.Docx,
            ".pptx" => DetectedFormat.Pptx,
            ".xlsx" => DetectedFormat.Xlsx,
            ".txt" or ".text" => DetectedFormat.Txt,
            ".md" or ".markdown" => DetectedFormat.Markdown,
            ".zip" => DetectedFormat.Zip,
            _ => DetectedFormat.Unknown
        };
    }

    public static FileCategory CategoryFromFormat(DetectedFormat format) => format switch
    {
        DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff => FileCategory.Image,
        DetectedFormat.Pdf or DetectedFormat.Docx or DetectedFormat.Txt or DetectedFormat.Markdown => FileCategory.Document,
        DetectedFormat.Pptx => FileCategory.Presentation,
        DetectedFormat.Xlsx => FileCategory.Spreadsheet,
        DetectedFormat.Zip => FileCategory.Archive,
        _ => FileCategory.Unknown
    };

    public static string FormatName(DetectedFormat format) => format switch
    {
        DetectedFormat.Png => "PNG",
        DetectedFormat.Jpeg => "JPEG",
        DetectedFormat.Webp => "WebP",
        DetectedFormat.Pdf => "PDF",
        DetectedFormat.Gif => "GIF",
        DetectedFormat.Bmp => "BMP",
        DetectedFormat.Tiff => "TIFF",
        DetectedFormat.Docx => "Word (DOCX)",
        DetectedFormat.Pptx => "PowerPoint (PPTX)",
        DetectedFormat.Xlsx => "Excel (XLSX)",
        DetectedFormat.Txt => "Plain Text",
        DetectedFormat.Markdown => "Markdown",
        DetectedFormat.Zip => "ZIP Archive",
        DetectedFormat.Empty => "an empty file",
        _ => "an unknown format"
    };

    private static FileDetectionResult MissingResult(string path, string fileName, string extension, ClassificationIssue issue, string status)
    {
        var meta = FileMetadata.Empty(path, fileName, extension);
        return new FileDetectionResult(
            Format: DetectedFormat.Unknown,
            Category: FileCategory.Unknown,
            ExtensionHint: HintFromExtension(extension),
            Issue: issue,
            Status: status,
            Metadata: meta,
            IsSupported: false,
            HasContentMismatch: false);
    }

    private static byte[] ReadPrefix(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[count];
        var read = stream.Read(buffer, 0, count);
        if (read < count)
        {
            Array.Resize(ref buffer, read);
        }

        return buffer;
    }

    private static DetectedFormat SniffBinaryHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x43 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            // Note: 0x4E is 'N', 0x47 is 'G'
        }

        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return DetectedFormat.Png;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return DetectedFormat.Jpeg;
        }

        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return DetectedFormat.Webp;
        }

        if (header.Length >= 5 &&
            header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D' && header[3] == (byte)'F' && header[4] == (byte)'-')
        {
            return DetectedFormat.Pdf;
        }

        if (header.Length >= 6 &&
            header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8' &&
            (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
        {
            return DetectedFormat.Gif;
        }

        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return DetectedFormat.Bmp;
        }

        if (header.Length >= 4 &&
            ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
             (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)))
        {
            return DetectedFormat.Tiff;
        }

        // PK ZIP header: 0x50, 0x4B, followed by 0x03,0x04 or 0x05,0x06 or 0x07,0x08
        if (header.Length >= 4 &&
            header[0] == 0x50 && header[1] == 0x4B &&
            (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07) &&
            (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08))
        {
            return DetectedFormat.Zip;
        }

        return DetectedFormat.Unknown;
    }

    private static DetectedFormat InspectZipPackage(string fullPath, out ClassificationIssue issue, out string? detail)
    {
        issue = ClassificationIssue.None;
        detail = null;

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            bool hasWord = false;
            bool hasPpt = false;
            bool hasXl = false;

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
                {
                    hasWord = true;
                }
                else if (name.StartsWith("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
                {
                    hasPpt = true;
                }
                else if (name.StartsWith("xl/workbook.xml", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    hasXl = true;
                }
            }

            if (hasWord) return DetectedFormat.Docx;
            if (hasPpt) return DetectedFormat.Pptx;
            if (hasXl) return DetectedFormat.Xlsx;

            return DetectedFormat.Zip;
        }
        catch (InvalidDataException ex)
        {
            issue = ClassificationIssue.CorruptedPackage;
            detail = $"Corrupted package: {ex.Message}";
            return DetectedFormat.Unknown;
        }
        catch (Exception ex)
        {
            issue = ClassificationIssue.MalformedFile;
            detail = $"Archive package read error: {ex.Message}";
            return DetectedFormat.Unknown;
        }
    }

    private static (bool IsText, DetectedFormat TextFormat, bool IsBinaryWithTextExtension) InspectTextContent(string fullPath, DetectedFormat hint)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int count = (int)Math.Min(stream.Length, TextInspectionLimit);
            var buffer = new byte[count];
            int read = stream.Read(buffer, 0, count);
            if (read == 0)
            {
                return (false, DetectedFormat.Unknown, false);
            }

            var span = buffer.AsSpan(0, read);
            bool isCleanText = LooksLikePlainText(span);

            if (hint is DetectedFormat.Txt or DetectedFormat.Markdown)
            {
                if (isCleanText)
                {
                    return (true, hint, false);
                }
                else
                {
                    return (false, DetectedFormat.Unknown, true);
                }
            }

            // If the file extension suggests a binary format (PDF, PNG, JPG, DOCX, etc.)
            // but the content is purely plain text, detect it as text for mismatch reporting
            if (hint != DetectedFormat.Unknown && isCleanText)
            {
                return (true, DetectedFormat.Txt, false);
            }

            return (false, DetectedFormat.Unknown, false);
        }
        catch
        {
            return (false, DetectedFormat.Unknown, false);
        }
    }

    private static bool LooksLikePlainText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return false;

        // Check for null bytes or excessive control characters indicating binary
        int nonPrintable = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == 0x00)
            {
                return false; // Null byte indicates binary content
            }

            // Printable ASCII, UTF-8 continuation, or standard whitespace/control (tab, CR, LF)
            if (b < 0x09 || b == 0x0B || b == 0x0C || (b >= 0x0E && b <= 0x1F))
            {
                nonPrintable++;
            }
        }

        // More than 5% control characters indicates binary
        if (nonPrintable > bytes.Length * 0.05)
        {
            return false;
        }

        // Validate UTF-8 encoding
        try
        {
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            utf8.GetCharCount(bytes.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryPngSize(ReadOnlySpan<byte> header, out int? width, out int? height)
    {
        width = null;
        height = null;
        if (header.Length < 24)
        {
            return;
        }

        width = ReadBigEndianInt32(header.Slice(16, 4));
        height = ReadBigEndianInt32(header.Slice(20, 4));
    }

    private static void TryWebpSize(ReadOnlySpan<byte> header, out int? width, out int? height)
    {
        width = null;
        height = null;
        if (header.Length < 30)
        {
            return;
        }

        var fourcc = Encoding.ASCII.GetString(header.Slice(12, 4));
        if (fourcc == "VP8X")
        {
            width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
            height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
        }
    }

    private static void TryJpegSize(string path, out int? width, out int? height)
    {
        width = null;
        height = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var limit = (int)Math.Min(stream.Length, JpegScanLimit);
            var buffer = new byte[limit];
            var read = stream.Read(buffer, 0, limit);
            ParseJpegSof(buffer.AsSpan(0, read), out width, out height);
        }
        catch
        {
            // Size is optional; classification of JPEG already succeeded from the SOI marker.
        }
    }

    private static void ParseJpegSof(ReadOnlySpan<byte> data, out int? width, out int? height)
    {
        width = null;
        height = null;
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            while (i < data.Length && data[i] == 0xFF)
            {
                i++;
            }

            if (i >= data.Length)
            {
                return;
            }

            var marker = data[i++];
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (i + 1 >= data.Length)
            {
                return;
            }

            var length = (data[i] << 8) | data[i + 1];
            if (length < 2 || i + length > data.Length)
            {
                return;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                height = (data[i + 3] << 8) | data[i + 4];
                width = (data[i + 5] << 8) | data[i + 6];
                return;
            }

            i += length;
        }
    }

    private static string? ReadPdfVersion(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(header.Slice(0, Math.Min(header.Length, 16)));
        var line = text.Split('\r', '\n')[0];
        return line.StartsWith("%PDF-", StringComparison.Ordinal) ? line.Trim() : null;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
