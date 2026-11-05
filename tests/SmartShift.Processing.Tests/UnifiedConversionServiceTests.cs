using System.IO.Compression;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Capabilities;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing.Conversion;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class UnifiedConversionServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileClassifier _classifier;
    private readonly FileCapabilityRegistry _capabilities;
    private readonly OutputPathAllocator _allocator;
    private readonly ProcessorCatalog _processors;
    private readonly UnifiedConversionService _service;

    public UnifiedConversionServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "smartshift-unified-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _classifier = new FileClassifier();
        _capabilities = new FileCapabilityRegistry();
        _allocator = new OutputPathAllocator(_testDir);
        _processors = new ProcessorCatalog(_classifier, _allocator);
        _service = new UnifiedConversionService(_capabilities, _processors, _allocator, _classifier);
        _processors.Register(ActionIds.UnifiedConvert, new UnifiedConvertProcessor(_service));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void GetSupportedTargets_ReturnsCorrectTargets_ForEachSourceFormat()
    {
        // Images
        var pngTargets = _service.GetSupportedTargets(DetectedFormat.Png);
        Assert.Equal(2, pngTargets.Count);
        Assert.Contains(DetectedFormat.Webp, pngTargets);
        Assert.Contains(DetectedFormat.Jpeg, pngTargets);
        Assert.DoesNotContain(DetectedFormat.Png, pngTargets);

        var jpegTargets = _service.GetSupportedTargets(DetectedFormat.Jpeg);
        Assert.Equal(2, jpegTargets.Count);
        Assert.Contains(DetectedFormat.Webp, jpegTargets);
        Assert.Contains(DetectedFormat.Png, jpegTargets);
        Assert.DoesNotContain(DetectedFormat.Jpeg, jpegTargets);

        var webpTargets = _service.GetSupportedTargets(DetectedFormat.Webp);
        Assert.Equal(2, webpTargets.Count);
        Assert.Contains(DetectedFormat.Png, webpTargets);
        Assert.Contains(DetectedFormat.Jpeg, webpTargets);
        Assert.DoesNotContain(DetectedFormat.Webp, webpTargets);

        // PDF
        var pdfTargets = _service.GetSupportedTargets(DetectedFormat.Pdf);
        Assert.Equal(5, pdfTargets.Count);
        Assert.Contains(DetectedFormat.Docx, pdfTargets);
        Assert.Contains(DetectedFormat.Pptx, pdfTargets);
        Assert.Contains(DetectedFormat.Txt, pdfTargets);
        Assert.Contains(DetectedFormat.Png, pdfTargets);
        Assert.Contains(DetectedFormat.Jpeg, pdfTargets);
        Assert.DoesNotContain(DetectedFormat.Pdf, pdfTargets);

        // DOCX supports PDF and PPTX targets
        var docxTargets = _service.GetSupportedTargets(DetectedFormat.Docx);
        Assert.Contains(DetectedFormat.Pdf, docxTargets);
        Assert.Contains(DetectedFormat.Pptx, docxTargets);

        // Unsupported or import-only formats must return empty target lists
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Pptx));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Xlsx));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Txt));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Markdown));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Gif));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Bmp));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Tiff));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Zip));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Unknown));
        Assert.Empty(_service.GetSupportedTargets(DetectedFormat.Empty));
    }

    [Fact]
    public void CanConvert_RejectsSameFormat_AndUnsupportedCombinations()
    {
        // Same format
        Assert.False(_service.CanConvert(DetectedFormat.Png, DetectedFormat.Png));
        Assert.False(_service.CanConvert(DetectedFormat.Pdf, DetectedFormat.Pdf));
        Assert.False(_service.CanConvert(DetectedFormat.Jpeg, DetectedFormat.Jpeg));

        // Unsupported combinations
        Assert.False(_service.CanConvert(DetectedFormat.Xlsx, DetectedFormat.Pdf));
        Assert.False(_service.CanConvert(DetectedFormat.Txt, DetectedFormat.Docx));
        Assert.False(_service.CanConvert(DetectedFormat.Markdown, DetectedFormat.Pdf));
        Assert.False(_service.CanConvert(DetectedFormat.Png, DetectedFormat.Docx));
        Assert.False(_service.CanConvert(DetectedFormat.Pdf, DetectedFormat.Webp));

        // Valid combinations
        Assert.True(_service.CanConvert(DetectedFormat.Png, DetectedFormat.Webp));
        Assert.True(_service.CanConvert(DetectedFormat.Jpeg, DetectedFormat.Png));
        Assert.True(_service.CanConvert(DetectedFormat.Pdf, DetectedFormat.Docx));
        Assert.True(_service.CanConvert(DetectedFormat.Pdf, DetectedFormat.Pptx));
        Assert.True(_service.CanConvert(DetectedFormat.Docx, DetectedFormat.Pdf));
        Assert.True(_service.CanConvert(DetectedFormat.Docx, DetectedFormat.Pptx));
    }

    [Fact]
    public void ResolveRoute_ThrowsForUnsupportedTarget_OrMissingFile()
    {
        var dummyDocx = Path.Combine(_testDir, "test.docx");
        File.WriteAllBytes(dummyDocx, [1, 2, 3]);
        var classifiedDocx = _classifier.Classify(dummyDocx);

        Assert.Throws<NotSupportedException>(() =>
            _service.ResolveRoute(classifiedDocx, DetectedFormat.Pdf));

        var missingPng = Path.Combine(_testDir, "nonexistent.png");
        var classifiedMissing = new ClassifiedFile(
            missingPng,
            "nonexistent.png",
            ".png",
            0,
            null,
            DetectedFormat.Png,
            DetectedFormat.Png,
            ClassificationIssue.Missing,
            "Missing",
            null,
            null,
            null);

        Assert.Throws<FileNotFoundException>(() =>
            _service.ResolveRoute(classifiedMissing, DetectedFormat.Webp));
    }

    [Fact]
    public void ResolveRoute_ResolvesCorrectActionsAndDefaults_ForValidCombinations()
    {
        var pngPath = CreateTestImage("sample.png", SKEncodedImageFormat.Png);
        var classifiedPng = _classifier.Classify(pngPath);

        var webpRoute = _service.ResolveRoute(classifiedPng, DetectedFormat.Webp);
        Assert.Equal(ActionIds.ConvertToWebp, webpRoute.ActionId);
        Assert.Equal("85", webpRoute.NormalizedOptions["quality"]);

        var jpegRoute = _service.ResolveRoute(classifiedPng, DetectedFormat.Jpeg, new Dictionary<string, string> { ["quality"] = "92" });
        Assert.Equal(ActionIds.ConvertToJpeg, jpegRoute.ActionId);
        Assert.Equal("92", jpegRoute.NormalizedOptions["quality"]);

        var pdfPath = CreateTestPdf("sample.pdf");
        var classifiedPdf = _classifier.Classify(pdfPath);

        var docxRoute = _service.ResolveRoute(classifiedPdf, DetectedFormat.Docx);
        Assert.Equal(ActionIds.ConvertPdfToDocx, docxRoute.ActionId);
        Assert.Equal("all", docxRoute.NormalizedOptions["pageRange"]);

        var pptxRoute = _service.ResolveRoute(classifiedPdf, DetectedFormat.Pptx, new Dictionary<string, string> { ["slideMode"] = "editable", ["pageRange"] = "1-2" });
        Assert.Equal(ActionIds.ConvertPdfToPptx, pptxRoute.ActionId);
        Assert.Equal("editable", pptxRoute.NormalizedOptions["slideMode"]);
        Assert.Equal("1-2", pptxRoute.NormalizedOptions["pageRange"]);

        var txtRoute = _service.ResolveRoute(classifiedPdf, DetectedFormat.Txt);
        Assert.Equal(ActionIds.ExtractPdfText, txtRoute.ActionId);
        Assert.Equal("all", txtRoute.NormalizedOptions["pageRange"]);

        var pngExportRoute = _service.ResolveRoute(classifiedPdf, DetectedFormat.Png);
        Assert.Equal(ActionIds.ExportPdfPages, pngExportRoute.ActionId);
        Assert.Equal("png", pngExportRoute.NormalizedOptions["format"]);
        Assert.Equal("150", pngExportRoute.NormalizedOptions["dpi"]);

        var jpegExportRoute = _service.ResolveRoute(classifiedPdf, DetectedFormat.Jpeg);
        Assert.Equal(ActionIds.ExportPdfPages, jpegExportRoute.ActionId);
        Assert.Equal("jpeg", jpegExportRoute.NormalizedOptions["format"]);
    }

    [Fact]
    public async Task ConvertAsync_PngToWebp_ProducesValidWebp_LeavesSourceUnchanged()
    {
        var pngPath = CreateTestImage("convert-source.png", SKEncodedImageFormat.Png);
        var originalHash = FileHasher.Sha256Hex(pngPath);
        var classified = _classifier.Classify(pngPath);

        var progressUpdates = new List<int>();
        var progress = new Progress<ProcessingProgress>(p => progressUpdates.Add(p.Percent));

        var result = await _service.ConvertAsync(
            classified,
            DetectedFormat.Webp,
            new Dictionary<string, string> { ["quality"] = "80" },
            _testDir,
            progress,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".webp", result.OutputPath, StringComparison.OrdinalIgnoreCase);

        // Verify source file immutability
        var afterHash = FileHasher.Sha256Hex(pngPath);
        Assert.Equal(originalHash, afterHash);

        // Verify output file header is genuine WebP
        var outClassified = _classifier.Classify(result.OutputPath!);
        Assert.Equal(DetectedFormat.Webp, outClassified.Format);
        Assert.NotEmpty(progressUpdates);
    }

    [Fact]
    public async Task ConvertAsync_PdfToDocx_ProducesValidDocxPackage_LeavesSourceUnchanged()
    {
        var pdfPath = CreateTestPdf("document-source.pdf");
        var originalHash = FileHasher.Sha256Hex(pdfPath);
        var classified = _classifier.Classify(pdfPath);

        var result = await _service.ConvertAsync(
            classified,
            DetectedFormat.Docx,
            new Dictionary<string, string> { ["pageRange"] = "all" },
            _testDir,
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".docx", result.OutputPath, StringComparison.OrdinalIgnoreCase);

        // Verify source file immutability
        var afterHash = FileHasher.Sha256Hex(pdfPath);
        Assert.Equal(originalHash, afterHash);

        // Verify output file is a genuine OpenXML DOCX package
        using var archive = ZipFile.OpenRead(result.OutputPath!);
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_PdfToTxt_ExtractsTextSuccessfully()
    {
        var pdfPath = CreateTestPdf("text-source.pdf");
        var originalHash = FileHasher.Sha256Hex(pdfPath);
        var classified = _classifier.Classify(pdfPath);

        var result = await _service.ConvertAsync(
            classified,
            DetectedFormat.Txt,
            new Dictionary<string, string>(),
            _testDir,
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".txt", result.OutputPath, StringComparison.OrdinalIgnoreCase);

        var afterHash = FileHasher.Sha256Hex(pdfPath);
        Assert.Equal(originalHash, afterHash);
    }

    [Fact]
    public async Task ConvertAsync_RejectsInvalidOptions_WithoutWritingOutput()
    {
        var pngPath = CreateTestImage("bad-options.png", SKEncodedImageFormat.Png);
        var classified = _classifier.Classify(pngPath);

        // Negative quality
        var result = await _service.ConvertAsync(
            classified,
            DetectedFormat.Jpeg,
            new Dictionary<string, string> { ["quality"] = "999" },
            _testDir,
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task ConvertAsync_CancellationGracefullyStops()
    {
        var pngPath = CreateTestImage("cancel.png", SKEncodedImageFormat.Png);
        var classified = _classifier.Classify(pngPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var result = await _service.ConvertAsync(
            classified,
            DetectedFormat.Webp,
            new Dictionary<string, string> { ["quality"] = "80" },
            _testDir,
            null,
            cts.Token);

        Assert.True(result.WasCancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task UnifiedConvertProcessor_DispatchesCorrectly_ViaCatalog()
    {
        var pngPath = CreateTestImage("dispatch.png", SKEncodedImageFormat.Png);
        var classified = _classifier.Classify(pngPath);

        var processor = _processors.Find(ActionIds.UnifiedConvert);
        Assert.NotNull(processor);

        var request = new ProcessingRequest(
            classified,
            ActionIds.UnifiedConvert,
            new ProcessingOptions(new Dictionary<string, string>
            {
                ["targetFormat"] = "webp",
                ["quality"] = "85"
            }),
            _testDir);

        var result = await processor!.ProcessAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.EndsWith(".webp", result.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateTestImage(string name, SKEncodedImageFormat format)
    {
        var path = Path.Combine(_testDir, name);
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.DodgerBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    private string CreateTestPdf(string name)
    {
        var path = Path.Combine(_testDir, name);
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            var font = new PdfSharp.Drawing.XFont("Arial", 14);
            gfx.DrawString("SmartShift Universal Convert Test Document", font, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(40, 60));
            doc.Save(path);
        }
        return path;
    }
}
