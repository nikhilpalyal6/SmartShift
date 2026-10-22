namespace SmartShift.Core.Classification;

/// <summary>
/// Deterministic header-based classification. Reads a small prefix only; never executes files.
/// </summary>
public sealed class FileClassifier
{
    private const int HeaderSize = 32;
    private const int JpegScanLimit = 65_536;

    public ClassifiedFile Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Missing("(empty path)", "", "", ClassificationIssue.Missing, "No path was provided.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return Missing(path, Path.GetFileName(path), Path.GetExtension(path), ClassificationIssue.Inaccessible, $"The path could not be resolved. {ex.Message}");
        }

        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var hint = HintFromExtension(extension);

        if (Directory.Exists(fullPath))
        {
            return new ClassifiedFile(fullPath, fileName, extension, 0, null, DetectedFormat.Unknown, hint, ClassificationIssue.Directory, "Folders cannot be dropped. Choose files instead.", null, null, null);
        }

        if (!File.Exists(fullPath))
        {
            return Missing(fullPath, fileName, extension, ClassificationIssue.Missing, "The file could not be found.");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
        }
        catch (Exception ex)
        {
            return Missing(fullPath, fileName, extension, ClassificationIssue.Inaccessible, $"The file could not be read. {ex.Message}");
        }

        if (info.Length == 0)
        {
            return new ClassifiedFile(fullPath, fileName, extension, 0, info.LastWriteTimeUtc, DetectedFormat.Empty, hint, ClassificationIssue.Empty, "The file is empty.", null, null, null);
        }

        byte[] header;
        try
        {
            header = ReadPrefix(fullPath, HeaderSize);
        }
        catch (Exception ex)
        {
            return new ClassifiedFile(fullPath, fileName, extension, info.Length, info.LastWriteTimeUtc, DetectedFormat.Unknown, hint, ClassificationIssue.Inaccessible, $"The file could not be opened. {ex.Message}", null, null, null);
        }

        var format = Sniff(header);
        int? width = null;
        int? height = null;
        string? pdfVersion = null;

        if (format == DetectedFormat.Png)
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

        var issue = ClassificationIssue.None;
        var status = $"Detected {FormatName(format)}.";

        if (format == DetectedFormat.Unknown)
        {
            issue = ClassificationIssue.UnknownFormat;
            status = "The file format was not recognized.";
        }
        else if (format is DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff)
        {
            issue = ClassificationIssue.FormatNotSupported;
            status = $"{FormatName(format)} was detected, but SmartShift does not support this format yet.";
        }
        else if (hint != DetectedFormat.Unknown && hint != format)
        {
            issue = ClassificationIssue.ExtensionContentMismatch;
            status = $"The extension suggests {FormatName(hint)}, but the contents are {FormatName(format)}.";
        }

        return new ClassifiedFile(
            Path: fullPath,
            FileName: fileName,
            Extension: extension,
            LengthBytes: info.Length,
            LastWriteTimeUtc: info.LastWriteTimeUtc,
            Format: format,
            ExtensionHint: hint,
            Issue: issue,
            Status: status,
            PixelWidth: width,
            PixelHeight: height,
            PdfVersion: pdfVersion);
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
            _ => DetectedFormat.Unknown
        };
    }

    public static string FormatName(DetectedFormat format) => format switch
    {
        DetectedFormat.Png => "PNG",
        DetectedFormat.Jpeg => "JPEG",
        DetectedFormat.Webp => "WebP",
        DetectedFormat.Pdf => "PDF",
        DetectedFormat.Gif => "GIF",
        DetectedFormat.Bmp => "BMP",
        DetectedFormat.Tiff => "TIFF",
        DetectedFormat.Empty => "an empty file",
        _ => "an unknown format"
    };

    private static ClassifiedFile Missing(string path, string fileName, string extension, ClassificationIssue issue, string status)
        => new(path, fileName, extension, 0, null, DetectedFormat.Unknown, HintFromExtension(extension), issue, status, null, null, null);

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

    private static DetectedFormat Sniff(ReadOnlySpan<byte> header)
    {
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

        return DetectedFormat.Unknown;
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

        var fourcc = System.Text.Encoding.ASCII.GetString(header.Slice(12, 4));
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

        var text = System.Text.Encoding.ASCII.GetString(header.Slice(0, Math.Min(header.Length, 16)));
        var line = text.Split('\r', '\n')[0];
        return line.StartsWith("%PDF-", StringComparison.Ordinal) ? line.Trim() : null;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
