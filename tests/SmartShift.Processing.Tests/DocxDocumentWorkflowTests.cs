using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Pdf.IO;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Documents;
using SmartShift.Processing.PresentationStudio.Models;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class DocxDocumentWorkflowTests : IDisposable
{
    private readonly string _testDir;
    private readonly OutputPathAllocator _outputAllocator;
    private readonly FileClassifier _classifier;

    public DocxDocumentWorkflowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "smartshift-docx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _outputAllocator = new OutputPathAllocator(_testDir);
        _classifier = new FileClassifier();
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

    private string CreateSampleDocx(string fileName = "sample.docx")
    {
        var filePath = Path.Combine(_testDir, fileName);
        using (var wordDoc = WordprocessingDocument.Create(filePath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            // Title
            body.AppendChild(new Paragraph(new Run(new Text("Executive Summary"))));

            // Section 1 Heading
            body.AppendChild(new Paragraph(new Run(new Text("1. Financial Overview"))));

            // Body Paragraphs
            body.AppendChild(new Paragraph(new Run(new Text("Revenue increased by 24% year-over-year with strong margins."))));
            body.AppendChild(new Paragraph(new Run(new Text("Operational costs remained strictly within planned thresholds."))));

            // Bullet list
            body.AppendChild(new Paragraph(new Run(new Text("• Core platform stability reached 99.99%."))));
            body.AppendChild(new Paragraph(new Run(new Text("• Customer retention improved to 96%."))));
            body.AppendChild(new Paragraph(new Run(new Text("• Zero security incidents reported in Q3."))));

            // Table with 2 rows, 2 columns
            var table = new Table();
            var row1 = new TableRow();
            row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Metric")))));
            row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Result")))));
            table.AppendChild(row1);

            var row2 = new TableRow();
            row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("Growth")))));
            row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("+24%")))));
            table.AppendChild(row2);

            body.AppendChild(table);
            mainPart.Document.Save();
        }

        return filePath;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    [Fact]
    public void DocxToPdf_ConvertsToValidPdf_AndPreservesOriginalDocx()
    {
        var docxPath = CreateSampleDocx("QuarterlyReport.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetPdf = Path.Combine(_testDir, "QuarterlyReport.pdf");

        var options = new DocxPdfConversionOptions(PreferLocalWordEngine: false);
        var outPath = DocxToPdfConverter.Convert(docxPath, targetPdf, options);

        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));

        // Verify valid PDF structure using PDFsharp
        using (var pdfDoc = PdfReader.Open(outPath, PdfDocumentOpenMode.Import))
        {
            Assert.True(pdfDoc.PageCount >= 1);
        }

        // Verify original DOCX is completely unmodified
        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    [Fact]
    public async Task DocxToPptx_ConvertsToValidPresentation_AndPreservesOriginalDocx()
    {
        var docxPath = CreateSampleDocx("StrategicPlan.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetPptx = Path.Combine(_testDir, "StrategicPlan.pptx");

        var options = new DocxPptxConversionOptions(
            AspectRatio: SlideAspectRatio.Widescreen16x9,
            GenerateTitleSlide: true);

        var outPath = await DocxToPptxConverter.ConvertAsync(docxPath, targetPptx, options);

        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));

        // Verify valid OpenXML PPTX
        using (var pptx = PresentationDocument.Open(outPath, false))
        {
            Assert.NotNull(pptx.PresentationPart);
            Assert.NotNull(pptx.PresentationPart.Presentation);
            Assert.True(pptx.PresentationPart.SlideParts.Any());
        }

        // Verify original DOCX is completely unmodified
        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    [Fact]
    public void DocxCompress_RecompressesOpenXml_AndReportsHonestMetrics()
    {
        var docxPath = CreateSampleDocx("DocToCompress.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetDocx = Path.Combine(_testDir, "DocCompressed.docx");

        var report = DocxCompressor.Compress(docxPath, targetDocx);

        Assert.NotNull(report.OutputPath);
        Assert.True(File.Exists(report.OutputPath));
        Assert.True(report.OriginalSizeBytes > 0);
        Assert.True(report.OutputSizeBytes > 0);

        // Output must remain a valid OpenXML package
        using (var doc = WordprocessingDocument.Open(report.OutputPath, false))
        {
            Assert.NotNull(doc.MainDocumentPart);
            Assert.NotNull(doc.MainDocumentPart.Document);
        }

        // Original DOCX strictly unchanged
        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }

    [Fact]
    public async Task Processors_ExecuteSuccessfullyViaCatalogRequests()
    {
        var docxPath = CreateSampleDocx("CatalogDoc.docx");
        var classified = _classifier.Classify(docxPath);

        // 1. DocxPdfProcessor
        var pdfProcessor = new DocxPdfProcessor(_outputAllocator, _classifier);
        var pdfReq = new ProcessingRequest(classified, ActionIds.ConvertDocxToPdf, new ProcessingOptions(new Dictionary<string, string>()), _testDir);
        var pdfRes = await pdfProcessor.ProcessAsync(pdfReq, new Progress<ProcessingProgress>(), CancellationToken.None);
        Assert.True(pdfRes.Succeeded);
        Assert.NotNull(pdfRes.OutputPath);

        // 2. DocxPptxProcessor
        var pptxProcessor = new DocxPptxProcessor(_outputAllocator, _classifier);
        var pptxReq = new ProcessingRequest(classified, ActionIds.ConvertDocxToPptx, new ProcessingOptions(new Dictionary<string, string>()), _testDir);
        var pptxRes = await pptxProcessor.ProcessAsync(pptxReq, new Progress<ProcessingProgress>(), CancellationToken.None);
        Assert.True(pptxRes.Succeeded);
        Assert.NotNull(pptxRes.OutputPath);

        // 3. DocxCompressProcessor
        var compProcessor = new DocxCompressProcessor(_outputAllocator, _classifier);
        var compReq = new ProcessingRequest(classified, ActionIds.CompressDocx, new ProcessingOptions(new Dictionary<string, string>()), _testDir);
        var compRes = await compProcessor.ProcessAsync(compReq, new Progress<ProcessingProgress>(), CancellationToken.None);
        Assert.True(compRes.Succeeded);
        Assert.NotNull(compRes.OutputPath);
    }

    [Fact]
    public void DocxToPdf_Cancellation_CleansUpPartialsAndLeavesInputsUntouched()
    {
        var docxPath = CreateSampleDocx("CancelDoc.docx");
        var originalHash = ComputeSha256(docxPath);
        var targetPdf = Path.Combine(_testDir, "CancelledDoc.pdf");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        Assert.Throws<OperationCanceledException>(() =>
            DocxToPdfConverter.Convert(docxPath, targetPdf, new DocxPdfConversionOptions(), null, cts.Token));

        var partials = Directory.GetFiles(_testDir, "*.partial");
        Assert.Empty(partials);
        Assert.Equal(originalHash, ComputeSha256(docxPath));
    }
}
