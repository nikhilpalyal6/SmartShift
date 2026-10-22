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

public sealed class PdfProcessingTests : IDisposable
{
    private readonly string _rootDir;
    private readonly List<string> _tempFiles = [];
    private readonly FileClassifier _classifier = new();

    public PdfProcessingTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-pdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    private TestAllocator Allocator() => new(_rootDir);

    private string CreatePdf(Action<PdfDocument> setup, string? name = null)
    {
        var path = Path.Combine(_rootDir, name ?? $"test-{Guid.NewGuid():N}.pdf");
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
    public async Task CompressPdf_SimpleOnePage_PreservesSourceAndReopens()
    {
        var path = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("Single page selectable text", new XFont("Arial", 14, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
        });

        var hashBefore = HashFile(path);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(result.OriginalUnchanged);
        Assert.Equal(hashBefore, HashFile(path));

        // Reopen and validate page count and dimensions
        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(1, verifyDoc.PageCount);
        Assert.Equal(612, verifyDoc.Pages[0].Width.Point, 1);
        Assert.Equal(792, verifyDoc.Pages[0].Height.Point, 1);
    }

    [Fact]
    public async Task CompressPdf_MultiPage_PreservesPageCount()
    {
        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 4; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawString($"Page {i + 1} content", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.DarkBlue, new XPoint(40, 40));
            }
        });

        var hashBefore = HashFile(path);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(path));

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(4, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_DifferentPageDimensions_PreservesAllDimensions()
    {
        var path = CreatePdf(doc =>
        {
            var p1 = doc.AddPage(); // Letter: 612 x 792
            p1.Width = XUnit.FromPoint(612);
            p1.Height = XUnit.FromPoint(792);

            var p2 = doc.AddPage(); // A4: 595 x 842
            p2.Width = XUnit.FromPoint(595);
            p2.Height = XUnit.FromPoint(842);

            var p3 = doc.AddPage(); // Landscape: 800 x 500
            p3.Width = XUnit.FromPoint(800);
            p3.Height = XUnit.FromPoint(500);
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
        Assert.Equal(612, verifyDoc.Pages[0].Width.Point, 1);
        Assert.Equal(792, verifyDoc.Pages[0].Height.Point, 1);
        Assert.Equal(595, verifyDoc.Pages[1].Width.Point, 1);
        Assert.Equal(842, verifyDoc.Pages[1].Height.Point, 1);
        Assert.Equal(800, verifyDoc.Pages[2].Width.Point, 1);
        Assert.Equal(500, verifyDoc.Pages[2].Height.Point, 1);
    }

    [Fact]
    public async Task CompressPdf_WithVectorGraphics_PreservesContent()
    {
        var path = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawLine(XPens.Red, 10, 10, 200, 200);
            gfx.DrawRectangle(XPens.Blue, XBrushes.LightGreen, 50, 50, 150, 80);
            gfx.DrawEllipse(XPens.Orange, 100, 100, 80, 80);
        });

        var hashBefore = HashFile(path);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(path));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task CompressPdf_UncompressedStream_ReducesFileSize()
    {
        var path = CreatePdf(doc =>
        {
            doc.Options.CompressContentStreams = false;
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 11, XFontStyleEx.Regular);
            for (int i = 0; i < 200; i++)
            {
                gfx.DrawString($"Line {i}: Repeating uncompressed text content that compresses efficiently via Deflate in SmartShift.", font, XBrushes.Black, new XPoint(20, 20 + (i * 3)));
            }
        });

        var originalSize = new FileInfo(path).Length;
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        var outputSize = new FileInfo(result.OutputPath!).Length;
        Assert.True(outputSize < originalSize, $"Expected output {outputSize} < original {originalSize}");
        Assert.Contains("Saved", result.Details["sizeNote"]);
    }

    [Fact]
    public async Task CompressPdf_AlreadyCompressedOrTiny_ReportsGrowthHonestly()
    {
        // Minimal PDF document with virtually no uncompressed data
        var path = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString(".", new XFont("Arial", 8, XFontStyleEx.Regular), XBrushes.Black, new XPoint(10, 10));
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        // When output is larger, details should report growth honestly without claiming reduction
        var originalSize = new FileInfo(path).Length;
        var outputSize = new FileInfo(result.OutputPath!).Length;
        if (outputSize > originalSize)
        {
            Assert.Contains("Output grew", result.Details["sizeNote"]);
        }
    }

    [Fact]
    public async Task CompressPdf_PasswordProtected_FailsGracefullyWithoutCrashing()
    {
        var path = CreatePdf(doc =>
        {
            doc.AddPage();
            doc.SecuritySettings.OwnerPassword = "SecretPassword123!";
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.True(result.OriginalUnchanged);
        Assert.Contains("encrypted", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressPdf_UserPasswordProtected_FailsGracefully()
    {
        var path = CreatePdf(doc =>
        {
            doc.AddPage();
            doc.SecuritySettings.UserPassword = "UserSecret456!";
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.True(result.OriginalUnchanged);
        Assert.Contains("encrypted", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressPdf_FailedOperation_DoesNotLeakFileHandle()
    {
        var path = CreatePdf(doc =>
        {
            doc.AddPage();
            doc.SecuritySettings.OwnerPassword = "LockMeOut123!";
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);
        Assert.False(result.Succeeded);

        // If file handle was leaked, opening exclusively will throw IOException
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task CompressPdf_ImageHeavy_PreservesImageStreamsAndReopens()
    {
        // Create a temporary PNG image to embed
        var imgPath = Path.Combine(_rootDir, "embedded.png");
        File.WriteAllBytes(imgPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        _tempFiles.Add(imgPath);

        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 3; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                using var ximage = XImage.FromFile(imgPath);
                gfx.DrawImage(ximage, 50, 50, 100, 100);
                gfx.DrawString($"Image page {i + 1}", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 180));
            }
        });

        var hashBefore = HashFile(path);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(hashBefore, HashFile(path));

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_LargeDocument_ProcessesSuccessfullyAndPreservesAllPages()
    {
        var path = CreatePdf(doc =>
        {
            var font = new XFont("Arial", 10, XFontStyleEx.Regular);
            for (int i = 0; i < 50; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                for (int j = 0; j < 25; j++)
                {
                    gfx.DrawString($"Page {i + 1}, Line {j + 1}: SmartShift verification dataset content stream line item.", font, XBrushes.DarkSlateGray, new XPoint(30, 30 + (j * 15)));
                }
            }
        });

        var hashBefore = HashFile(path);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.Equal(hashBefore, HashFile(path));

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(50, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_ReadOnlySourceFile_ProcessesSuccessfullyAndPreservesAttribute()
    {
        var path = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("Read Only Source", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
        });

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var hashBefore = HashFile(path);
            var processor = new PdfFileProcessor(Allocator(), _classifier);
            var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

            var result = await processor.ProcessAsync(request, null, CancellationToken.None);

            Assert.True(result.Succeeded, result.Status);
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(hashBefore, HashFile(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task CompressPdf_NonPdfOrUnsupportedFile_FailsGracefully()
    {
        var textFile = Path.Combine(_rootDir, "plain.txt");
        File.WriteAllText(textFile, "This is plain text, not a PDF document.");
        _tempFiles.Add(textFile);

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(textFile), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.True(result.OriginalUnchanged);
        Assert.Contains("PDF", result.Status);
    }

    [Fact]
    public async Task CompressPdf_EmptyFile_FailsGracefully()
    {
        var emptyFile = Path.Combine(_rootDir, "empty.pdf");
        File.WriteAllBytes(emptyFile, []);
        _tempFiles.Add(emptyFile);

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(emptyFile), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task CompressPdf_CorruptFile_FailsGracefully()
    {
        var corruptFile = Path.Combine(_rootDir, "corrupt.pdf");
        File.WriteAllText(corruptFile, "%PDF-1.7 corrupt stream garbage endobj trailer <<>> %%EOF");
        _tempFiles.Add(corruptFile);

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(corruptFile), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.Contains("corrupt", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressPdf_MissingFile_FailsGracefully()
    {
        var missingPath = Path.Combine(_rootDir, "does-not-exist.pdf");
        var fakeClassified = _classifier.Classify(missingPath);

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(fakeClassified, ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task CompressPdf_CollisionHandling_AppendsIncrementalSuffix()
    {
        var path = CreatePdf(doc =>
        {
            doc.AddPage();
        }, "report.pdf");

        // Occupy the first destination name
        var occupied = Path.Combine(_rootDir, "report-compressed.pdf");
        File.WriteAllText(occupied, "existing content");
        _tempFiles.Add(occupied);

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_rootDir, "report-compressed (1).pdf"), result.OutputPath);
        Assert.Equal("existing content", File.ReadAllText(occupied));
    }

    [Fact]
    public async Task CompressPdf_Cancellation_CleansUpTemporaryFiles()
    {
        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 50; i++)
            {
                var p = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(p);
                gfx.DrawString($"Page {i}", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(20, 20));
            }
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(), _rootDir);

        var result = await processor.ProcessAsync(request, null, cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.WasCancelled);
        Assert.Null(result.OutputPath);
        Assert.Empty(Directory.GetFiles(_rootDir, "*-compressed*.partial"));
    }

    [Fact]
    public async Task InspectPdf_PopulatesPageCountDimensionsAndVersion()
    {
        var path = CreatePdf(doc =>
        {
            var p1 = doc.AddPage();
            p1.Width = XUnit.FromPoint(612);
            p1.Height = XUnit.FromPoint(792);
            doc.AddPage();
            doc.Info.Title = "Sample Title";
            doc.Info.Author = "SmartShift Author";
        });

        var inspector = new InspectFileProcessor(ActionIds.ShowPdfInfo, _classifier);
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.ShowPdfInfo, new ProcessingOptions(), _rootDir);

        var result = await inspector.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("2", result.Details["pages"]);
        Assert.Contains("612 × 792", result.Details["dimensions"]);
        Assert.Equal("Sample Title", result.Details["title"]);
        Assert.Equal("SmartShift Author", result.Details["author"]);
        Assert.Equal("None (Unencrypted)", result.Details["security"]);
    }

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

    [Theory]
    [InlineData("500", "KB")]
    [InlineData("1", "MB")]
    [InlineData("2", "MB")]
    [InlineData("5", "MB")]
    public async Task CompressPdf_PredefinedTargetSizes_EachExecutesSuccessfully(string size, string unit)
    {
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString("Predefined target test", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(40, 40));
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = size,
            ["targetUnit"] = unit
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(result.Details.ContainsKey("targetSize"));
        Assert.True(result.Details.ContainsKey("targetReached"));

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(1, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_CustomTarget_KB_And_MB_ExecuteCorrectly()
    {
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString("Custom targets test", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(40, 40));
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);

        // Custom 750 KB
        var reqKb = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "750",
            ["targetUnit"] = "KB"
        }), _rootDir);
        var resKb = await processor.ProcessAsync(reqKb, null, CancellationToken.None);
        Assert.True(resKb.Succeeded);
        Assert.Equal("750 KB", resKb.Details["targetSize"]);

        // Custom 3.5 MB
        var reqMb = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "3.5",
            ["targetUnit"] = "MB"
        }), _rootDir);
        var resMb = await processor.ProcessAsync(reqMb, null, CancellationToken.None);
        Assert.True(resMb.Succeeded);
        Assert.Contains("MB", resMb.Details["targetSize"]);
    }

    [Theory]
    [InlineData("0", "MB")]
    [InlineData("-5", "MB")]
    [InlineData("invalid", "MB")]
    [InlineData("2000", "MB")]
    public async Task CompressPdf_InvalidOrZeroOrNegativeTarget_ThrowsValidationException(string size, string unit)
    {
        var path = CreatePdf(doc => doc.AddPage());
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = size,
            ["targetUnit"] = unit
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.Contains("targetSize", result.Status);
    }

    [Fact]
    public async Task CompressPdf_AlreadyBelowTarget_ReportsTargetMetAccurately()
    {
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString("Small document", new XFont("Arial", 10, XFontStyleEx.Regular), XBrushes.Black, new XPoint(20, 20));
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "2",
            ["targetUnit"] = "MB"
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("true", result.Details["targetReached"]);
        Assert.Contains("met", result.Details["targetStatus"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressPdf_LosslessStageA_ReachesTargetWithoutImageOptimization()
    {
        var path = CreatePdf(doc =>
        {
            doc.Options.CompressContentStreams = false;
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            var font = new XFont("Arial", 10, XFontStyleEx.Regular);
            for (int i = 0; i < 100; i++)
            {
                gfx.DrawString($"Uncompressed stream text line {i} to deflate in Stage A.", font, XBrushes.Black, new XPoint(20, 20 + (i * 5)));
            }
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "1",
            ["targetUnit"] = "MB"
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("true", result.Details["targetReached"]);
        Assert.Contains("Stage A", result.Details["stage"]);
    }

    [Fact]
    public async Task CompressPdf_ImageHeavy_OptimizesImagesToReachTarget()
    {
        var imgPath = CreateJpeg(800, 800, quality: 95);
        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 3; i++)
            {
                var p = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(p);
                using var ximg = XImage.FromFile(imgPath);
                gfx.DrawImage(ximg, 20, 20, 400, 400);
            }
        });

        var originalLength = new FileInfo(path).Length;
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        // Set target well below original length so Stage B is triggered
        var targetKb = (int)Math.Max(10, (originalLength / 1024) / 2);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = targetKb.ToString(),
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        var outputLength = new FileInfo(result.OutputPath!).Length;
        Assert.True(outputLength < originalLength, $"Expected {outputLength} < {originalLength}");
        Assert.True(result.Details.ContainsKey("imagesOptimized"));
        Assert.True(int.Parse(result.Details["imagesOptimized"]) > 0);

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_UnachievableTarget_ReportsHonestlyWithoutDegradingContent()
    {
        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 5; i++)
            {
                var p = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(p);
                gfx.DrawString($"Unachievable target page {i}", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
            }
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetBytes"] = "100"
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("false", result.Details["targetReached"]);
        Assert.Contains("not reached", result.Details["targetStatus"], StringComparison.OrdinalIgnoreCase);

        using var verifyDoc = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(5, verifyDoc.PageCount);
    }

    [Fact]
    public async Task CompressPdf_QualityPolicy_Off_EnforcesLosslessOnly()
    {
        var imgPath = CreateJpeg(300, 300, quality: 90);
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 20, 20, 300, 300);
        });

        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "50",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "off"
        });
        var request = new ProcessingRequest(_classifier.Classify(path), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("Stage A", result.Details["stage"]);
        Assert.False(result.Details.ContainsKey("imagesOptimized"));
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
