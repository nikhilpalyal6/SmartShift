using System.Text.RegularExpressions;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;

namespace SmartShift.Processing.PresentationStudio.Outline;

public sealed class DeterministicOutlineGenerator : IOutlineGenerator
{
    private static readonly Regex SentenceSplitter = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    public PresentationOutline GenerateOutline(ExtractedDocumentContent content, OutlineGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new OutlineGenerationOptions();

        var fileName = Path.GetFileName(content.SourceFilePath);
        var outline = new PresentationOutline
        {
            PresentationTitle = !string.IsNullOrWhiteSpace(content.DocumentTitle) ? content.DocumentTitle : (string.IsNullOrWhiteSpace(fileName) ? "Presentation" : Path.GetFileNameWithoutExtension(fileName)),
            SourceFilePath = content.SourceFilePath
        };

        // 1. Optional Title Slide
        if (options.GenerateTitleSlide)
        {
            var titleDraft = new OutlineSlideDraft
            {
                Title = outline.PresentationTitle,
                ParagraphText = !string.IsNullOrWhiteSpace(fileName) ? $"Generated from {fileName}" : "Presentation Overview",
                SuggestedLayout = PresentationLayoutType.Title,
                SourceProvenance = !string.IsNullOrWhiteSpace(fileName) ? $"Source: {fileName}" : null
            };
            outline.Slides.Add(titleDraft);
        }

        // If no sections exist (e.g. empty or scanned document), return outline with title slide or blank slide
        if (content.Sections.Count == 0)
        {
            if (outline.Slides.Count == 0)
            {
                outline.Slides.Add(new OutlineSlideDraft
                {
                    Title = outline.PresentationTitle,
                    SuggestedLayout = PresentationLayoutType.TitleAndContent,
                    SourceProvenance = !string.IsNullOrWhiteSpace(fileName) ? $"Source: {fileName}" : null
                });
            }
            return outline;
        }

        // 2. Process each section deterministically
        int sectionIndex = 1;
        foreach (var section in content.Sections)
        {
            var provenance = BuildProvenance(fileName, section.SourcePageNumber);
            string baseTitle = options.UseHeadingsAsSlideTitles && !string.IsNullOrWhiteSpace(section.Heading)
                ? section.Heading
                : $"Topic {sectionIndex}";

            // Collect all items from the section's blocks
            var items = new List<string>();
            foreach (var block in section.Blocks)
            {
                if (block.Kind is ContentBlockKind.BulletItem or ContentBlockKind.NumberedItem)
                {
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        items.Add(block.Text);
                    }
                }
                else if (block.Kind is ContentBlockKind.Paragraph or ContentBlockKind.CodeBlock)
                {
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        // Check if paragraph is very long: split cleanly into sentences
                        int wordCount = CountWords(block.Text);
                        if (wordCount > options.MaxWordsPerSlide)
                        {
                            var sentences = SentenceSplitter.Split(block.Text)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();

                            if (sentences.Count > 1)
                            {
                                items.AddRange(sentences);
                            }
                            else
                            {
                                items.Add(block.Text);
                            }
                        }
                        else
                        {
                            items.Add(block.Text);
                        }
                    }
                }
            }

            if (items.Count == 0)
            {
                // Section only had a heading
                if (!string.IsNullOrWhiteSpace(section.Heading))
                {
                    outline.Slides.Add(new OutlineSlideDraft
                    {
                        Title = section.Heading,
                        SuggestedLayout = PresentationLayoutType.SectionDivider,
                        SourceProvenance = provenance,
                        SourcePageNumber = section.SourcePageNumber
                    });
                }
                sectionIndex++;
                continue;
            }

            // Chunk items according to max bullets per slide
            int maxPerSlide = Math.Max(2, options.MaxBulletsPerSlide);
            int totalChunks = (int)Math.Ceiling(items.Count / (double)maxPerSlide);

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                var chunk = items.Skip(chunkIndex * maxPerSlide).Take(maxPerSlide).ToList();
                string slideTitle = totalChunks > 1
                    ? $"{baseTitle} (Part {chunkIndex + 1})"
                    : baseTitle;

                outline.Slides.Add(new OutlineSlideDraft
                {
                    Title = slideTitle,
                    BulletPoints = chunk,
                    SuggestedLayout = PresentationLayoutType.TitleAndContent,
                    SourceProvenance = provenance,
                    SourcePageNumber = section.SourcePageNumber
                });
            }

            sectionIndex++;
        }

        // Guarantee at least one slide
        if (outline.Slides.Count == 0)
        {
            outline.Slides.Add(new OutlineSlideDraft
            {
                Title = outline.PresentationTitle,
                SuggestedLayout = PresentationLayoutType.TitleAndContent,
                SourceProvenance = !string.IsNullOrWhiteSpace(fileName) ? $"Source: {fileName}" : null
            });
        }

        return outline;
    }

    private static string? BuildProvenance(string? fileName, int? pageNumber)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return pageNumber.HasValue ? $"Source: {fileName}, Page {pageNumber}" : $"Source: {fileName}";
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }
}

