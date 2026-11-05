using SmartShift.Processing.PresentationStudio.Outline;

namespace SmartShift.Processing.PresentationStudio.Models;

public sealed class PresentationDeck
{
    public string Title { get; set; } = "Untitled Presentation";
    public string Author { get; set; } = "SmartShift";
    public string? SourceFilePath { get; set; }
    public SlideDimensions Dimensions { get; set; } = SlideDimensions.Widescreen16x9;
    public PresentationTheme Theme { get; set; } = PresentationTheme.DarkSlate;
    public List<PresentationSlide> Slides { get; set; } = [];

    public PresentationDeck()
    {
    }

    public static PresentationDeck CreateDefault(string title = "New Presentation")
    {
        var doc = new PresentationDeck
        {
            Title = title,
            Theme = PresentationTheme.DarkSlate,
            Dimensions = SlideDimensions.Widescreen16x9
        };
        doc.Slides.Add(new PresentationSlide
        {
            Title = title,
            Layout = PresentationLayoutType.Title,
            PrimaryParagraph = "Subtitle or presentation overview"
        });
        return doc;
    }

    public static PresentationDeck FromOutline(PresentationOutline outline, PresentationTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(outline);

        var doc = new PresentationDeck
        {
            Title = !string.IsNullOrWhiteSpace(outline.PresentationTitle) ? outline.PresentationTitle : "Presentation",
            SourceFilePath = outline.SourceFilePath,
            Theme = theme ?? PresentationTheme.DarkSlate,
            Dimensions = SlideDimensions.Widescreen16x9
        };

        foreach (var draft in outline.Slides)
        {
            var slide = new PresentationSlide
            {
                Id = draft.Id,
                Title = draft.Title,
                Layout = draft.SuggestedLayout,
                SourceProvenance = draft.SourceProvenance,
                SourcePageNumber = draft.SourcePageNumber
            };

            if (draft.BulletPoints.Count > 0)
            {
                slide.PrimaryBullets = [.. draft.BulletPoints];
            }

            if (!string.IsNullOrWhiteSpace(draft.ParagraphText))
            {
                slide.PrimaryParagraph = draft.ParagraphText;
            }

            doc.Slides.Add(slide);
        }

        if (doc.Slides.Count == 0)
        {
            doc.Slides.Add(new PresentationSlide
            {
                Title = doc.Title,
                Layout = PresentationLayoutType.Title
            });
        }

        return doc;
    }

    public PresentationSlide AddSlide(PresentationLayoutType layout = PresentationLayoutType.TitleAndContent, string? title = null)
    {
        var slide = new PresentationSlide
        {
            Title = title ?? (layout == PresentationLayoutType.Title ? Title : $"Slide {Slides.Count + 1}"),
            Layout = layout
        };
        Slides.Add(slide);
        return slide;
    }

    public bool DeleteSlide(string slideId)
    {
        var slide = Slides.FirstOrDefault(s => s.Id == slideId);
        if (slide is null) return false;

        Slides.Remove(slide);

        // Deliberately handle last-slide deletion: never leave document completely devoid of slides
        if (Slides.Count == 0)
        {
            Slides.Add(new PresentationSlide
            {
                Title = Title,
                Layout = PresentationLayoutType.TitleAndContent
            });
        }

        return true;
    }

    public PresentationSlide? DuplicateSlide(string slideId)
    {
        int index = Slides.FindIndex(s => s.Id == slideId);
        if (index < 0) return null;

        var clone = Slides[index].Clone();
        clone.Title = $"{clone.Title} (Copy)";
        Slides.Insert(index + 1, clone);
        return clone;
    }

    public bool MoveSlide(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Slides.Count || newIndex < 0 || newIndex >= Slides.Count)
        {
            return false;
        }
        if (oldIndex == newIndex) return true;

        var item = Slides[oldIndex];
        Slides.RemoveAt(oldIndex);
        Slides.Insert(newIndex, item);
        return true;
    }

    public bool MoveSlideUp(string slideId)
    {
        int index = Slides.FindIndex(s => s.Id == slideId);
        if (index <= 0) return false;
        return MoveSlide(index, index - 1);
    }

    public bool MoveSlideDown(string slideId)
    {
        int index = Slides.FindIndex(s => s.Id == slideId);
        if (index < 0 || index >= Slides.Count - 1) return false;
        return MoveSlide(index, index + 1);
    }

    /// <summary>
    /// Splits an overlong slide's bullets across two separate slides
    /// </summary>
    public PresentationSlide? SplitSlide(string slideId)
    {
        int index = Slides.FindIndex(s => s.Id == slideId);
        if (index < 0) return null;

        var original = Slides[index];
        var bullets = original.PrimaryBullets;
        if (bullets.Count < 2) return null;

        int half = (int)Math.Ceiling(bullets.Count / 2.0);
        var firstHalf = bullets.Take(half).ToList();
        var secondHalf = bullets.Skip(half).ToList();

        original.PrimaryBullets = firstHalf;

        var newSlide = original.Clone();
        newSlide.Title = $"{original.Title} (Cont.)";
        newSlide.PrimaryBullets = secondHalf;

        Slides.Insert(index + 1, newSlide);
        return newSlide;
    }

    /// <summary>
    /// Merges compatible adjacent slides (merges bullets of next slide into selected slide)
    /// </summary>
    public bool MergeWithNext(string slideId)
    {
        int index = Slides.FindIndex(s => s.Id == slideId);
        if (index < 0 || index >= Slides.Count - 1) return false;

        var current = Slides[index];
        var next = Slides[index + 1];

        // Merge bullets
        var combinedBullets = new List<string>(current.PrimaryBullets);
        combinedBullets.AddRange(next.PrimaryBullets);
        current.PrimaryBullets = combinedBullets;

        // If next slide had an image and current didn't, preserve image
        if (current.Image is null && next.Image is not null)
        {
            current.Image = next.Image;
            if (current.Layout == PresentationLayoutType.TitleAndContent)
            {
                current.Layout = PresentationLayoutType.TextWithImage;
            }
        }

        Slides.RemoveAt(index + 1);
        return true;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Title))
        {
            errors.Add("Presentation title cannot be empty.");
        }

        if (Slides.Count == 0)
        {
            errors.Add("Presentation must contain at least one slide.");
        }

        foreach (var (slide, idx) in Slides.Select((s, i) => (s, i + 1)))
        {
            if (slide.Image is not null && !File.Exists(slide.Image.ImagePath))
            {
                errors.Add($"Slide {idx} ('{slide.Title}') references an image file that does not exist on disk: {slide.Image.ImagePath}");
            }
        }

        return errors;
    }
}

