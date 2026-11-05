using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PresentationImportAndOutlineTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "smartshift-pres-test-" + Guid.NewGuid().ToString("N"));

    public PresentationImportAndOutlineTests()
    {
        Directory.CreateDirectory(_testDir);
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

    [Fact]
    public async Task PlainTextImporter_ExtractsHeadingsAndBullets()
    {
        var filePath = Path.Combine(_testDir, "test_notes.txt");
        var content = """
            INTRODUCTION:
            * First key point for the project
            * Second key point with details
            • Third key point with bullet

            1. Numbered item alpha
            2. Numbered item beta

            SUMMARY
            Here is a paragraph describing the project roadmap.
            """;
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var importer = new PlainTextImporter();
        Assert.True(importer.CanImport(filePath));

        var extracted = await importer.ImportAsync(filePath, CancellationToken.None);

        Assert.True(extracted.DocumentTitle is "test_notes" or "INTRODUCTION");
        Assert.True(extracted.Sections.Count >= 2);
        Assert.True(extracted.TotalCharacters > 0);
        Assert.False(extracted.HasScannedContentOnly);

        var firstSection = extracted.Sections[0];
        Assert.Contains(firstSection.Blocks, b => b.Kind == ContentBlockKind.BulletItem && b.Text.Contains("First key point"));
        Assert.Contains(firstSection.Blocks, b => b.Kind == ContentBlockKind.NumberedItem && b.Text.Contains("Numbered item alpha"));
    }

    [Fact]
    public async Task PlainTextImporter_ThrowsOnEmptyFile()
    {
        var filePath = Path.Combine(_testDir, "empty.txt");
        await File.WriteAllBytesAsync(filePath, []);

        var importer = new PlainTextImporter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(filePath, CancellationToken.None));
    }

    [Fact]
    public async Task MarkdownImporter_ExtractsHeadingsListsAndSanitizesHtml()
    {
        var filePath = Path.Combine(_testDir, "architecture.md");
        var md = """
            # Cloud Migration Architecture

            Overview of the multi-tier migration.

            ## Key Principles
            - Zero downtime deployment
            - High availability across regions
            - Local-first telemetry

            ## Security Considerations
            <script>alert('xss');</script>
            1. End-to-end encryption
            2. Strict access control

            ![System Diagram](images/diagram.png)
            """;
        await File.WriteAllTextAsync(filePath, md, Encoding.UTF8);

        var importer = new MarkdownImporter();
        Assert.True(importer.CanImport(filePath));

        var extracted = await importer.ImportAsync(filePath, CancellationToken.None);

        Assert.Equal("Cloud Migration Architecture", extracted.DocumentTitle);
        Assert.True(extracted.Sections.Count >= 2);

        // Security check: HTML script tags must be stripped
        var allText = string.Join(" ", extracted.Sections.SelectMany(s => s.Blocks).Select(b => b.Text));
        Assert.DoesNotContain("<script>", allText);
        Assert.DoesNotContain("</script>", allText);

        // Check image reference was parsed
        var imgBlock = extracted.Sections.SelectMany(s => s.Blocks).FirstOrDefault(b => b.Kind == ContentBlockKind.ImageRef);
        Assert.NotNull(imgBlock);
        Assert.Equal("images/diagram.png", imgBlock.Text);
    }

    [Fact]
    public async Task DocxImporter_ExtractsParagraphsAndHeadings()
    {
        var filePath = Path.Combine(_testDir, "sample_document.docx");

        // Create a real docx with OpenXML
        using (var wordDoc = WordprocessingDocument.Create(filePath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            // Title
            var titlePara = new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Title" }),
                new Run(new Text("Project Q3 Report")));
            body.AppendChild(titlePara);

            // Heading 1
            var h1 = new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text("Financial Highlights")));
            body.AppendChild(h1);

            // Body Paragraph
            var p1 = new Paragraph(new Run(new Text("Revenue increased by 18 percent quarter-over-quarter.")));
            body.AppendChild(p1);

            // List item
            var listPara = new Paragraph(
                new ParagraphProperties(new NumberingProperties(new NumberingId { Val = 1 }, new NumberingLevelReference { Val = 0 })),
                new Run(new Text("Operating expenses decreased by 4 percent.")));
            body.AppendChild(listPara);

            mainPart.Document.Save();
        }

        var importer = new DocxImporter();
        Assert.True(importer.CanImport(filePath));

        var extracted = await importer.ImportAsync(filePath, CancellationToken.None);

        Assert.Equal("Project Q3 Report", extracted.DocumentTitle);
        Assert.True(extracted.Sections.Count >= 1);
        Assert.True(extracted.TotalCharacters > 0);
    }

    [Fact]
    public async Task PdfImporter_ExtractsPagesAndDetectsScannedPdf()
    {
        // 1. Create a PDF with text
        var textPdfPath = Path.Combine(_testDir, "report.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            var page = pdf.AddPage();
            var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 16, XFontStyleEx.Bold);
            gfx.DrawString("Executive Summary", font, XBrushes.Black, new XPoint(50, 50));
            gfx.DrawString("• Strategic milestone reached", font, XBrushes.Black, new XPoint(50, 90));
            pdf.Save(textPdfPath);
        }

        var importer = new PdfImporter();
        Assert.True(importer.CanImport(textPdfPath));

        var extracted = await importer.ImportAsync(textPdfPath, CancellationToken.None);

        Assert.False(extracted.HasScannedContentOnly);
        Assert.True(extracted.Sections.Count >= 1);
        Assert.True(extracted.TotalCharacters > 0);
        Assert.Equal(1, extracted.Sections[0].SourcePageNumber);

        // 2. Create an empty scanned-like PDF with no text
        var scannedPdfPath = Path.Combine(_testDir, "scanned.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            pdf.AddPage(); // Blank page, no text drawn
            pdf.Save(scannedPdfPath);
        }

        var scannedExtracted = await importer.ImportAsync(scannedPdfPath, CancellationToken.None);
        Assert.True(scannedExtracted.HasScannedContentOnly);
        Assert.NotNull(scannedExtracted.WarningMessage);
        Assert.Contains("OCR", scannedExtracted.WarningMessage);
    }

    [Fact]
    public void DocumentImporterCatalog_ValidatesSignaturesAndRejectsUnsupported()
    {
        var catalog = new DocumentImporterCatalog();

        // Unsupported extension
        var fakePath = Path.Combine(_testDir, "data.xlsx");
        File.WriteAllText(fakePath, "fake data");
        Assert.Throws<NotSupportedException>(() => catalog.GetImporter(fakePath));

        // Invalid PDF header signature
        var corruptPdf = Path.Combine(_testDir, "invalid.pdf");
        File.WriteAllText(corruptPdf, "Not A Real PDF Header");
        Assert.Throws<InvalidOperationException>(() => catalog.GetImporter(corruptPdf));

        // Binary null bytes in txt
        var binaryTxt = Path.Combine(_testDir, "binary.txt");
        File.WriteAllBytes(binaryTxt, [0x00, 0x01, 0x02, 0x00]);
        Assert.Throws<InvalidOperationException>(() => catalog.GetImporter(binaryTxt));
    }

    [Fact]
    public void DeterministicOutlineGenerator_GroupsByHeadingsAndPreservesWording()
    {
        var content = new ExtractedDocumentContent(
            SourceFilePath: "C:\\Docs\\Strategy.md",
            DocumentTitle: "2026 Strategy",
            Sections:
            [
                new ExtractedContentSection("Market Analysis", 1,
                [
                    new ExtractedContentBlock(ContentBlockKind.BulletItem, "Competitor A entered the enterprise tier"),
                    new ExtractedContentBlock(ContentBlockKind.BulletItem, "Consumer adoption grew 25% year over year"),
                    new ExtractedContentBlock(ContentBlockKind.BulletItem, "Supply chain costs stabilized globally")
                ], SourcePageNumber: 1),

                new ExtractedContentSection("Action Items", 1,
                [
                    new ExtractedContentBlock(ContentBlockKind.BulletItem, "Accelerate local feature rollout"),
                    new ExtractedContentBlock(ContentBlockKind.BulletItem, "Publish quarterly privacy report")
                ], SourcePageNumber: 2)
            ],
            TotalCharacters: 150);

        var generator = new DeterministicOutlineGenerator();
        var options = new OutlineGenerationOptions
        {
            GenerateTitleSlide = true,
            UseHeadingsAsSlideTitles = true,
            ContentDensity = SlideContentDensity.Standard
        };

        var outline1 = generator.GenerateOutline(content, options);
        var outline2 = generator.GenerateOutline(content, options);

        // Verification 1: Title slide exists
        Assert.Equal("2026 Strategy", outline1.PresentationTitle);
        Assert.Equal(PresentationLayoutType.Title, outline1.Slides[0].SuggestedLayout);

        // Verification 2: Sections become slides with matching titles
        Assert.Contains(outline1.Slides, s => s.Title == "Market Analysis");
        Assert.Contains(outline1.Slides, s => s.Title == "Action Items");

        // Verification 3: Source wording preserved verbatim
        var marketSlide = outline1.Slides.First(s => s.Title == "Market Analysis");
        Assert.Equal(3, marketSlide.BulletPoints.Count);
        Assert.Equal("Competitor A entered the enterprise tier", marketSlide.BulletPoints[0]);

        // Verification 4: Provenance tracked
        Assert.Equal("Source: Strategy.md, Page 1", marketSlide.SourceProvenance);

        // Verification 5: Determinism - exact identical output for identical inputs
        Assert.Equal(outline1.Slides.Count, outline2.Slides.Count);
        for (int i = 0; i < outline1.Slides.Count; i++)
        {
            Assert.Equal(outline1.Slides[i].Title, outline2.Slides[i].Title);
            Assert.Equal(outline1.Slides[i].BulletPoints, outline2.Slides[i].BulletPoints);
            Assert.Equal(outline1.Slides[i].SuggestedLayout, outline2.Slides[i].SuggestedLayout);
        }
    }

    [Fact]
    public void DeterministicOutlineGenerator_SplitsHeavySectionsAcrossSlides()
    {
        // 9 bullet points in one section
        var bullets = Enumerable.Range(1, 9)
            .Select(i => new ExtractedContentBlock(ContentBlockKind.BulletItem, $"Milestone step number {i}"))
            .ToList();

        var content = new ExtractedDocumentContent(
            SourceFilePath: "Heavy.txt",
            DocumentTitle: "Long Roadmap",
            Sections: [new ExtractedContentSection("Detailed Steps", 1, bullets)],
            TotalCharacters: 200);

        var generator = new DeterministicOutlineGenerator();
        var options = new OutlineGenerationOptions
        {
            GenerateTitleSlide = false,
            ContentDensity = SlideContentDensity.Compact // Max 3 per slide
        };

        var outline = generator.GenerateOutline(content, options);

        // 9 items / 3 per slide = 3 slides
        Assert.Equal(3, outline.Slides.Count);
        Assert.Equal("Detailed Steps (Part 1)", outline.Slides[0].Title);
        Assert.Equal(3, outline.Slides[0].BulletPoints.Count);
        Assert.Equal("Detailed Steps (Part 2)", outline.Slides[1].Title);
        Assert.Equal("Detailed Steps (Part 3)", outline.Slides[2].Title);
    }
}
