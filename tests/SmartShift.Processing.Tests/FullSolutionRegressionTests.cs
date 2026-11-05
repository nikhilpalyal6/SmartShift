using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Core.Logging;
using SmartShift.Infrastructure.Logging;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Documents;
using SmartShift.Processing.Images;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Office;
using SmartShift.Processing.PresentationStudio.Export;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;
using SmartShift.Processing.PresentationStudio.Service;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class FullSolutionRegressionTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _outputRoot;
    private readonly FileClassifier _classifier;
    private readonly OutputPathAllocator _allocator;

    public FullSolutionRegressionTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "smartshift-regression-" + Guid.NewGuid().ToString("N"));
        _outputRoot = Path.Combine(_testRoot, "Outputs");
        Directory.CreateDirectory(_outputRoot);
        _classifier = new FileClassifier();
        _allocator = new OutputPathAllocator(_outputRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private string CreateImage(string filename, int width, int height, SKEncodedImageFormat format, SKColor color)
    {
        var path = Path.Combine(_testRoot, filename);
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(color);
        using var fs = File.Create(path);
        bmp.Encode(fs, format, 90);
        return path;
    }

    private string CreateTestPdfWithText(string filename, int pages, string title = "Regression Document")
    {
        var path = Path.Combine(_testRoot, filename);
        using (var doc = new PdfDocument())
        {
            for (int i = 1; i <= pages; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14);
                gfx.DrawString($"{title} - Section {i}", font, XBrushes.Black, new XPoint(40, 50));
                gfx.DrawString($"• Operational milestone {i} verified locally", font, XBrushes.Navy, new XPoint(40, 80));
                gfx.DrawString($"• Quality assurance passed with zero defects", font, XBrushes.DarkGreen, new XPoint(40, 110));
            }
            doc.Save(path);
        }
        return path;
    }

    // =========================================================================
    // 1. CROSS-FEATURE END-TO-END PIPELINE: Image Edit -> Document Builder -> Presentation Studio
    // =========================================================================
    [Fact]
    public async Task CrossFeature_ImageEdit_Then_DocumentBuilder_Then_PresentationStudio()
    {
        // Step 1: Create and Edit Image with ImageEditSession
        var origImg = CreateImage("source_diagram.png", 600, 400, SKEncodedImageFormat.Png, SKColors.CornflowerBlue);
        var origHash = ComputeSha256(origImg);

        string editedImgPath;
        using (var session = new ImageEditSession(origImg))
        {
            session.Crop(new SKRectI(50, 50, 450, 350)); // 400x300
            session.Rotate90Clockwise(); // 300x400
            session.Resize(150, 200);

            var saveResult = session.Save(_allocator, _outputRoot);
            Assert.True(saveResult.Succeeded);
            Assert.NotNull(saveResult.OutputPath);
            Assert.True(File.Exists(saveResult.OutputPath));
            editedImgPath = saveResult.OutputPath;
        }

        // Verify original image immutability
        Assert.Equal(origHash, ComputeSha256(origImg));
        Assert.NotEqual(origImg, editedImgPath);

        // Step 2: Feed Edited Image into MultiImageDocumentBuilder for PDF & PPTX
        var classifiedImg = _classifier.Classify(editedImgPath);
        var docBuilder = new MultiImageDocumentBuilder(_allocator, _classifier);

        var pdfRequest = new MultiImageDocumentRequest(
            Files: [classifiedImg],
            Mode: DocumentOutputMode.Pdf,
            PageSettings: new DocumentPageSettings(PageSize: PageSizeOption.A4),
            SlideSettings: new DocumentSlideSettings(),
            OutputDirectory: _outputRoot,
            CustomFileName: "MultiImgDoc.pdf");

        var pdfDocResult = await docBuilder.BuildDocumentAsync(pdfRequest, null, CancellationToken.None);
        Assert.True(pdfDocResult.Succeeded);
        Assert.True(File.Exists(pdfDocResult.OutputPath));

        using (var pdf = PdfReader.Open(pdfDocResult.OutputPath!, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, pdf.PageCount);
        }

        var pptxRequest = new MultiImageDocumentRequest(
            Files: [classifiedImg],
            Mode: DocumentOutputMode.Pptx,
            PageSettings: new DocumentPageSettings(),
            SlideSettings: new DocumentSlideSettings(SlideSize: SlideSizeOption.Widescreen16x9),
            OutputDirectory: _outputRoot,
            CustomFileName: "MultiImgDeck.pptx");

        var pptxDocResult = await docBuilder.BuildDocumentAsync(pptxRequest, null, CancellationToken.None);
        Assert.True(pptxDocResult.Succeeded);
        Assert.True(File.Exists(pptxDocResult.OutputPath));

        using (var pptxDoc = PresentationDocument.Open(pptxDocResult.OutputPath!, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pptxDoc).ToList();
            Assert.Empty(errors);
        }

        // Step 3: Import PDF into Presentation Studio, compose deck, attach edited image, and export PPTX
        var studio = new PresentationStudioService(_allocator);
        var importedContent = await studio.ImportDocumentAsync(pdfDocResult.OutputPath!, CancellationToken.None);
        Assert.NotNull(importedContent);

        var outline = studio.GenerateOutline(importedContent, new OutlineGenerationOptions
        {
            GenerateTitleSlide = true,
            ContentDensity = SlideContentDensity.Standard
        });
        Assert.NotNull(outline);

        var deck = PresentationDeck.FromOutline(outline, PresentationTheme.DarkSlate);
        Assert.NotEmpty(deck.Slides);

        // Add a slide using the edited image
        var imgSlide = deck.AddSlide(PresentationLayoutType.TextWithImage, "Visual System Analysis");
        imgSlide.PrimaryBullets = ["High efficiency rendering verified", "Local-first storage confirmed"];
        imgSlide.Image = new SlideImageReference
        {
            ImagePath = editedImgPath,
            NaturalWidth = 150,
            NaturalHeight = 200
        };

        var finalPptx = await studio.ExportPresentationAsync(deck, "StudioOutput.pptx", null, CancellationToken.None);
        Assert.True(File.Exists(finalPptx));

        // Validate final PPTX package schema
        using (var finalPackage = PresentationDocument.Open(finalPptx, false))
        {
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(finalPackage).ToList();
            Assert.Empty(errors);

            var presPart = finalPackage.PresentationPart;
            Assert.NotNull(presPart);
            Assert.Equal(deck.Slides.Count, presPart.SlideParts.Count());
        }
    }

    // =========================================================================
    // 2. MIXED BATCH PROCESSING -> DOCUMENT BUILDER WITH FAILURE ISOLATION
    // =========================================================================
    [Fact]
    public async Task CrossFeature_MixedBatch_WithFailureIsolation_FeedsIntoDocumentBuilder()
    {
        var valid1 = CreateImage("valid1.jpg", 100, 100, SKEncodedImageFormat.Jpeg, SKColors.Red);
        var corrupt = Path.Combine(_testRoot, "corrupt.png");
        await File.WriteAllBytesAsync(corrupt, [0x89, 0x50, 0x4E, 0x47, 0xDE, 0xAD, 0xBE, 0xEF]); // invalid corrupt PNG
        var valid2 = CreateImage("valid2.png", 120, 120, SKEncodedImageFormat.Png, SKColors.Blue);

        var processors = new ProcessorCatalog(_classifier, _allocator);
        var batchEngine = new BatchImageProcessingEngine(processors, _allocator, _classifier);

        var files = new[] { _classifier.Classify(valid1), _classifier.Classify(corrupt), _classifier.Classify(valid2) };
        var batchReq = new BatchJobRequest(files, ActionIds.ConvertToWebp, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputRoot);

        var batchSummary = await batchEngine.ProcessBatchAsync(batchReq, null, CancellationToken.None);

        Assert.Equal(3, batchSummary.TotalCount);
        Assert.Equal(2, batchSummary.SucceededCount);
        Assert.Equal(1, batchSummary.FailedCount);
        Assert.False(batchSummary.AllSucceeded);
        Assert.True(batchSummary.AnySucceeded);

        // Collect successfully converted webp outputs
        var successfulOutputs = batchSummary.ItemResults
            .Where(r => r.Status == BatchItemStatus.Succeeded)
            .Select(r => _classifier.Classify(r.OutputPath!))
            .ToList();
        Assert.Equal(2, successfulOutputs.Count);

        // Build PPTX from successful batch outputs
        var docBuilder = new MultiImageDocumentBuilder(_allocator, _classifier);
        var docReq = new MultiImageDocumentRequest(
            Files: successfulOutputs,
            Mode: DocumentOutputMode.Pptx,
            PageSettings: new DocumentPageSettings(),
            SlideSettings: new DocumentSlideSettings(),
            OutputDirectory: _outputRoot,
            CustomFileName: "BatchConvertedSlides.pptx");

        var docResult = await docBuilder.BuildDocumentAsync(docReq, null, CancellationToken.None);
        Assert.True(docResult.Succeeded);
        Assert.Equal(2, docResult.PageOrSlideCount);

        using var presDoc = PresentationDocument.Open(docResult.OutputPath!, false);
        var validator = new OpenXmlValidator();
        Assert.Empty(validator.Validate(presDoc));
    }

    // =========================================================================
    // 3. COLLISION-SAFE OUTPUTS: Concurrent Writes Between One Conversion & Presentation Studio
    // =========================================================================
    [Fact]
    public async Task CrossFeature_CollisionSafety_BetweenOneConversion_And_PresentationStudio()
    {
        var testPdf = CreateTestPdfWithText("collision_test.pdf", 2);

        // Convert PDF to PPTX via One Conversion
        var oneConversionResult = PdfToPptxConverter.ConvertToPptx(
            testPdf,
            _allocator,
            PptxSlideMode.PageAsImage,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(oneConversionResult.OutputPath));

        // Now export a Presentation Studio deck using the same base name
        var studio = new PresentationStudioService(_allocator);
        var deck = PresentationDeck.CreateDefault(Path.GetFileNameWithoutExtension(testPdf));

        var studioOutputPath = await studio.ExportPresentationAsync(deck, Path.GetFileName(oneConversionResult.OutputPath), null, CancellationToken.None);

        Assert.True(File.Exists(studioOutputPath));
        Assert.NotEqual(oneConversionResult.OutputPath, studioOutputPath);

        // Both files exist independently and are valid PPTX packages
        using (var pkg1 = PresentationDocument.Open(oneConversionResult.OutputPath, false))
        {
            var validator = new OpenXmlValidator();
            Assert.Empty(validator.Validate(pkg1));
        }

        using (var pkg2 = PresentationDocument.Open(studioOutputPath, false))
        {
            var validator = new OpenXmlValidator();
            Assert.Empty(validator.Validate(pkg2));
        }
    }

    // =========================================================================
    // 4. PARTIAL-FILE CLEANUP & CANCELLATION ACROSS ALL ENGINES
    // =========================================================================
    [Fact]
    public async Task CrossFeature_Cancellation_CleansUpPartialOutputs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var img = CreateImage("cancel_test.png", 100, 100, SKEncodedImageFormat.Png, SKColors.Green);
        var classified = _classifier.Classify(img);

        // 1. MultiImageDocumentBuilder cancellation
        var docBuilder = new MultiImageDocumentBuilder(_allocator, _classifier);
        var docReq = new MultiImageDocumentRequest([classified], DocumentOutputMode.Pdf, new DocumentPageSettings(), new DocumentSlideSettings(), _outputRoot, "cancel_test.pdf");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            docBuilder.BuildDocumentAsync(docReq, null, cts.Token));
        Assert.False(File.Exists(Path.Combine(_outputRoot, "cancel_test.pdf.partial")));

        // 2. PresentationPptxExporter cancellation
        var exporter = new PresentationPptxExporter();
        var deck = PresentationDeck.CreateDefault("Cancel Presentation");
        var presPath = Path.Combine(_outputRoot, "cancelled_presentation.pptx");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(deck, presPath, null, cts.Token));

        Assert.False(File.Exists(presPath));
        Assert.False(File.Exists(presPath + ".partial"));
    }

    // =========================================================================
    // 5. PRESENTATION STUDIO SERVICE DI & LIFECYCLE AUDIT
    // =========================================================================
    [Fact]
    public void PresentationStudioService_DiAndLifecycle_WiredCorrectly()
    {
        var service = new PresentationStudioService(_allocator);

        Assert.NotNull(service.Importers);
        Assert.NotNull(service.OutlineGenerator);
        Assert.NotNull(service.AiService);
        Assert.NotNull(service.Exporter);

        // Verify importer catalog can handle supported document types
        Assert.True(service.Importers.IsExtensionSupported(".txt"));
        Assert.True(service.Importers.IsExtensionSupported(".md"));
        Assert.True(service.Importers.IsExtensionSupported(".docx"));
        Assert.True(service.Importers.IsExtensionSupported(".pdf"));
        Assert.False(service.Importers.IsExtensionSupported(".exe"));
    }

    // =========================================================================
    // 6. STEP 4.1 REMNANTS AUDIT: Verify Complete Absence of Step 4.1 Types & Enums
    // =========================================================================
    [Fact]
    public void Step41Removal_ReflectionAudit_ZeroStep41RemnantsInProcessing()
    {
        var processingAssembly = typeof(PresentationDeck).Assembly;
        var allTypes = processingAssembly.GetTypes().ToList();

        // 1. Planning & Classifier types must not exist
        Assert.DoesNotContain(allTypes, t => t.Name == "PresentationPlanner" || t.Name == "IPresentationPlanner");
        Assert.DoesNotContain(allTypes, t => t.Name == "SlideContentClassifier");

        // 2. Web Image Search types must not exist
        Assert.DoesNotContain(allTypes, t => t.Name == "ImageSearchService");
        Assert.DoesNotContain(allTypes, t => t.Name == "PexelsImageSearchProvider");
        Assert.DoesNotContain(allTypes, t => t.Name == "WikimediaCommonsImageSearchProvider");

        // 3. PresentationLayoutType must only have the original 7 values
        var enumNames = Enum.GetNames<PresentationLayoutType>();
        Assert.Equal(7, enumNames.Length);
        Assert.Contains("Title", enumNames);
        Assert.Contains("TitleAndContent", enumNames);
        Assert.Contains("SectionDivider", enumNames);
        Assert.Contains("TwoColumn", enumNames);
        Assert.Contains("TextWithImage", enumNames);
        Assert.Contains("ImageFocused", enumNames);
        Assert.Contains("Blank", enumNames);

        // Step 4.1 layout types must NOT exist
        Assert.DoesNotContain("ProcessFlow", enumNames);
        Assert.DoesNotContain("KeyMetrics", enumNames);
        Assert.DoesNotContain("Comparison", enumNames);
        Assert.DoesNotContain("QuoteTakeaway", enumNames);
    }
}
