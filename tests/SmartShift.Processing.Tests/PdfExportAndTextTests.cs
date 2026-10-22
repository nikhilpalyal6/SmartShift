using System.Security.Cryptography;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing.Pdf;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfExportAndTextTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileClassifier _classifier;
    private readonly OutputPathAllocator _allocator;

    public PdfExportAndTextTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smartshift-pdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _classifier = new FileClassifier();
        _allocator = new OutputPathAllocator(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    private string CreateTestPdf(int pageCount, string textPrefix = "Page Content")
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 1; i <= pageCount; i++)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(200); // 200 pt
                page.Height = XUnit.FromPoint(300); // 300 pt

                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14);
                gfx.DrawString($"{textPrefix} - Number {i}", font, XBrushes.DarkBlue, new XPoint(20, 50));
                gfx.DrawRectangle(XPens.Red, 10, 10, 180, 280);
            }
            doc.Save(path);
        }
        return path;
    }

    private string CreateEmptyPagePdf()
    {
        var path = Path.Combine(_tempDir, $"empty_page_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            doc.AddPage();
            doc.Save(path);
        }
        return path;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("1", true)]
    [InlineData("1, 2, 3", true)]
    [InlineData("1-3, 5, 7-9", true)]
    [InlineData("  4 - 6  ", true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("5-2", false)]
    [InlineData("1, , 2", false)]
    [InlineData("1-2-3", false)]
    public void PageRangeParser_SyntaxValidation_BehavesCorrectly(string input, bool expectedValid)
    {
        bool isValid = PageRangeParser.TryValidateSyntax(input, out var error);
        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void PageRangeParser_Parse_ResolvesExpectedPages()
    {
        var pages = PageRangeParser.Parse("1, 3-4", totalPages: 5);
        Assert.Equal(new[] { 1, 3, 4 }, pages);

        var allPages = PageRangeParser.Parse("all", totalPages: 3);
        Assert.Equal(new[] { 1, 2, 3 }, allPages);

        var outOfBounds = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("1, 6", totalPages: 5));
        Assert.Contains("exceeds document page count", outOfBounds.Message);
    }

    [Fact]
    public void PdfPageExporter_ExportPng_RendersCrispPagesAndValidDpiScaling()
    {
        var pdfPath = CreateTestPdf(2, "Export Test");
        var shaBefore = ComputeSha256(pdfPath);

        // 150 DPI: 200 pt * (150/72) ~ 416 px width, 300 pt * (150/72) ~ 625 px height
        var result = PdfPageExporter.ExportPages(
            pdfPath,
            _allocator,
            ExportImageFormat.Png,
            dpi: 150,
            jpegQuality: 85,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.ExportedCount);
        Assert.Equal(2, result.ExportedFiles.Count);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore); // Immutability

        foreach (var file in result.ExportedFiles)
        {
            Assert.True(File.Exists(file));
            Assert.EndsWith(".png", file, StringComparison.OrdinalIgnoreCase);

            using var codec = SKCodec.Create(file);
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
            Assert.True(codec.Info.Width > 400 && codec.Info.Width < 430);
            Assert.True(codec.Info.Height > 610 && codec.Info.Height < 640);
        }
    }

    [Fact]
    public void PdfPageExporter_ExportJpeg_RendersWithQualityAndWhiteBackground()
    {
        var pdfPath = CreateTestPdf(1, "JPEG Test");
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfPageExporter.ExportPages(
            pdfPath,
            _allocator,
            ExportImageFormat.Jpeg,
            dpi: 100,
            jpegQuality: 90,
            pageRange: "1",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.Single(result.ExportedFiles);
        var file = result.ExportedFiles[0];
        Assert.EndsWith(".jpg", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore);

        using var codec = SKCodec.Create(file);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
    }

    [Fact]
    public void PdfPageExporter_PageRangeFiltering_ExportsOnlySelectedPages()
    {
        var pdfPath = CreateTestPdf(4, "Multi Page");

        var result = PdfPageExporter.ExportPages(
            pdfPath,
            _allocator,
            ExportImageFormat.Png,
            dpi: 100,
            jpegQuality: 85,
            pageRange: "2, 4",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.ExportedCount);
        Assert.Equal(4, result.TotalPagesInDoc);
        Assert.Contains("-page-002.png", result.ExportedFiles[0]);
        Assert.Contains("-page-004.png", result.ExportedFiles[1]);
    }

    [Fact]
    public void PdfPageExporter_Cancellation_CleansUpPartials()
    {
        var pdfPath = CreateTestPdf(3, "Cancel Test");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        Assert.Throws<OperationCanceledException>(() =>
            PdfPageExporter.ExportPages(
                pdfPath,
                _allocator,
                ExportImageFormat.Png,
                dpi: 150,
                jpegQuality: 85,
                pageRange: "all",
                progress: null,
                cancellationToken: cts.Token));

        var partials = Directory.GetFiles(_tempDir, "*.partial", SearchOption.AllDirectories);
        Assert.Empty(partials);
    }

    [Fact]
    public void PdfTextExtractor_ExtractsTextInReadingOrderWithSeparators()
    {
        var pdfPath = CreateTestPdf(2, "Reading Order Test");
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfTextExtractor.ExtractText(
            pdfPath,
            _allocator,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.False(result.IsScannedOrImageOnly);
        Assert.True(result.TotalCharactersExtracted > 0);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore);

        var content = File.ReadAllText(result.OutputPath, Encoding.UTF8);
        Assert.Contains("--- Page 1 ---", content);
        Assert.Contains("--- Page 2 ---", content);
        Assert.Contains("Reading Order Test - Number 1", content);
        Assert.Contains("Reading Order Test - Number 2", content);

        // Verify reading order: Page 1 comes before Page 2
        var p1Index = content.IndexOf("--- Page 1 ---", StringComparison.Ordinal);
        var p2Index = content.IndexOf("--- Page 2 ---", StringComparison.Ordinal);
        Assert.True(p1Index < p2Index);
    }

    [Fact]
    public void PdfTextExtractor_EmptyPagePdf_FlagsScannedOrEmptyHonesty()
    {
        var pdfPath = CreateEmptyPagePdf();

        var result = PdfTextExtractor.ExtractText(
            pdfPath,
            _allocator,
            pageRange: "1",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.True(result.IsScannedOrImageOnly);
        Assert.Equal(0, result.TotalCharactersExtracted);
        Assert.Contains("scanned or image-only", result.SummaryNote);
    }

    [Fact]
    public async Task PdfExportProcessor_EndToEnd_ProcessesRequestSuccessfully()
    {
        var pdfPath = CreateTestPdf(2, "Processor Test");
        var classified = _classifier.Classify(pdfPath);

        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["format"] = "png",
            ["dpi"] = "100",
            ["pageRange"] = "1"
        });

        var request = new ProcessingRequest(classified, ActionIds.ExportPdfPages, options, _tempDir);
        var processor = new PdfExportProcessor(_allocator, _classifier);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(result.OriginalUnchanged);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal("1", result.Details["exportedPages"]);
    }

    [Fact]
    public async Task PdfTextProcessor_EndToEnd_ProcessesRequestSuccessfully()
    {
        var pdfPath = CreateTestPdf(1, "Text Processor Test");
        var classified = _classifier.Classify(pdfPath);

        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["pageRange"] = "all"
        });

        var request = new ProcessingRequest(classified, ActionIds.ExtractPdfText, options, _tempDir);
        var processor = new PdfTextProcessor(_allocator, _classifier);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(result.OriginalUnchanged);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".txt", result.OutputPath, StringComparison.OrdinalIgnoreCase);
    }
}
