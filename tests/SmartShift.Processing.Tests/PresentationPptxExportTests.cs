using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using SmartShift.Processing.PresentationStudio.Export;
using SmartShift.Processing.PresentationStudio.Models;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;

namespace SmartShift.Processing.Tests;

public sealed class PresentationPptxExportTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "smartshift-pptx-test-" + Guid.NewGuid().ToString("N"));

    public PresentationPptxExportTests()
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

    private string CreateTestImage(string filename, int width = 400, int height = 300)
    {
        var path = Path.Combine(_testDir, filename);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.DodgerBlue);
        using var fs = File.Create(path);
        bitmap.Encode(fs, SKEncodedImageFormat.Png, 90);
        return path;
    }

    [Fact]
    public async Task PresentationPptxExporter_GeneratesValidPptxWithEditableText()
    {
        var deck = new PresentationDeck
        {
            Title = "Product Roadmap 2026",
            Theme = PresentationTheme.DarkSlate,
            Dimensions = SlideDimensions.Widescreen16x9
        };

        // Slide 1: Title
        deck.Slides.Add(new PresentationSlide
        {
            Title = "Product Roadmap 2026",
            Layout = PresentationLayoutType.Title,
            PrimaryParagraph = "Engineering & Design Update"
        });

        // Slide 2: Title and Content with bullets
        var slide2 = new PresentationSlide
        {
            Title = "Strategic Initiatives",
            Layout = PresentationLayoutType.TitleAndContent,
            PrimaryBullets =
            [
                "Zero cloud uploads architecture",
                "Sub-50ms native responsiveness",
                "Layered file integrity verification"
            ],
            SourceProvenance = "Source: Strategy.md, Page 1"
        };
        deck.Slides.Add(slide2);

        // Slide 3: Two Column
        var slide3 = new PresentationSlide
        {
            Title = "Quarterly Split",
            Layout = PresentationLayoutType.TwoColumn,
            PrimaryBullets = ["Q1: Local Engine", "Q2: Performance Tuning"],
            SecondaryBullets = ["Q3: Presentation Studio", "Q4: Desktop Integration"]
        };
        deck.Slides.Add(slide3);

        // Slide 4: Text with Image
        var imgPath = CreateTestImage("chart.png");
        var slide4 = new PresentationSlide
        {
            Title = "Performance Growth",
            Layout = PresentationLayoutType.TextWithImage,
            PrimaryBullets = ["Memory footprint reduced by 40%", "Rendering speeds improved 3x"],
            Image = new SlideImageReference
            {
                ImagePath = imgPath,
                NaturalWidth = 400,
                NaturalHeight = 300
            }
        };
        deck.Slides.Add(slide4);

        // Slide 5: Section Divider
        deck.Slides.Add(new PresentationSlide
        {
            Title = "Next Steps",
            Layout = PresentationLayoutType.SectionDivider,
            PrimaryParagraph = "Deployment checklist and Q&A"
        });

        // Slide 6: Blank
        deck.Slides.Add(new PresentationSlide
        {
            Title = "Blank Appendix",
            Layout = PresentationLayoutType.Blank
        });

        var exporter = new PresentationPptxExporter();
        var outputPath = Path.Combine(_testDir, "roadmap.pptx");

        var resultPath = await exporter.ExportAsync(deck, outputPath, null, CancellationToken.None);

        Assert.True(File.Exists(resultPath));
        Assert.Equal(outputPath, resultPath);

        // Verify with OpenXmlValidator
        using (var package = PresentationDocument.Open(resultPath, false))
        {
            var validator = new OpenXmlValidator();
            var validationErrors = validator.Validate(package).ToList();
            Assert.Empty(validationErrors);

            var presPart = package.PresentationPart;
            Assert.NotNull(presPart);
            var slides = presPart.SlideParts.ToList();
            Assert.Equal(6, slides.Count);

            // Verify Slide 1: Has title text element
            var s1 = slides[0].Slide;
            Assert.NotNull(s1);
            var s1Texts = s1.Descendants<A.Text>().Select(t => t.Text).ToList();
            Assert.Contains("Product Roadmap 2026", s1Texts);
            Assert.Contains("Engineering & Design Update", s1Texts);

            // Verify Slide 2: Has bullets as editable text elements
            var s2 = slides[1].Slide;
            Assert.NotNull(s2);
            var s2Texts = s2.Descendants<A.Text>().Select(t => t.Text).ToList();
            Assert.Contains("Strategic Initiatives", s2Texts);
            Assert.Contains("Zero cloud uploads architecture", s2Texts);
            Assert.Contains("Sub-50ms native responsiveness", s2Texts);

            // Verify Slide 4: Has genuine ImagePart and Picture shape
            Assert.Single(slides[3].ImageParts);
            var s4 = slides[3].Slide;
            Assert.NotNull(s4);
            var picture = s4.Descendants<Picture>().FirstOrDefault();
            Assert.NotNull(picture);
        }
    }

    [Fact]
    public async Task PresentationPptxExporter_SupportsStandard4x3AndThemes()
    {
        var deck = new PresentationDeck
        {
            Title = "Standard 4:3 Deck",
            Theme = PresentationTheme.ModernMinimal,
            Dimensions = SlideDimensions.Standard4x3
        };
        deck.Slides.Add(new PresentationSlide
        {
            Title = "Modern Minimal Title",
            Layout = PresentationLayoutType.Title
        });

        var exporter = new PresentationPptxExporter();
        var outputPath = Path.Combine(_testDir, "standard_4x3.pptx");

        await exporter.ExportAsync(deck, outputPath, null, CancellationToken.None);

        using var package = PresentationDocument.Open(outputPath, false);
        var pres = package.PresentationPart?.Presentation;
        Assert.NotNull(pres);
        var slideSize = pres.SlideSize;
        Assert.NotNull(slideSize);
        Assert.NotNull(slideSize.Cx);
        Assert.NotNull(slideSize.Cy);
        Assert.NotNull(slideSize.Type);
        Assert.Equal(SlideDimensions.Standard4x3.WidthEmu, (long)slideSize.Cx.Value);
        Assert.Equal(SlideDimensions.Standard4x3.HeightEmu, (long)slideSize.Cy.Value);
        Assert.Equal(SlideSizeValues.Screen4x3, slideSize.Type.Value);
    }

    [Fact]
    public async Task PresentationPptxExporter_PreservesOriginalFilesAndRollsBackOnFailure()
    {
        var deck = new PresentationDeck
        {
            Title = "Fail Test",
            Slides = [new PresentationSlide { Title = "Slide 1", Layout = PresentationLayoutType.Title }]
        };

        var exporter = new PresentationPptxExporter();
        // Invalid path to directory that cannot be created
        var invalidPath = Path.Combine("Z:\\NonExistentDrive789\\output.pptx");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            exporter.ExportAsync(deck, invalidPath, null, CancellationToken.None));

        // Confirm partial file is not left behind
        Assert.False(File.Exists(invalidPath + ".partial"));
    }
}
