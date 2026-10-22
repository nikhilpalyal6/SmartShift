using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Office;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SmartShift.Processing.Tests;

public sealed class PdfOfficeConversionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileClassifier _classifier;
    private readonly OutputPathAllocator _allocator;

    public PdfOfficeConversionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smartshift-office-tests-" + Guid.NewGuid().ToString("N"));
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
        catch { }
    }

    private string CreateTestPdf(int pageCount, string textPrefix = "Quarterly Report")
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 1; i <= pageCount; i++)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(600);
                page.Height = XUnit.FromPoint(800);

                using var gfx = XGraphics.FromPdfPage(page);
                var titleFont = new XFont("Arial", 16);
                var bodyFont = new XFont("Arial", 11);

                gfx.DrawString($"{textPrefix} - Section {i}", titleFont, XBrushes.Navy, new XPoint(50, 60));
                gfx.DrawString($"SmartShift automated local processing and verification test for slide/page {i}.", bodyFont, XBrushes.Black, new XPoint(50, 90));
                gfx.DrawRectangle(XPens.DarkBlue, 40, 40, 520, 720);
            }
            doc.Save(path);
        }
        return path;
    }

    private string CreateEmptyPagePdf()
    {
        var path = Path.Combine(_tempDir, $"empty_{Guid.NewGuid():N}.pdf");
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

    [Fact]
    public void PdfToDocxConverter_ConvertsTextPdf_ProducesValidOpenXmlDocx()
    {
        var pdfPath = CreateTestPdf(2, "Word Conversion Test");
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfToDocxConverter.ConvertToDocx(
            pdfPath,
            _allocator,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".docx", result.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsScannedOrImageOnly);
        Assert.True(result.TotalCharactersExtracted > 0);
        Assert.Equal(2, result.PagesConvertedCount);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore); // Immutability

        // Validate Office Open XML package structure
        var validator = new OpenXmlValidator();
        using (var docx = WordprocessingDocument.Open(result.OutputPath, false))
        {
            Assert.NotNull(docx.MainDocumentPart?.Document?.Body);

            var validationErrors = validator.Validate(docx);
            Assert.DoesNotContain(validationErrors, e => e.ErrorType == ValidationErrorType.Schema && !e.Description.Contains("attribute"));

            var textContent = docx.MainDocumentPart!.Document.Body!.InnerText;
            Assert.Contains("Word Conversion Test", textContent);
            Assert.Contains("Section 1", textContent);
            Assert.Contains("Section 2", textContent);
        }
    }

    [Fact]
    public void PdfToDocxConverter_ScannedEmptyPdf_HonestReporting()
    {
        var pdfPath = CreateEmptyPagePdf();
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfToDocxConverter.ConvertToDocx(
            pdfPath,
            _allocator,
            pageRange: "1",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.True(result.IsScannedOrImageOnly);
        Assert.Equal(0, result.TotalCharactersExtracted);
        Assert.Contains("OCR required", result.SummaryNote);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore);

        using (var docx = WordprocessingDocument.Open(result.OutputPath, false))
        {
            Assert.NotNull(docx.MainDocumentPart?.Document?.Body);
            var drawings = docx.MainDocumentPart!.Document.Body!.Descendants<Drawing>().ToList();
            Assert.NotEmpty(drawings); // Embedded image present
        }
    }

    [Fact]
    public void PdfToPptxConverter_PageAsImageMode_ProducesValidOpenXmlPptx()
    {
        var pdfPath = CreateTestPdf(3, "PowerPoint Image Mode");
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfToPptxConverter.ConvertToPptx(
            pdfPath,
            _allocator,
            PptxSlideMode.PageAsImage,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".pptx", result.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PptxSlideMode.PageAsImage, result.SlideMode);
        Assert.Equal(3, result.SlidesCreatedCount);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore);

        var validator = new OpenXmlValidator();
        using (var pptx = PresentationDocument.Open(result.OutputPath, false))
        {
            Assert.NotNull(pptx.PresentationPart);
            Assert.NotNull(pptx.PresentationPart.Presentation);
            Assert.NotNull(pptx.PresentationPart.Presentation.SlideIdList);
            Assert.Equal(3, pptx.PresentationPart.Presentation.SlideIdList!.ChildElements.Count);

            var validationErrors = validator.Validate(pptx);
            Assert.DoesNotContain(validationErrors, e => e.ErrorType == ValidationErrorType.Schema && !e.Description.Contains("attribute"));
        }
    }

    [Fact]
    public void PdfToPptxConverter_EditableMode_ReconstructsTextElements()
    {
        var pdfPath = CreateTestPdf(2, "PowerPoint Editable Mode");
        var shaBefore = ComputeSha256(pdfPath);

        var result = PdfToPptxConverter.ConvertToPptx(
            pdfPath,
            _allocator,
            PptxSlideMode.Editable,
            pageRange: "all",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(PptxSlideMode.Editable, result.SlideMode);
        Assert.Equal(2, result.SlidesCreatedCount);
        Assert.Equal(ComputeSha256(pdfPath), shaBefore);

        using (var pptx = PresentationDocument.Open(result.OutputPath, false))
        {
            Assert.NotNull(pptx.PresentationPart);
            Assert.NotNull(pptx.PresentationPart.Presentation);
            Assert.NotNull(pptx.PresentationPart.Presentation.SlideIdList);
            Assert.Equal(2, pptx.PresentationPart.Presentation.SlideIdList!.ChildElements.Count);

            var firstSlideRelId = ((P.SlideId)pptx.PresentationPart.Presentation.SlideIdList.FirstChild!).RelationshipId!.Value!;
            var firstSlide = (SlidePart)pptx.PresentationPart.GetPartById(firstSlideRelId);
            Assert.NotNull(firstSlide.Slide);
            var textRuns = firstSlide.Slide.Descendants<A.Text>().Select(t => t.Text).ToList();
            Assert.Contains(textRuns, t => t.Contains("PowerPoint Editable Mode"));
        }
    }

    [Fact]
    public void PdfToPptxConverter_PageRangeFiltering_ConvertsOnlySelectedPages()
    {
        var pdfPath = CreateTestPdf(4, "Range Filter Test");

        var result = PdfToPptxConverter.ConvertToPptx(
            pdfPath,
            _allocator,
            PptxSlideMode.PageAsImage,
            pageRange: "2, 4",
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SlidesCreatedCount);
        Assert.Equal(4, result.TotalPagesInDoc);

        using (var pptx = PresentationDocument.Open(result.OutputPath, false))
        {
            Assert.NotNull(pptx.PresentationPart);
            Assert.NotNull(pptx.PresentationPart.Presentation);
            Assert.NotNull(pptx.PresentationPart.Presentation.SlideIdList);
            Assert.Equal(2, pptx.PresentationPart.Presentation.SlideIdList!.ChildElements.Count);
        }
    }

    [Fact]
    public void OfficeConverters_Cancellation_CleansUpPartials()
    {
        var pdfPath = CreateTestPdf(2, "Cancel Office");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        Assert.Throws<OperationCanceledException>(() =>
            PdfToDocxConverter.ConvertToDocx(
                pdfPath,
                _allocator,
                pageRange: "all",
                progress: null,
                cancellationToken: cts.Token));

        Assert.Throws<OperationCanceledException>(() =>
            PdfToPptxConverter.ConvertToPptx(
                pdfPath,
                _allocator,
                PptxSlideMode.PageAsImage,
                pageRange: "all",
                progress: null,
                cancellationToken: cts.Token));

        var partials = Directory.GetFiles(_tempDir, "*.partial", SearchOption.AllDirectories);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task PdfDocxProcessor_EndToEnd_ProcessesRequestSuccessfully()
    {
        var pdfPath = CreateTestPdf(2, "Docx Processor Test");
        var classified = _classifier.Classify(pdfPath);

        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["pageRange"] = "all"
        });

        var request = new ProcessingRequest(classified, ActionIds.ConvertPdfToDocx, options, _tempDir);
        var processor = new PdfDocxProcessor(_allocator, _classifier);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(result.OriginalUnchanged);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".docx", result.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DOCX", result.Details["outputFormat"]);
    }

    [Fact]
    public async Task PdfPptxProcessor_EndToEnd_ProcessesRequestSuccessfully()
    {
        var pdfPath = CreateTestPdf(2, "Pptx Processor Test");
        var classified = _classifier.Classify(pdfPath);

        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["slideMode"] = "pageAsImage",
            ["pageRange"] = "all"
        });

        var request = new ProcessingRequest(classified, ActionIds.ConvertPdfToPptx, options, _tempDir);
        var processor = new PdfPptxProcessor(_allocator, _classifier);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(result.OriginalUnchanged);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".pptx", result.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PPTX", result.Details["outputFormat"]);
        Assert.Equal("pageAsImage", result.Details["slideMode"]);
    }
}
