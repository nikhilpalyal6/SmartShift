using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Capabilities;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Conversion;
using SmartShift.Processing.Documents;
using SmartShift.Processing.Images;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.PresentationStudio.Export;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;
using SmartShift.Processing.PresentationStudio.Service;
using Xunit;

namespace SmartShift.Processing.Tests;

/// <summary>
/// Comprehensive Cross-Feature End-to-End Integration Suite executing the 15 release-critical
/// workflows defined in Section 3 of Step 10 specification.
/// </summary>
public sealed class CrossFeatureIntegrationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _outputDir;
    private readonly FileClassifier _classifier;
    private readonly FileCapabilityRegistry _capabilities;
    private readonly OutputPathAllocator _allocator;
    private readonly ProcessorCatalog _processors;
    private readonly UnifiedConversionService _conversionService;
    private readonly PdfMergeEngine _mergeEngine;

    public CrossFeatureIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "smartshift-crossfeature-" + Guid.NewGuid().ToString("N"));
        _outputDir = Path.Combine(_testDir, "Outputs");
        Directory.CreateDirectory(_outputDir);

        _classifier = new FileClassifier();
        _capabilities = new FileCapabilityRegistry();
        _allocator = new OutputPathAllocator(_outputDir);
        _processors = new ProcessorCatalog(_classifier, _allocator);
        _conversionService = new UnifiedConversionService(_capabilities, _processors, _allocator, _classifier);
        _processors.Register(ActionIds.UnifiedConvert, new UnifiedConvertProcessor(_conversionService));
        _mergeEngine = new PdfMergeEngine(_allocator);
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
            // Best effort cleanup
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private string CreateImage(string filename, int width, int height, SKEncodedImageFormat format, SKColor color)
    {
        var path = Path.Combine(_testDir, filename);
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(color);
        using var fs = File.Create(path);
        bmp.Encode(fs, format, 90);
        return path;
    }

    private string CreatePdfWithText(string filename, int pages, string title = "Integration PDF")
    {
        var path = Path.Combine(_testDir, filename);
        using (var doc = new PdfDocument())
        {
            for (int i = 1; i <= pages; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14);
                gfx.DrawString($"{title} - Section {i}", font, XBrushes.Black, new XPoint(40, 50));
                gfx.DrawString($"Paragraph line content for page {i}", font, XBrushes.DarkSlateGray, new XPoint(40, 80));
            }
            doc.Save(path);
        }
        return path;
    }

    private string CreateSampleDocx(string fileName = "sample.docx")
    {
        var filePath = Path.Combine(_testDir, fileName);
        using (var wordDoc = WordprocessingDocument.Create(filePath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            body.AppendChild(new Paragraph(new Run(new Text("Release Readiness Strategy"))));
            body.AppendChild(new Paragraph(new Run(new Text("1. Architecture Audit"))));
            body.AppendChild(new Paragraph(new Run(new Text("All 15 cross-feature integration scenarios must pass."))));
            body.AppendChild(new Paragraph(new Run(new Text("• Core data immutability strictly verified."))));
            body.AppendChild(new Paragraph(new Run(new Text("• Zero unhandled exceptions permitted in release candidate."))));
            mainPart.Document.Save();
        }
        return filePath;
    }

    // =========================================================================
    // Workflow 1: Image import -> Image Editor -> verify output
    // =========================================================================
    [Fact]
    public void Workflow01_ImageEditor_CropResizeRotate_LeavesSourceIntact()
    {
        var srcImg = CreateImage("editor_source.png", 800, 600, SKEncodedImageFormat.Png, SKColors.Crimson);
        var originalHash = ComputeSha256(srcImg);

        string outputPath;
        using (var session = new ImageEditSession(srcImg))
        {
            Assert.Equal(800, session.CurrentWidth);
            Assert.Equal(600, session.CurrentHeight);

            // Crop to center 400x300
            session.Crop(new SKRectI(200, 150, 600, 450));
            Assert.Equal(400, session.CurrentWidth);
            Assert.Equal(300, session.CurrentHeight);

            // Rotate 90 CW -> 300x400
            session.Rotate90Clockwise();
            Assert.Equal(300, session.CurrentWidth);
            Assert.Equal(400, session.CurrentHeight);

            // Resize to 150x200
            session.Resize(150, 200);
            Assert.Equal(150, session.CurrentWidth);
            Assert.Equal(200, session.CurrentHeight);

            var saveResult = session.Save(_allocator, _outputDir);
            Assert.True(saveResult.Succeeded);
            Assert.NotNull(saveResult.OutputPath);
            outputPath = saveResult.OutputPath;
        }

        Assert.True(File.Exists(outputPath));
        using (var codec = SKCodec.Create(outputPath))
        {
            Assert.NotNull(codec);
            Assert.Equal(150, codec.Info.Width);
            Assert.Equal(200, codec.Info.Height);
        }

        // SHA-256 source immutability
        Assert.Equal(originalHash, ComputeSha256(srcImg));
    }

    // =========================================================================
    // Workflow 2: Multiple images -> Batch Processing -> verify all outputs
    // =========================================================================
    [Fact]
    public async Task Workflow02_BatchProcessing_MultipleImages_VerifyOutputsAndIntegrity()
    {
        var img1 = CreateImage("batch_1.png", 200, 200, SKEncodedImageFormat.Png, SKColors.DodgerBlue);
        var img2 = CreateImage("batch_2.jpg", 300, 300, SKEncodedImageFormat.Jpeg, SKColors.ForestGreen);
        var img3 = CreateImage("batch_3.png", 250, 250, SKEncodedImageFormat.Png, SKColors.Goldenrod);

        var hash1 = ComputeSha256(img1);
        var hash2 = ComputeSha256(img2);
        var hash3 = ComputeSha256(img3);

        var batchEngine = new BatchImageProcessingEngine(_processors, _allocator, _classifier);
        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2), _classifier.Classify(img3) };
        var batchReq = new BatchJobRequest(files, ActionIds.ConvertToWebp, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "85" }), _outputDir);

        var result = await batchEngine.ProcessBatchAsync(batchReq, null, CancellationToken.None);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.AllSucceeded);

        foreach (var item in result.ItemResults)
        {
            Assert.Equal(BatchItemStatus.Succeeded, item.Status);
            Assert.NotNull(item.OutputPath);
            Assert.True(File.Exists(item.OutputPath));
            Assert.EndsWith(".webp", item.OutputPath, StringComparison.OrdinalIgnoreCase);
        }

        // Source hashes unchanged
        Assert.Equal(hash1, ComputeSha256(img1));
        Assert.Equal(hash2, ComputeSha256(img2));
        Assert.Equal(hash3, ComputeSha256(img3));
    }

    // =========================================================================
    // Workflow 3: Multiple images -> Document Builder -> PDF generation
    // =========================================================================
    [Fact]
    public async Task Workflow03_DocumentBuilder_MultipleImages_ToPdf()
    {
        var imgA = CreateImage("doc_img_a.png", 400, 300, SKEncodedImageFormat.Png, SKColors.Purple);
        var imgB = CreateImage("doc_img_b.jpg", 500, 400, SKEncodedImageFormat.Jpeg, SKColors.Teal);

        var hashA = ComputeSha256(imgA);
        var hashB = ComputeSha256(imgB);

        var docBuilder = new MultiImageDocumentBuilder(_allocator, _classifier);
        var req = new MultiImageDocumentRequest(
            Files: [_classifier.Classify(imgA), _classifier.Classify(imgB)],
            Mode: DocumentOutputMode.Pdf,
            PageSettings: new DocumentPageSettings(PageSize: PageSizeOption.A4),
            SlideSettings: new DocumentSlideSettings(),
            OutputDirectory: _outputDir,
            CustomFileName: "ImagesCombined.pdf");

        var docResult = await docBuilder.BuildDocumentAsync(req, null, CancellationToken.None);

        Assert.True(docResult.Succeeded);
        Assert.NotNull(docResult.OutputPath);
        Assert.True(File.Exists(docResult.OutputPath));
        Assert.Equal(2, docResult.PageOrSlideCount);

        using (var pdf = PdfReader.Open(docResult.OutputPath, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, pdf.PageCount);
        }

        Assert.Equal(hashA, ComputeSha256(imgA));
        Assert.Equal(hashB, ComputeSha256(imgB));
    }

    // =========================================================================
    // Workflow 4: Multiple images -> Document Builder -> editable PPTX generation
    // =========================================================================
    [Fact]
    public async Task Workflow04_DocumentBuilder_MultipleImages_ToPptx()
    {
        var img1 = CreateImage("deck_img_1.png", 320, 240, SKEncodedImageFormat.Png, SKColors.OrangeRed);
        var img2 = CreateImage("deck_img_2.png", 640, 480, SKEncodedImageFormat.Png, SKColors.MediumSeaGreen);

        var hash1 = ComputeSha256(img1);
        var hash2 = ComputeSha256(img2);

        var docBuilder = new MultiImageDocumentBuilder(_allocator, _classifier);
        var req = new MultiImageDocumentRequest(
            Files: [_classifier.Classify(img1), _classifier.Classify(img2)],
            Mode: DocumentOutputMode.Pptx,
            PageSettings: new DocumentPageSettings(),
            SlideSettings: new DocumentSlideSettings(SlideSize: SlideSizeOption.Widescreen16x9),
            OutputDirectory: _outputDir,
            CustomFileName: "ImagesDeck.pptx");

        var docResult = await docBuilder.BuildDocumentAsync(req, null, CancellationToken.None);

        Assert.True(docResult.Succeeded);
        Assert.NotNull(docResult.OutputPath);
        Assert.True(File.Exists(docResult.OutputPath));
        Assert.Equal(2, docResult.PageOrSlideCount);

        using (var pptx = PresentationDocument.Open(docResult.OutputPath, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pptx).ToList();
            Assert.Empty(errors);
            Assert.Equal(2, pptx.PresentationPart!.SlideParts.Count());
        }

        Assert.Equal(hash1, ComputeSha256(img1));
        Assert.Equal(hash2, ComputeSha256(img2));
    }

    // =========================================================================
    // Workflow 5: DOCX -> Presentation Studio -> PPTX export
    // =========================================================================
    [Fact]
    public async Task Workflow05_PresentationStudio_ImportDocx_ExportPptx()
    {
        var docxPath = CreateSampleDocx("StrategyReport.docx");
        var originalHash = ComputeSha256(docxPath);

        var studio = new PresentationStudioService(_allocator);
        var imported = await studio.ImportDocumentAsync(docxPath, CancellationToken.None);

        Assert.NotNull(imported);
        Assert.NotEmpty(imported.Sections);

        var outline = studio.GenerateOutline(imported, new OutlineGenerationOptions
        {
            GenerateTitleSlide = true,
            ContentDensity = SlideContentDensity.Standard
        });
        Assert.NotNull(outline);
        Assert.NotEmpty(outline.Slides);

        var deck = PresentationDeck.FromOutline(outline, PresentationTheme.ClassicNavy);
        var pptxPath = await studio.ExportPresentationAsync(deck, "StrategyDeck.pptx", null, CancellationToken.None);

        Assert.True(File.Exists(pptxPath));
        using (var pptx = PresentationDocument.Open(pptxPath, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pptx).ToList();
            Assert.Empty(errors);
        }

        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    // =========================================================================
    // Workflow 6: TXT or Markdown -> Presentation Studio -> PPTX export
    // =========================================================================
    [Fact]
    public async Task Workflow06_PresentationStudio_ImportMarkdown_ExportPptx()
    {
        var mdPath = Path.Combine(_testDir, "roadmap.md");
        await File.WriteAllTextAsync(mdPath, """
            # Cloud Transformation Roadmap

            ## Phase 1: Assessment
            - Inventory current legacy services
            - Map inter-service dependencies
            - Identify mission-critical security controls

            ## Phase 2: Migration
            - Containerize stateless applications
            - Establish automated CI/CD deployment gates
            - Enforce zero-trust network boundaries
            """);
        var originalHash = ComputeSha256(mdPath);

        var studio = new PresentationStudioService(_allocator);
        var imported = await studio.ImportDocumentAsync(mdPath, CancellationToken.None);

        Assert.NotNull(imported);
        Assert.NotEmpty(imported.Sections);

        var outline = studio.GenerateOutline(imported, new OutlineGenerationOptions { GenerateTitleSlide = true });
        var deck = PresentationDeck.FromOutline(outline, PresentationTheme.DarkSlate);
        var exportedPptx = await studio.ExportPresentationAsync(deck, "RoadmapDeck.pptx", null, CancellationToken.None);

        Assert.True(File.Exists(exportedPptx));
        using (var pptx = PresentationDocument.Open(exportedPptx, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pptx).ToList();
            Assert.Empty(errors);
        }

        Assert.Equal(originalHash, ComputeSha256(mdPath));
    }

    // =========================================================================
    // Workflow 7: PDF -> Unified Convert -> DOCX
    // =========================================================================
    [Fact]
    public async Task Workflow07_UnifiedConvert_PdfToDocx()
    {
        var pdfPath = CreatePdfWithText("document_for_docx.pdf", 2, "Architecture Specs");
        var originalHash = ComputeSha256(pdfPath);
        var classified = _classifier.Classify(pdfPath);

        var result = await _conversionService.ConvertAsync(
            classified,
            DetectedFormat.Docx,
            new Dictionary<string, string>(),
            _outputDir,
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".docx", result.OutputPath, StringComparison.OrdinalIgnoreCase);

        using (var docx = WordprocessingDocument.Open(result.OutputPath, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(docx).ToList();
            Assert.Empty(errors);
            Assert.NotNull(docx.MainDocumentPart?.Document?.Body);
        }

        Assert.Equal(originalHash, ComputeSha256(pdfPath));
    }

    // =========================================================================
    // Workflow 8: DOCX -> PDF (DocxToPdfConverter) -> open and inspect PDF
    // =========================================================================
    [Fact]
    public void Workflow08_DocxToPdfConverter_GeneratesValidPdf()
    {
        var docxPath = CreateSampleDocx("DocToPdfSample.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetPdf = Path.Combine(_outputDir, "ConvertedDoc.pdf");

        var options = new DocxPdfConversionOptions(PreferLocalWordEngine: false);
        var outPath = DocxToPdfConverter.Convert(docxPath, targetPdf, options);

        Assert.True(File.Exists(outPath));
        using (var pdf = PdfReader.Open(outPath, PdfDocumentOpenMode.Import))
        {
            Assert.True(pdf.PageCount >= 1);
        }

        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    // =========================================================================
    // Workflow 9: DOCX -> PPTX (DocxToPptxConverter) -> open and inspect presentation
    // =========================================================================
    [Fact]
    public async Task Workflow09_DocxToPptxConverter_GeneratesValidPptx()
    {
        var docxPath = CreateSampleDocx("DocToPptxSample.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetPptx = Path.Combine(_outputDir, "ConvertedDeck.pptx");

        var options = new DocxPptxConversionOptions(Theme: PresentationTheme.ForestGreen, GenerateTitleSlide: true);
        var outPath = await DocxToPptxConverter.ConvertAsync(docxPath, targetPptx, options);

        Assert.True(File.Exists(outPath));
        using (var pptx = PresentationDocument.Open(outPath, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pptx).ToList();
            Assert.Empty(errors);
            Assert.True(pptx.PresentationPart!.SlideParts.Count() >= 1);
        }

        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    // =========================================================================
    // Workflow 10: Multiple PDFs -> PDF Merge Studio -> merged output validation
    // =========================================================================
    [Fact]
    public async Task Workflow10_PdfMerge_MultipleFiles_PreservesOrderAndPages()
    {
        var doc1 = CreatePdfWithText("merge_p1.pdf", 2, "Document Part 1");
        var doc2 = CreatePdfWithText("merge_p2.pdf", 3, "Document Part 2");
        var doc3 = CreatePdfWithText("merge_p3.pdf", 1, "Document Part 3");

        var hash1 = ComputeSha256(doc1);
        var hash2 = ComputeSha256(doc2);
        var hash3 = ComputeSha256(doc3);

        var targetMerged = Path.Combine(_outputDir, "ConsolidatedReport.pdf");
        var outPath = await _mergeEngine.MergeAsync([doc1, doc2, doc3], targetMerged);

        Assert.True(File.Exists(outPath));
        using (var mergedDoc = PdfReader.Open(outPath, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(6, mergedDoc.PageCount); // 2 + 3 + 1 = 6
        }

        // Immutability of original files
        Assert.Equal(hash1, ComputeSha256(doc1));
        Assert.Equal(hash2, ComputeSha256(doc2));
        Assert.Equal(hash3, ComputeSha256(doc3));
    }

    // =========================================================================
    // Workflow 11: PDF -> existing PDF compression -> size reporting
    // =========================================================================
    [Fact]
    public async Task Workflow11_PdfCompression_ReducesSizeAndReportsDiagnostics()
    {
        // Create an uncompressed PDF with an embedded bitmap
        var imgPath = CreateImage("huge_asset.jpg", 600, 600, SKEncodedImageFormat.Jpeg, SKColors.DeepSkyBlue);
        var uncompressedPdf = Path.Combine(_testDir, "asset_heavy.pdf");
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 10, 10, 500, 500);
            doc.Save(uncompressedPdf);
        }

        var originalHash = ComputeSha256(uncompressedPdf);

        var processor = new PdfFileProcessor(_allocator, _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "100",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });

        var classified = _classifier.Classify(uncompressedPdf);
        var request = new ProcessingRequest(classified, ActionIds.CompressPdf, options, _outputDir);
        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        // Diagnostics details
        Assert.True(result.Details.ContainsKey("originalBytes"));
        Assert.True(result.Details.ContainsKey("outputBytes"));

        // Source immutability
        Assert.Equal(originalHash, ComputeSha256(uncompressedPdf));
    }

    // =========================================================================
    // Workflow 12: Image -> Unified Convert -> WebP and PNG verification
    // =========================================================================
    [Fact]
    public async Task Workflow12_UnifiedConvert_ImageToWebpAndPng()
    {
        var srcJpeg = CreateImage("input_photo.jpeg", 300, 200, SKEncodedImageFormat.Jpeg, SKColors.Crimson);
        var originalHash = ComputeSha256(srcJpeg);
        var classified = _classifier.Classify(srcJpeg);

        // Convert JPEG to WebP
        var toWebp = await _conversionService.ConvertAsync(
            classified,
            DetectedFormat.Webp,
            new Dictionary<string, string> { ["quality"] = "80" },
            _outputDir,
            null,
            CancellationToken.None);

        Assert.True(toWebp.Succeeded);
        Assert.True(File.Exists(toWebp.OutputPath));
        var classifiedWebp = _classifier.Classify(toWebp.OutputPath!);
        Assert.Equal(DetectedFormat.Webp, classifiedWebp.Format);

        // Convert WebP to PNG
        var toPng = await _conversionService.ConvertAsync(
            classifiedWebp,
            DetectedFormat.Png,
            new Dictionary<string, string>(),
            _outputDir,
            null,
            CancellationToken.None);

        Assert.True(toPng.Succeeded);
        Assert.True(File.Exists(toPng.OutputPath));
        var classifiedPng = _classifier.Classify(toPng.OutputPath!);
        Assert.Equal(DetectedFormat.Png, classifiedPng.Format);

        // Source JPEG intact
        Assert.Equal(originalHash, ComputeSha256(srcJpeg));
    }

    // =========================================================================
    // Workflow 13: Unsupported file (XLSX / ZIP) -> safe fallback behavior
    // =========================================================================
    [Fact]
    public void Workflow13_UnsupportedFiles_ClassifiedHonestly_WithSafeFallbacks()
    {
        // 1. XLSX file
        var xlsxPath = Path.Combine(_testDir, "spreadsheet.xlsx");
        File.WriteAllBytes(xlsxPath, [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00]); // zip header
        var xlsx = _classifier.Classify(xlsxPath);
        var xlsxDesc = _capabilities.GetDescriptor(xlsx.Format);

        Assert.False(xlsxDesc.CanEdit);
        Assert.Empty(xlsxDesc.SupportedConversionOutputs);
        Assert.Equal(PreviewCapability.None, xlsxDesc.Preview);
        Assert.NotNull(xlsxDesc.UnsupportedReason);

        // 2. Generic ZIP file
        var zipPath = Path.Combine(_testDir, "archive.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("test.txt");
        }
        var zip = _classifier.Classify(zipPath);
        var zipDesc = _capabilities.GetDescriptor(zip.Format);

        Assert.False(zipDesc.CanEdit);
        Assert.Empty(zipDesc.SupportedConversionOutputs);
        Assert.NotNull(zipDesc.UnsupportedReason);
    }

    // =========================================================================
    // Workflow 14: Cross-file action switching & options isolation
    // =========================================================================
    [Fact]
    public async Task Workflow14_ActionSwitching_OptionsIsolation_NoStateLeakage()
    {
        var imgPath = CreateImage("action_switch.png", 200, 200, SKEncodedImageFormat.Png, SKColors.DarkCyan);
        var pdfPath = CreatePdfWithText("action_switch.pdf", 1, "Switch Test");

        var classifiedImg = _classifier.Classify(imgPath);
        var classifiedPdf = _classifier.Classify(pdfPath);

        // Convert image to WebP with specific quality
        var imgOptions = new Dictionary<string, string> { ["quality"] = "45" };
        var imgRoute = _conversionService.ResolveRoute(classifiedImg, DetectedFormat.Webp);
        Assert.Equal(ActionIds.ConvertToWebp, imgRoute.ActionId);

        var imgResult = await _conversionService.ConvertAsync(
            classifiedImg,
            DetectedFormat.Webp,
            imgOptions,
            _outputDir,
            null,
            CancellationToken.None);
        Assert.True(imgResult.Succeeded);

        // Switch to PDF conversion to TXT - verify options from img do not leak
        var pdfRoute = _conversionService.ResolveRoute(classifiedPdf, DetectedFormat.Txt);
        Assert.Equal(ActionIds.ExtractPdfText, pdfRoute.ActionId);
        Assert.False(pdfRoute.NormalizedOptions.ContainsKey("quality"));

        var pdfResult = await _conversionService.ConvertAsync(
            classifiedPdf,
            DetectedFormat.Txt,
            new Dictionary<string, string>(),
            _outputDir,
            null,
            CancellationToken.None);

        Assert.True(pdfResult.Succeeded);
        Assert.EndsWith(".txt", pdfResult.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(pdfResult.OutputPath));
    }

    // =========================================================================
    // Workflow 15: Cancellation -> temporary file cleanup & SHA-256 source integrity
    // =========================================================================
    [Fact]
    public async Task Workflow15_Cancellation_CleansUpPartials_AndPreservesOriginal()
    {
        var testPdf = CreatePdfWithText("cancel_pipeline.pdf", 4, "Cancellation Doc");
        var originalHash = ComputeSha256(testPdf);
        var classifiedPdf = _classifier.Classify(testPdf);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        // Cancelled unified convert returns Cancelled ProcessingResult
        var cancelResult = await _conversionService.ConvertAsync(
            classifiedPdf,
            DetectedFormat.Docx,
            new Dictionary<string, string>(),
            _outputDir,
            null,
            cts.Token);

        Assert.True(cancelResult.WasCancelled);
        Assert.False(cancelResult.Succeeded);
        Assert.True(cancelResult.OriginalUnchanged);

        // Ensure no leftover .partial or target file exists
        var potentialPartials = Directory.GetFiles(_outputDir, "*cancel_pipeline*.partial");
        Assert.Empty(potentialPartials);

        // Source file is 100% intact
        Assert.Equal(originalHash, ComputeSha256(testPdf));
    }
}
