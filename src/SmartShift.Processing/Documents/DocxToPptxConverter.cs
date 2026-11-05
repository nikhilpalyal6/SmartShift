using DocumentFormat.OpenXml.Packaging;
using SmartShift.Processing;
using SmartShift.Processing.PresentationStudio.Export;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;

namespace SmartShift.Processing.Documents;

public sealed record DocxPptxConversionOptions(
    PresentationTheme? Theme = null,
    SlideAspectRatio AspectRatio = SlideAspectRatio.Widescreen16x9,
    bool GenerateTitleSlide = true,
    SlideContentDensity Density = SlideContentDensity.Standard);

public static class DocxToPptxConverter
{
    public static async Task<string> ConvertAsync(
        string sourceDocxPath,
        string outputPath,
        DocxPptxConversionOptions? options = null,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceDocxPath) || !File.Exists(sourceDocxPath))
        {
            throw new FileNotFoundException("Source Word document does not exist.", sourceDocxPath);
        }

        var sourceInfo = new FileInfo(sourceDocxPath);
        if (sourceInfo.Length == 0)
        {
            throw new InvalidOperationException("Source Word document is empty (0 bytes).");
        }

        options ??= new DocxPptxConversionOptions();

        progress?.Report(new ProcessingProgress(15, "Importing Word document hierarchy and sections…"));
        var importer = new DocxImporter();
        var extracted = await importer.ImportAsync(sourceDocxPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProcessingProgress(45, "Generating deterministic presentation outline…"));
        var outlineGen = new DeterministicOutlineGenerator();
        var outlineOptions = new OutlineGenerationOptions
        {
            GenerateTitleSlide = options.GenerateTitleSlide,
            ContentDensity = options.Density,
            UseHeadingsAsSlideTitles = true
        };
        var outline = outlineGen.GenerateOutline(extracted, outlineOptions);

        progress?.Report(new ProcessingProgress(65, "Composing PowerPoint presentation deck…"));
        var theme = options.Theme ?? PresentationTheme.DarkSlate;
        var deck = PresentationDeck.FromOutline(outline, theme);
        deck.Dimensions = options.AspectRatio == SlideAspectRatio.Standard4x3
            ? SlideDimensions.Standard4x3
            : SlideDimensions.Widescreen16x9;

        progress?.Report(new ProcessingProgress(80, "Exporting editable PowerPoint presentation…"));
        var exporter = new PresentationPptxExporter();
        var finalFile = await exporter.ExportAsync(deck, outputPath, progress, cancellationToken).ConfigureAwait(false);

        // Validate the generated PPTX package
        progress?.Report(new ProcessingProgress(95, "Validating PowerPoint package structure…"));
        using (var pptx = PresentationDocument.Open(finalFile, false))
        {
            if (pptx.PresentationPart?.Presentation?.SlideIdList is null ||
                !pptx.PresentationPart.Presentation.SlideIdList.ChildElements.Any())
            {
                throw new InvalidOperationException("Generated PPTX contains no presentation slides.");
            }
        }

        progress?.Report(new ProcessingProgress(100, "DOCX to PPTX conversion completed."));
        return finalFile;
    }
}
