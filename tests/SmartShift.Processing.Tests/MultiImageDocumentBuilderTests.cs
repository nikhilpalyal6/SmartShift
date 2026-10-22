using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Documents;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class MultiImageDocumentBuilderTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "smartshift-doc-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _outputDir;
    private readonly FileClassifier _classifier = new();
    private readonly OutputPathAllocator _allocator;
    private readonly MultiImageDocumentBuilder _builder;

    public MultiImageDocumentBuilderTests()
    {
        _outputDir = Path.Combine(_testDir, "Outputs");
        Directory.CreateDirectory(_outputDir);
        _allocator = new OutputPathAllocator(_outputDir);
        _builder = new MultiImageDocumentBuilder(_allocator, _classifier);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private string CreateImage(string filename, int width, int height, SKEncodedImageFormat format, SKColor color)
    {
        var path = Path.Combine(_testDir, filename);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var fs = File.Create(path);
        bitmap.Encode(fs, format, 90);
        return path;
    }

    // ==========================================
    // 1. LAYOUT ENGINE TESTS
    // ==========================================

    [Fact]
    public void DocumentLayoutEngine_PdfA4Portrait_Contain_CentersImageWithinMargins()
    {
        var settings = new DocumentPageSettings(
            PageSize: PageSizeOption.A4,
            Orientation: PageOrientationOption.Portrait,
            Placement: ImagePlacementOption.Contain,
            MarginPoints: 36.0);

        var result = DocumentLayoutEngine.ComputePdfPageLayout(200, 100, settings);

        Assert.Equal(DocumentLayoutEngine.A4WidthPoints, result.TargetWidth);
        Assert.Equal(DocumentLayoutEngine.A4HeightPoints, result.TargetHeight);
        Assert.Null(result.SourceCropRect);
        Assert.True(result.ImageX >= 36.0);
        Assert.True(result.ImageY >= 36.0);
        Assert.True(result.ImageWidth <= result.TargetWidth - 72.0);
        Assert.True(result.ImageHeight <= result.TargetHeight - 72.0);
    }

    [Fact]
    public void DocumentLayoutEngine_PdfAutoOrientation_SelectsCorrectOrientation()
    {
        var landscapeImg = DocumentLayoutEngine.ComputePdfPageLayout(800, 400, new DocumentPageSettings(Orientation: PageOrientationOption.Auto));
        Assert.True(landscapeImg.TargetWidth > landscapeImg.TargetHeight);

        var portraitImg = DocumentLayoutEngine.ComputePdfPageLayout(400, 800, new DocumentPageSettings(Orientation: PageOrientationOption.Auto));
        Assert.True(portraitImg.TargetHeight > portraitImg.TargetWidth);
    }

    [Fact]
    public void DocumentLayoutEngine_PdfFillCrop_ComputesExactCropRectangle()
    {
        var settings = new DocumentPageSettings(
            PageSize: PageSizeOption.A4,
            Orientation: PageOrientationOption.Portrait,
            Placement: ImagePlacementOption.FillCrop,
            MarginPoints: 0);

        // A4 Portrait ratio: ~595 / 842 = ~0.707
        // Landscape image 1000x500 has ratio 2.0 -> wider than A4 -> must crop left & right
        var result = DocumentLayoutEngine.ComputePdfPageLayout(1000, 500, settings);

        Assert.NotNull(result.SourceCropRect);
        var crop = result.SourceCropRect.Value;
        Assert.Equal(500, crop.Height);
        Assert.True(crop.Width < 1000);
        Assert.True(crop.Left > 0);
    }

    [Fact]
    public void DocumentLayoutEngine_Pptx16x9_ContainAndFillCrop_ProduceExpectedDimensions()
    {
        var containResult = DocumentLayoutEngine.ComputePptxSlideLayout(100, 100, new DocumentSlideSettings(Placement: ImagePlacementOption.Contain));
        Assert.Equal(DocumentLayoutEngine.Slide16x9WidthEmu, containResult.TargetWidth);
        Assert.Equal(DocumentLayoutEngine.Slide16x9HeightEmu, containResult.TargetHeight);
        Assert.Null(containResult.SourceCropRect);
        // Square image inside 16:9 slide should have equal width and height, centered horizontally
        Assert.Equal(containResult.ImageWidth, containResult.ImageHeight);
        Assert.True(containResult.ImageX > 0);

        var cropResult = DocumentLayoutEngine.ComputePptxSlideLayout(1000, 1000, new DocumentSlideSettings(Placement: ImagePlacementOption.FillCrop));
        Assert.NotNull(cropResult.SourceCropRect);
        Assert.Equal(DocumentLayoutEngine.Slide16x9WidthEmu, (long)cropResult.ImageWidth);
        Assert.Equal(DocumentLayoutEngine.Slide16x9HeightEmu, (long)cropResult.ImageHeight);
    }

    // ==========================================
    // 2. PDF GENERATION TESTS
    // ==========================================

    [Fact]
    public async Task BuildDocumentAsync_Pdf_GeneratesValidMultiPagePdf_PreservesOrderAndSource()
    {
        var img1 = CreateImage("page1.jpg", 100, 80, SKEncodedImageFormat.Jpeg, SKColors.Red);
        var img2 = CreateImage("page2.png", 80, 120, SKEncodedImageFormat.Png, SKColors.Green);
        var img3 = CreateImage("page3.webp", 150, 100, SKEncodedImageFormat.Webp, SKColors.Blue);

        var hash1 = FileHasher.Sha256Hex(img1);
        var hash2 = FileHasher.Sha256Hex(img2);
        var hash3 = FileHasher.Sha256Hex(img3);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2), _classifier.Classify(img3) };
        var request = new MultiImageDocumentRequest(
            Files: files,
            Mode: DocumentOutputMode.Pdf,
            PageSettings: new DocumentPageSettings(PageSize: PageSizeOption.A4, Orientation: PageOrientationOption.Auto, Placement: ImagePlacementOption.Contain),
            SlideSettings: new DocumentSlideSettings(),
            OutputDirectory: _outputDir,
            CustomFileName: "MultiPageTest.pdf");

        var result = await _builder.BuildDocumentAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(3, result.PageOrSlideCount);

        // Verify PDF open and exact page count
        using (var pdf = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(3, pdf.PageCount);
            // Page 1 was 100x80 (landscape) -> with Auto orientation should be Landscape
            Assert.True(pdf.Pages[0].Width > pdf.Pages[0].Height);
            // Page 2 was 80x120 (portrait) -> with Auto orientation should be Portrait
            Assert.True(pdf.Pages[1].Height > pdf.Pages[1].Width);
        }

        // Verify source files remained completely untouched
        Assert.Equal(hash1, FileHasher.Sha256Hex(img1));
        Assert.Equal(hash2, FileHasher.Sha256Hex(img2));
        Assert.Equal(hash3, FileHasher.Sha256Hex(img3));
    }

    [Fact]
    public async Task BuildDocumentAsync_Pdf_ImageMatchAndLetter_Succeeds()
    {
        var img = CreateImage("letter.png", 200, 200, SKEncodedImageFormat.Png, SKColors.Purple);
        var files = new[] { _classifier.Classify(img) };

        var request = new MultiImageDocumentRequest(
            Files: files,
            Mode: DocumentOutputMode.Pdf,
            PageSettings: new DocumentPageSettings(PageSize: PageSizeOption.Letter, MarginPoints: 18.0),
            SlideSettings: new DocumentSlideSettings(),
            OutputDirectory: _outputDir);

        var result = await _builder.BuildDocumentAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        using var pdf = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(DocumentLayoutEngine.LetterWidthPoints, Math.Round(pdf.Pages[0].Width.Point));
        Assert.Equal(DocumentLayoutEngine.LetterHeightPoints, Math.Round(pdf.Pages[0].Height.Point));
    }

    // ==========================================
    // 3. PPTX GENERATION TESTS
    // ==========================================

    [Fact]
    public async Task BuildDocumentAsync_Pptx_GeneratesValidOpenXmlPresentation_WithRealPictureShapes()
    {
        var img1 = CreateImage("slide1.png", 300, 200, SKEncodedImageFormat.Png, SKColors.DarkOrange);
        var img2 = CreateImage("slide2.jpg", 200, 300, SKEncodedImageFormat.Jpeg, SKColors.SteelBlue);

        var hash1 = FileHasher.Sha256Hex(img1);
        var hash2 = FileHasher.Sha256Hex(img2);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2) };
        var request = new MultiImageDocumentRequest(
            Files: files,
            Mode: DocumentOutputMode.Pptx,
            PageSettings: new DocumentPageSettings(),
            SlideSettings: new DocumentSlideSettings(SlideSize: SlideSizeOption.Widescreen16x9, Placement: ImagePlacementOption.Contain),
            OutputDirectory: _outputDir,
            CustomFileName: "PresentationTest.pptx");

        var result = await _builder.BuildDocumentAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(2, result.PageOrSlideCount);

        // Verify PPTX OpenXML structure
        using (var presentationDoc = PresentationDocument.Open(result.OutputPath, false))
        {
            var presPart = presentationDoc.PresentationPart;
            Assert.NotNull(presPart);

            var slideParts = presPart.SlideParts.ToList();
            Assert.Equal(2, slideParts.Count);

            // Verify each slide has real picture shape and embedded image part
            foreach (var slidePart in slideParts)
            {
                Assert.NotEmpty(slidePart.ImageParts);
                var pictures = slidePart.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Picture>().ToList();
                Assert.Single(pictures);
            }

            // Validate package schema
            var validator = new OpenXmlValidator();
            var validationErrors = validator.Validate(presentationDoc).ToList();
            Assert.True(validationErrors.Count == 0, string.Join("; ", validationErrors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
        }

        // Verify source images untouched
        Assert.Equal(hash1, FileHasher.Sha256Hex(img1));
        Assert.Equal(hash2, FileHasher.Sha256Hex(img2));
    }

    [Fact]
    public async Task BuildDocumentAsync_Pptx_Standard4x3_Succeeds()
    {
        var img = CreateImage("slide43.png", 100, 100, SKEncodedImageFormat.Png, SKColors.Crimson);
        var files = new[] { _classifier.Classify(img) };

        var request = new MultiImageDocumentRequest(
            Files: files,
            Mode: DocumentOutputMode.Pptx,
            PageSettings: new DocumentPageSettings(),
            SlideSettings: new DocumentSlideSettings(SlideSize: SlideSizeOption.Standard4x3, Placement: ImagePlacementOption.FillCrop),
            OutputDirectory: _outputDir);

        var result = await _builder.BuildDocumentAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded);

        using var presDoc = PresentationDocument.Open(result.OutputPath!, false);
        Assert.Single(presDoc.PresentationPart!.SlideParts);
    }

    // ==========================================
    // 4. VALIDATION, SAFETY & PREVIEW TESTS
    // ==========================================

    [Fact]
    public void ValidateRequest_RejectsEmptyOrMissingFiles()
    {
        var emptyReq = new MultiImageDocumentRequest([], DocumentOutputMode.Pdf, new DocumentPageSettings(), new DocumentSlideSettings(), _outputDir);
        var errors = _builder.ValidateRequest(emptyReq);
        Assert.Contains(errors, e => e.Contains("At least one image", StringComparison.OrdinalIgnoreCase));

        var missingReq = new MultiImageDocumentRequest(
            [_classifier.Classify(Path.Combine(_testDir, "missing.png"))],
            DocumentOutputMode.Pdf,
            new DocumentPageSettings(),
            new DocumentSlideSettings(),
            _outputDir);
        var missingErrors = _builder.ValidateRequest(missingReq);
        Assert.Contains(missingErrors, e => e.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildDocumentAsync_CollisionSafeNaming_AllocatesDistinctOutputs()
    {
        var img = CreateImage("item.jpg", 50, 50, SKEncodedImageFormat.Jpeg, SKColors.Teal);
        var files = new[] { _classifier.Classify(img) };

        var req1 = new MultiImageDocumentRequest(files, DocumentOutputMode.Pdf, new DocumentPageSettings(), new DocumentSlideSettings(), _outputDir, "Doc.pdf");
        var res1 = await _builder.BuildDocumentAsync(req1, null, CancellationToken.None);

        var req2 = new MultiImageDocumentRequest(files, DocumentOutputMode.Pdf, new DocumentPageSettings(), new DocumentSlideSettings(), _outputDir, "Doc.pdf");
        var res2 = await _builder.BuildDocumentAsync(req2, null, CancellationToken.None);

        Assert.True(res1.Succeeded);
        Assert.True(res2.Succeeded);
        Assert.NotEqual(res1.OutputPath, res2.OutputPath);
        Assert.True(File.Exists(res1.OutputPath));
        Assert.True(File.Exists(res2.OutputPath));
    }

    [Fact]
    public void RenderPreview_ProducesValidBitmapForPdfAndPptxModes()
    {
        var imgPath = CreateImage("preview_test.png", 400, 300, SKEncodedImageFormat.Png, SKColors.CadetBlue);
        var classified = _classifier.Classify(imgPath);

        using var pdfPreview = _builder.RenderPreview(
            classified,
            DocumentOutputMode.Pdf,
            new DocumentPageSettings(),
            new DocumentSlideSettings(),
            600, 400);

        Assert.NotNull(pdfPreview);
        Assert.Equal(600, pdfPreview.Width);
        Assert.Equal(400, pdfPreview.Height);

        using var pptxPreview = _builder.RenderPreview(
            classified,
            DocumentOutputMode.Pptx,
            new DocumentPageSettings(),
            new DocumentSlideSettings(SlideSize: SlideSizeOption.Widescreen16x9),
            800, 450);

        Assert.NotNull(pptxPreview);
        Assert.Equal(800, pptxPreview.Width);
        Assert.Equal(450, pptxPreview.Height);
    }
}
