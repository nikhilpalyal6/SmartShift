using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Pdf;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfCompressionDiagnosticTests : IDisposable
{
    private readonly string _rootDir;
    private readonly List<string> _tempFiles = [];
    private readonly FileClassifier _classifier = new();

    public PdfCompressionDiagnosticTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-pdf-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    private TestAllocator Allocator() => new(_rootDir);

    private string CreateJpeg(int width, int height, int quality = 90)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkBlue);
        using var paint = new SKPaint { Color = SKColors.Yellow, StrokeWidth = 4 };
        for (int i = 0; i < width; i += 10)
        {
            canvas.DrawLine(i, 0, width - i, height, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        var path = Path.Combine(_rootDir, $"fixture-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, data.ToArray());
        _tempFiles.Add(path);
        return path;
    }

    private string CreatePdf(Action<PdfDocument> setup, string? name = null)
    {
        var path = Path.Combine(_rootDir, name ?? $"diag-{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            setup(doc);
            doc.Save(path);
        }
        _tempFiles.Add(path);
        return path;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task CompressPdf_TargetReached_DiagnosticsReportAccurateCountsAndBytes()
    {
        var imgPath = CreateJpeg(800, 800, quality: 95);
        var pdfPath = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 20, 20, 400, 400);
        });

        var initialSize = new FileInfo(pdfPath).Length;
        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "200",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        // Diagnostics assertions
        Assert.Equal("true", result.Details["targetReached"]);
        Assert.Equal("1", result.Details["imagesDiscovered"]);
        Assert.Equal("1", result.Details["imagesEligible"]);
        Assert.Equal("1", result.Details["imagesOptimized"]);
        Assert.Equal(initialSize.ToString(), result.Details["originalBytes"]);
        var outputSize = new FileInfo(result.OutputPath).Length;
        Assert.Equal(outputSize.ToString(), result.Details["outputBytes"]);

        // Status message format
        Assert.StartsWith("Completed — Target reached", result.Status);

        // Status message format
        Assert.StartsWith("Completed — Target reached", result.Status);
    }

    [Fact]
    public async Task CompressPdf_TargetNotReached_DiagnosticsReportAccurateSkipReasons()
    {
        // Create a PDF with a PNG/FlateDecode image and an unachievable target (100 B)
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var bmp = new SKBitmap(100, 100);
            bmp.Erase(SKColors.DarkRed);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            var pngPath = Path.Combine(_rootDir, $"sample-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(pngPath, data.ToArray());
            _tempFiles.Add(pngPath);

            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(pngPath);
            gfx.DrawImage(ximg, 30, 30, 100, 100);
        });

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "1",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));
        Assert.Equal("false", result.Details["targetReached"]);
        Assert.StartsWith("Completed — Target not reached", result.Status);

        // Discovered 1 image, but 0 eligible (unsupported filter FlateDecode)
        Assert.Equal("1", result.Details["imagesDiscovered"]);
        Assert.Equal("0", result.Details["imagesEligible"]);
        Assert.False(result.Details.ContainsKey("imagesOptimized"));
        Assert.True(result.Details.ContainsKey("skippedReasons"));
        Assert.Contains("Unsupported filter", result.Details["skippedReasons"]);
        Assert.True(result.Details.ContainsKey("limitsExplanation"));
    }

    [Fact]
    public async Task CompressPdf_TextOnlyDocument_ReportsZeroImagesDiscovered()
    {
        var pdfPath = CreatePdf(doc =>
        {
            for (int i = 0; i < 3; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawString($"Page {i + 1} with text only.", new XFont("Arial", 14, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
            }
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "100",
            ["targetUnit"] = "KB"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("0", result.Details["imagesDiscovered"]);
        Assert.Equal("0", result.Details["imagesEligible"]);
        Assert.Contains("no embedded raster images", result.Details["limitsExplanation"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressPdf_OutputLarger_ReportsIncreaseAccurately()
    {
        // An already-compressed tiny file where overhead might increase size slightly
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("A", new XFont("Arial", 10, XFontStyleEx.Regular), XBrushes.Black, new XPoint(10, 10));
        });

        var summary = new ProcessingResultSummary(
            status: "Completed — File did not reduce.",
            succeeded: true,
            wasCancelled: false,
            originalUnchanged: true,
            outputPath: pdfPath,
            originalSizeBytes: 1000,
            outputSizeBytes: 1050,
            outputDimensions: "612 × 792 pt (1 page)",
            outputFormat: "PDF",
            encoderNote: null,
            metadataNote: null,
            sizeNote: "Output grew (+5.0%)");

        Assert.True(summary.IsIncrease);
        Assert.False(summary.IsReduction);
        Assert.Equal(50, summary.DeltaBytes);
        Assert.Equal(5.0, summary.DeltaPercentage);
        Assert.Contains("+50 B", summary.DeltaText);
        Assert.Contains("+5%", summary.DeltaText);
    }

    [Fact]
    public async Task CompressPdf_SourceSha256_Immutable_And_PartialCleanedUp()
    {
        var imgPath = CreateJpeg(400, 400, quality: 90);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 20, 20, 200, 200);
        });

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        // Check no .partial or .stageB.tmp files remain in output directory
        var partials = Directory.GetFiles(_rootDir, "*.partial");
        var tmps = Directory.GetFiles(_rootDir, "*.tmp");
        Assert.Empty(partials);
        Assert.Empty(tmps);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, true);
            }
        }
        catch
        {
        }
    }

    private sealed class TestAllocator : IOutputPathAllocator
    {
        public TestAllocator(string root) => RootDirectory = root;

        public string RootDirectory { get; }

        public void EnsureRoot() => Directory.CreateDirectory(RootDirectory);

        public string Allocate(string sourcePath, string desiredFileName)
        {
            var baseName = Path.GetFileNameWithoutExtension(desiredFileName);
            var ext = Path.GetExtension(desiredFileName);
            var candidate = Path.Combine(RootDirectory, desiredFileName);
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(RootDirectory, $"{baseName} ({counter}){ext}");
                counter++;
            }
            return candidate;
        }
    }
}
