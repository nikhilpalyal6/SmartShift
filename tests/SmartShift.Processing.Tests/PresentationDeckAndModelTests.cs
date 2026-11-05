using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PresentationDeckAndModelTests
{
    [Fact]
    public void PresentationDeck_CreateDefault_HasTitleSlide()
    {
        var deck = PresentationDeck.CreateDefault("My Q3 Kickoff");
        Assert.Equal("My Q3 Kickoff", deck.Title);
        Assert.Single(deck.Slides);
        Assert.Equal(PresentationLayoutType.Title, deck.Slides[0].Layout);
        Assert.Equal("My Q3 Kickoff", deck.Slides[0].Title);
        Assert.False(deck.Slides[0].IsEmpty);
    }

    [Fact]
    public void PresentationDeck_FromOutline_ConvertsCorrectly()
    {
        var outline = new PresentationOutline
        {
            PresentationTitle = "Executive Deck",
            SourceFilePath = "C:\\test\\notes.txt",
            Slides =
            [
                new OutlineSlideDraft
                {
                    Title = "Executive Deck",
                    SuggestedLayout = PresentationLayoutType.Title,
                    ParagraphText = "Quarterly update"
                },
                new OutlineSlideDraft
                {
                    Title = "Key Metrics",
                    SuggestedLayout = PresentationLayoutType.TitleAndContent,
                    BulletPoints = ["Revenue: $4.2M", "Growth: 22%"],
                    SourceProvenance = "Source: notes.txt, Page 1",
                    SourcePageNumber = 1
                }
            ]
        };

        var deck = PresentationDeck.FromOutline(outline, PresentationTheme.ClassicNavy);

        Assert.Equal("Executive Deck", deck.Title);
        Assert.Equal("Classic Navy", deck.Theme.Name);
        Assert.Equal(2, deck.Slides.Count);

        var metricsSlide = deck.Slides[1];
        Assert.Equal("Key Metrics", metricsSlide.Title);
        Assert.Equal(PresentationLayoutType.TitleAndContent, metricsSlide.Layout);
        Assert.Equal(2, metricsSlide.PrimaryBullets.Count);
        Assert.Equal("Revenue: $4.2M", metricsSlide.PrimaryBullets[0]);
        Assert.Equal("Source: notes.txt, Page 1", metricsSlide.SourceProvenance);
        Assert.Equal(1, metricsSlide.SourcePageNumber);
    }

    [Fact]
    public void SlideOperations_AddDuplicateMoveDelete_MaintainsIntegrity()
    {
        var deck = PresentationDeck.CreateDefault("Deck Ops");

        // Add slide
        var slide2 = deck.AddSlide(PresentationLayoutType.TitleAndContent, "Slide Two");
        slide2.PrimaryBullets = ["Item 1", "Item 2"];
        Assert.Equal(2, deck.Slides.Count);

        // Duplicate slide
        var duplicate = deck.DuplicateSlide(slide2.Id);
        Assert.NotNull(duplicate);
        Assert.Equal(3, deck.Slides.Count);
        Assert.Equal("Slide Two (Copy)", duplicate.Title);
        Assert.Equal(2, duplicate.PrimaryBullets.Count);

        // Move slide up
        Assert.True(deck.MoveSlideUp(duplicate.Id));
        Assert.Equal(duplicate.Id, deck.Slides[1].Id);

        // Move slide down
        Assert.True(deck.MoveSlideDown(duplicate.Id));
        Assert.Equal(duplicate.Id, deck.Slides[2].Id);

        // Delete slide
        Assert.True(deck.DeleteSlide(duplicate.Id));
        Assert.Equal(2, deck.Slides.Count);

        // Delete all slides: last slide deletion must preserve at least one clean slide
        deck.DeleteSlide(slide2.Id);
        Assert.Single(deck.Slides);
        deck.DeleteSlide(deck.Slides[0].Id);
        Assert.Single(deck.Slides); // Still 1 slide preserved
    }

    [Fact]
    public void SplitAndMergeOperations_HandleBulletsCorrectly()
    {
        var deck = PresentationDeck.CreateDefault("Split Test");
        var slide = deck.AddSlide(PresentationLayoutType.TitleAndContent, "Long List");
        slide.PrimaryBullets = ["Point 1", "Point 2", "Point 3", "Point 4"];

        // Split slide
        var secondHalf = deck.SplitSlide(slide.Id);
        Assert.NotNull(secondHalf);
        Assert.Equal(3, deck.Slides.Count); // initial title + original + split
        Assert.Equal(2, slide.PrimaryBullets.Count);
        Assert.Equal("Point 1", slide.PrimaryBullets[0]);
        Assert.Equal("Point 2", slide.PrimaryBullets[1]);
        Assert.Equal(2, secondHalf.PrimaryBullets.Count);
        Assert.Equal("Point 3", secondHalf.PrimaryBullets[0]);
        Assert.Equal("Point 4", secondHalf.PrimaryBullets[1]);

        // Merge back
        Assert.True(deck.MergeWithNext(slide.Id));
        Assert.Equal(2, deck.Slides.Count);
        Assert.Equal(4, slide.PrimaryBullets.Count);
        Assert.Equal("Point 4", slide.PrimaryBullets[3]);
    }

    [Fact]
    public void TextOverflowHeuristics_WarnsOnDenseSlides()
    {
        var slide = new PresentationSlide
        {
            Title = "Dense Slide",
            Layout = PresentationLayoutType.TitleAndContent,
            PrimaryBullets = Enumerable.Range(1, 12).Select(i => $"Detailed analytical finding number {i} with extensive explanations").ToList()
        };

        Assert.True(slide.HasLikelyOverflow(out var warning));
        Assert.Contains("dense", warning);
        Assert.Contains("splitting", warning);

        // Moderate slide should not trigger overflow
        var normalSlide = new PresentationSlide
        {
            Title = "Normal Slide",
            Layout = PresentationLayoutType.TitleAndContent,
            PrimaryBullets = ["First point", "Second point", "Third point"]
        };
        Assert.False(normalSlide.HasLikelyOverflow(out _));
    }

    [Fact]
    public void PresentationDeck_Validation_DetectsMissingImagesAndEmptyTitles()
    {
        var deck = new PresentationDeck { Title = "" };
        var slide = deck.AddSlide();
        slide.Image = new SlideImageReference
        {
            ImagePath = "C:\\nonexistent_image_12345.png"
        };

        var errors = deck.Validate();
        Assert.Contains(errors, e => e.Contains("title cannot be empty"));
        Assert.Contains(errors, e => e.Contains("references an image file that does not exist"));
    }
}
