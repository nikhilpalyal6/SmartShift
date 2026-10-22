using SmartShift.Core.Classification;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class FileClassifierTests
{
    private readonly FileClassifier _classifier = new();

    [Fact]
    public void Classify_Png_ReadsHeaderAndLeavesFileUnchanged()
    {
        using var file = SampleFiles.Bytes("a.png", SampleFiles.Png1x1());
        var before = File.ReadAllBytes(file.Path);
        var result = _classifier.Classify(file.Path);
        Assert.Equal(DetectedFormat.Png, result.Format);
        Assert.True(result.IsSupportedCategory);
        Assert.Equal(1, result.PixelWidth);
        Assert.Equal(1, result.PixelHeight);
        Assert.Equal(before, File.ReadAllBytes(file.Path));
        Assert.Equal(new FileInfo(file.Path).LastWriteTimeUtc, result.LastWriteTimeUtc?.UtcDateTime);
    }

    [Fact]
    public void Classify_JpegAndWebpAndPdf()
    {
        using var jpeg = SampleFiles.Bytes("a.jpg", SampleFiles.Jpeg1x1());
        using var webp = SampleFiles.Bytes("a.webp", SampleFiles.Webp1x1());
        using var pdf = SampleFiles.Bytes("a.pdf", SampleFiles.Pdf());
        var jpegResult = _classifier.Classify(jpeg.Path);
        Assert.Equal(DetectedFormat.Jpeg, jpegResult.Format);
        Assert.Equal(1, jpegResult.PixelWidth);
        Assert.Equal(1, jpegResult.PixelHeight);
        var webpResult = _classifier.Classify(webp.Path);
        Assert.Equal(DetectedFormat.Webp, webpResult.Format);
        Assert.Equal(1, webpResult.PixelWidth);
        Assert.Equal(1, webpResult.PixelHeight);
        var pdfResult = _classifier.Classify(pdf.Path);
        Assert.Equal(DetectedFormat.Pdf, pdfResult.Format);
        Assert.Equal("%PDF-1.7", pdfResult.PdfVersion);
        Assert.True(pdfResult.IsSupportedCategory);
    }

    [Fact]
    public void Classify_Gif_IsDetectedButNotSupported()
    {
        using var gif = SampleFiles.Bytes("a.gif", SampleFiles.Gif());
        var result = _classifier.Classify(gif.Path);
        Assert.Equal(DetectedFormat.Gif, result.Format);
        Assert.Equal(ClassificationIssue.FormatNotSupported, result.Issue);
        Assert.False(result.IsSupportedCategory);
    }

    [Fact]
    public void Classify_UnknownExtensionAndInvalidContent()
    {
        using var txt = SampleFiles.Text("notes.txt", "hello");
        var result = _classifier.Classify(txt.Path);
        Assert.Equal(DetectedFormat.Unknown, result.Format);
        Assert.Equal(ClassificationIssue.UnknownFormat, result.Issue);
        Assert.False(result.IsSupportedCategory);
    }

    [Fact]
    public void Classify_ExtensionMismatch_UsesContent()
    {
        using var file = SampleFiles.Bytes("photo.png", SampleFiles.Jpeg1x1());
        var result = _classifier.Classify(file.Path);
        Assert.Equal(DetectedFormat.Jpeg, result.Format);
        Assert.Equal(ClassificationIssue.ExtensionContentMismatch, result.Issue);
        Assert.True(result.IsSupportedCategory);
    }

    [Fact]
    public void Classify_MissingEmptyDirectoryAndInaccessible()
    {
        var missing = _classifier.Classify(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartshift-missing-" + Guid.NewGuid() + ".png"));
        Assert.Equal(ClassificationIssue.Missing, missing.Issue);

        using var empty = SampleFiles.Bytes("empty.png", []);
        Assert.Equal(ClassificationIssue.Empty, _classifier.Classify(empty.Path).Issue);

        var dir = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartshift-dir-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.Equal(ClassificationIssue.Directory, _classifier.Classify(dir.FullName).Issue);
            Assert.True(Directory.Exists(dir.FullName));
        }
        finally
        {
            Directory.Delete(dir.FullName);
        }

        using var bmp = SampleFiles.Bytes("a.bmp", "BM"u8.ToArray().Concat(new byte[14]).ToArray());
        var bmpResult = _classifier.Classify(bmp.Path);
        Assert.Equal(DetectedFormat.Bmp, bmpResult.Format);
        Assert.False(bmpResult.IsSupportedCategory);

        using var tiff = SampleFiles.Bytes("a.tif", [0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0]);
        var tiffResult = _classifier.Classify(tiff.Path);
        Assert.Equal(DetectedFormat.Tiff, tiffResult.Format);
        Assert.False(tiffResult.IsSupportedCategory);

        using var locked = SampleFiles.Bytes("lock.bin", SampleFiles.Png1x1());
        using var stream = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var inaccessible = _classifier.Classify(locked.Path);
        Assert.Equal(ClassificationIssue.Inaccessible, inaccessible.Issue);
    }
}
