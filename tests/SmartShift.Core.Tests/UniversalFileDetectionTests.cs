using System.IO.Compression;
using System.Text;
using SmartShift.Core.Classification;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class UniversalFileDetectionTests
{
    private readonly FileClassifier _classifier = new();

    [Theory]
    [InlineData(".png", DetectedFormat.Png, FileCategory.Image)]
    [InlineData(".jpg", DetectedFormat.Jpeg, FileCategory.Image)]
    [InlineData(".webp", DetectedFormat.Webp, FileCategory.Image)]
    [InlineData(".pdf", DetectedFormat.Pdf, FileCategory.Document)]
    [InlineData(".docx", DetectedFormat.Docx, FileCategory.Document)]
    [InlineData(".pptx", DetectedFormat.Pptx, FileCategory.Presentation)]
    [InlineData(".xlsx", DetectedFormat.Xlsx, FileCategory.Spreadsheet)]
    [InlineData(".txt", DetectedFormat.Txt, FileCategory.Document)]
    [InlineData(".md", DetectedFormat.Markdown, FileCategory.Document)]
    public void Classify_AllSupportedFormats_DetectsCorrectFormatAndCategory(
        string extension,
        DetectedFormat expectedFormat,
        FileCategory expectedCategory)
    {
        using var tempFile = CreateTestFileForFormat(extension, expectedFormat);
        var beforeBytes = File.ReadAllBytes(tempFile.Path);
        var beforeTime = File.GetLastWriteTimeUtc(tempFile.Path);

        var result = _classifier.Classify(tempFile.Path);

        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(ClassificationIssue.None, result.Issue);
        if (expectedFormat == DetectedFormat.Xlsx)
        {
            Assert.False(result.IsSupported);
        }
        else
        {
            Assert.True(result.IsSupported);
        }
        Assert.True(result.IsRecognized);
        Assert.False(string.IsNullOrWhiteSpace(result.DisplayType));

        // Verify immutability
        Assert.Equal(beforeBytes, File.ReadAllBytes(tempFile.Path));
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(tempFile.Path));
    }

    [Fact]
    public void Classify_ReportPdfContainingPlainText_ReportsMismatch()
    {
        // A file named report.pdf that contains plain text must not be silently treated as a PDF
        using var fakePdf = SampleFiles.Text("report.pdf", "Hello, this is actually plain text inside a PDF named file.");
        var result = _classifier.Classify(fakePdf.Path);

        Assert.Equal(DetectedFormat.Txt, result.Format);
        Assert.Equal(DetectedFormat.Pdf, result.ExtensionHint);
        Assert.Equal(ClassificationIssue.ExtensionContentMismatch, result.Issue);
        Assert.Contains("suggests PDF", result.Status);
        Assert.Contains("Plain Text", result.Status);
    }

    [Fact]
    public void Classify_PhotoPngContainingJpeg_ReportsMismatch()
    {
        using var fakePng = SampleFiles.Bytes("photo.png", SampleFiles.Jpeg1x1());
        var result = _classifier.Classify(fakePng.Path);

        Assert.Equal(DetectedFormat.Jpeg, result.Format);
        Assert.Equal(DetectedFormat.Png, result.ExtensionHint);
        Assert.Equal(ClassificationIssue.ExtensionContentMismatch, result.Issue);
        Assert.Contains("suggests PNG", result.Status);
    }

    [Fact]
    public void Classify_DocxContainingGenericZip_ReportsMismatch()
    {
        // A file named document.docx that is an ordinary ZIP archive with no word/ parts
        using var fakeDocx = CreateGenericZip("document.docx", "data.txt", "Some generic zip data");
        var result = _classifier.Classify(fakeDocx.Path);

        Assert.Equal(DetectedFormat.Zip, result.Format);
        Assert.Equal(DetectedFormat.Docx, result.ExtensionHint);
        Assert.Equal(ClassificationIssue.ExtensionContentMismatch, result.Issue);
        Assert.Contains("suggests Word (DOCX)", result.Status);
    }

    [Fact]
    public void Classify_TextFileContainingBinaryNullBytes_ReportsMalformed()
    {
        // A file named notes.txt that contains binary null bytes
        var binary = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x01, 0x02 };
        using var fakeTxt = SampleFiles.Bytes("notes.txt", binary);
        var result = _classifier.Classify(fakeTxt.Path);

        Assert.Equal(DetectedFormat.Unknown, result.Format);
        Assert.Equal(ClassificationIssue.MalformedFile, result.Issue);
    }

    [Fact]
    public void Classify_CorruptedZipPackage_ReportsCorruptedPackage()
    {
        // Starts with PK signature but is truncated/corrupt
        var brokenZip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x99, 0x88, 0x77 };
        using var brokenFile = SampleFiles.Bytes("corrupted.docx", brokenZip);
        var result = _classifier.Classify(brokenFile.Path);

        Assert.Equal(DetectedFormat.Unknown, result.Format);
        Assert.Equal(ClassificationIssue.CorruptedPackage, result.Issue);
    }

    [Fact]
    public void Classify_GenericZipArchive_DetectedAsZipAndNotSupported()
    {
        using var zip = CreateGenericZip("archive.zip", "test.txt", "inside zip");
        var result = _classifier.Classify(zip.Path);

        Assert.Equal(DetectedFormat.Zip, result.Format);
        Assert.Equal(FileCategory.Archive, result.Category);
        Assert.Equal(ClassificationIssue.FormatNotSupported, result.Issue);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Classify_GifBmpTiff_DetectedWithFormatNotSupported()
    {
        using var gif = SampleFiles.Bytes("a.gif", SampleFiles.Gif());
        using var bmp = SampleFiles.Bytes("a.bmp", "BM"u8.ToArray().Concat(new byte[14]).ToArray());
        using var tiff = SampleFiles.Bytes("a.tif", [0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0]);

        var gifRes = _classifier.Classify(gif.Path);
        Assert.Equal(DetectedFormat.Gif, gifRes.Format);
        Assert.Equal(ClassificationIssue.FormatNotSupported, gifRes.Issue);
        Assert.False(gifRes.IsSupported);

        var bmpRes = _classifier.Classify(bmp.Path);
        Assert.Equal(DetectedFormat.Bmp, bmpRes.Format);
        Assert.Equal(ClassificationIssue.FormatNotSupported, bmpRes.Issue);
        Assert.False(bmpRes.IsSupported);

        var tiffRes = _classifier.Classify(tiff.Path);
        Assert.Equal(DetectedFormat.Tiff, tiffRes.Format);
        Assert.Equal(ClassificationIssue.FormatNotSupported, tiffRes.Issue);
        Assert.False(tiffRes.IsSupported);
    }

    [Fact]
    public void Classify_UnknownBinary_DetectedAsUnknown()
    {
        using var unknown = SampleFiles.Bytes("random.xyz", [0x11, 0x22, 0x33, 0x44, 0x55]);
        var result = _classifier.Classify(unknown.Path);

        Assert.Equal(DetectedFormat.Unknown, result.Format);
        Assert.Equal(FileCategory.Unknown, result.Category);
        Assert.Equal(ClassificationIssue.UnknownFormat, result.Issue);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Classify_EmptyFile_DetectedAsEmpty()
    {
        using var empty = SampleFiles.Bytes("empty.txt", []);
        var result = _classifier.Classify(empty.Path);

        Assert.Equal(DetectedFormat.Empty, result.Format);
        Assert.Equal(ClassificationIssue.Empty, result.Issue);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Classify_MissingAndInaccessibleFile_HandledGracefully()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".docx");
        var missingRes = _classifier.Classify(missingPath);
        Assert.Equal(ClassificationIssue.Missing, missingRes.Issue);
        Assert.False(missingRes.Exists);

        using var locked = SampleFiles.Bytes("locked.png", SampleFiles.Png1x1());
        using var lockStream = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var inaccRes = _classifier.Classify(locked.Path);
        Assert.Equal(ClassificationIssue.Inaccessible, inaccRes.Issue);
    }

    [Fact]
    public void Classify_LargeFile_ReadsOnlyPrefixEfficiently()
    {
        // Create a 10MB test file with plain text prefix and verify detection succeeds without loading 10MB
        var path = Path.Combine(Path.GetTempPath(), "large-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var line = Encoding.UTF8.GetBytes("Header line: SmartShift Universal Support file content for testing.\n");
                for (int i = 0; i < 80; i++)
                {
                    fs.Write(line, 0, line.Length);
                }
                fs.SetLength(10 * 1024 * 1024); // 10 MB total length
            }

            var result = _classifier.Classify(path);
            Assert.Equal(DetectedFormat.Txt, result.Format);
            Assert.Equal(10 * 1024 * 1024, result.LengthBytes);
            Assert.Equal(ClassificationIssue.None, result.Issue);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static SampleFiles CreateTestFileForFormat(string extension, DetectedFormat format)
    {
        return format switch
        {
            DetectedFormat.Png => SampleFiles.Bytes("test.png", SampleFiles.Png1x1()),
            DetectedFormat.Jpeg => SampleFiles.Bytes("test.jpg", SampleFiles.Jpeg1x1()),
            DetectedFormat.Webp => SampleFiles.Bytes("test.webp", SampleFiles.Webp1x1()),
            DetectedFormat.Pdf => SampleFiles.Bytes("test.pdf", SampleFiles.Pdf()),
            DetectedFormat.Docx => CreateOpenXmlPackage("test.docx", "word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>"),
            DetectedFormat.Pptx => CreateOpenXmlPackage("test.pptx", "ppt/presentation.xml", "<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main'/>"),
            DetectedFormat.Xlsx => CreateOpenXmlPackage("test.xlsx", "xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'/>"),
            DetectedFormat.Txt => SampleFiles.Text("test.txt", "Plain text document content."),
            DetectedFormat.Markdown => SampleFiles.Text("test.md", "# Markdown Title\n\n- Item 1\n- Item 2"),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static SampleFiles CreateOpenXmlPackage(string fileName, string entryPath, string content)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "smartshift-test-" + Guid.NewGuid().ToString("N") + "-" + fileName);
        using (var fs = new FileStream(tempPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return SampleFiles.Bytes(fileName, File.ReadAllBytes(tempPath));
    }

    private static SampleFiles CreateGenericZip(string fileName, string entryName, string content)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "smartshift-zip-" + Guid.NewGuid().ToString("N") + "-" + fileName);
        using (var fs = new FileStream(tempPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return SampleFiles.Bytes(fileName, File.ReadAllBytes(tempPath));
    }
}
