using SmartShift.Core.Actions;
using SmartShift.Core.Capabilities;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Conversion;

public sealed class UnifiedConversionService : IUnifiedConversionService
{
    private readonly IFileCapabilityRegistry _capabilityRegistry;
    private readonly ProcessorCatalog _processorCatalog;
    private readonly IOutputPathAllocator _outputAllocator;
    private readonly FileClassifier _classifier;

    public UnifiedConversionService(
        IFileCapabilityRegistry capabilityRegistry,
        ProcessorCatalog processorCatalog,
        IOutputPathAllocator outputAllocator,
        FileClassifier classifier)
    {
        _capabilityRegistry = capabilityRegistry;
        _processorCatalog = processorCatalog;
        _outputAllocator = outputAllocator;
        _classifier = classifier;
    }

    public IReadOnlyList<DetectedFormat> GetSupportedTargets(DetectedFormat sourceFormat)
    {
        var registeredTargets = _capabilityRegistry.GetSupportedOutputFormats(sourceFormat);
        var result = new List<DetectedFormat>();

        foreach (var target in registeredTargets)
        {
            if (target != sourceFormat && CanConvertInternal(sourceFormat, target))
            {
                result.Add(target);
            }
        }

        return result;
    }

    public bool CanConvert(DetectedFormat sourceFormat, DetectedFormat targetFormat)
    {
        if (sourceFormat == targetFormat)
        {
            return false;
        }

        return CanConvertInternal(sourceFormat, targetFormat);
    }

    private static bool CanConvertInternal(DetectedFormat source, DetectedFormat target)
    {
        if (source is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
        {
            return target is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp;
        }

        if (source == DetectedFormat.Pdf)
        {
            return target is DetectedFormat.Docx
                or DetectedFormat.Pptx
                or DetectedFormat.Txt
                or DetectedFormat.Png
                or DetectedFormat.Jpeg;
        }

        if (source == DetectedFormat.Docx)
        {
            return target is DetectedFormat.Pdf or DetectedFormat.Pptx;
        }

        return false;
    }

    public ConversionRoute ResolveRoute(ClassifiedFile source, DetectedFormat targetFormat, IReadOnlyDictionary<string, string>? userOptions = null)
    {
        if (!CanConvert(source.Format, targetFormat))
        {
            throw new NotSupportedException($"Conversion from {source.Format} to {targetFormat} is not supported.");
        }

        if (!File.Exists(source.Path))
        {
            throw new FileNotFoundException("Source file does not exist or is inaccessible.", source.Path);
        }

        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (userOptions != null)
        {
            foreach (var kvp in userOptions)
            {
                opts[kvp.Key] = kvp.Value;
            }
        }

        // 1. Image Sources: PNG, JPEG, WebP
        if (source.Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
        {
            var quality = opts.TryGetValue("quality", out var q) && !string.IsNullOrWhiteSpace(q) ? q.Trim() : "85";

            return targetFormat switch
            {
                DetectedFormat.Webp => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertToWebp,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["quality"] = quality },
                    "WebP Image",
                    "Convert image to modern WebP format with alpha preservation."),

                DetectedFormat.Jpeg => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertToJpeg,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["quality"] = quality },
                    "JPEG Image",
                    "Convert image to standard JPEG format."),

                DetectedFormat.Png => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertToPng,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["quality"] = quality },
                    "PNG Image",
                    "Convert image to lossless PNG format with alpha preservation."),

                _ => throw new NotSupportedException($"Target format {targetFormat} is not supported for image conversion.")
            };
        }

        // 2. PDF Source
        if (source.Format == DetectedFormat.Pdf)
        {
            var pageRange = opts.TryGetValue("pageRange", out var pr) && !string.IsNullOrWhiteSpace(pr) ? pr.Trim() : "all";

            return targetFormat switch
            {
                DetectedFormat.Docx => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertPdfToDocx,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pageRange"] = pageRange },
                    "Word Document (.docx)",
                    "Convert PDF into an editable Word document with preserved paragraphs and fonts."),

                DetectedFormat.Pptx => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertPdfToPptx,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["pageRange"] = pageRange,
                        ["slideMode"] = opts.TryGetValue("slideMode", out var sm) && !string.IsNullOrWhiteSpace(sm) ? sm.Trim() : "pageAsImage"
                    },
                    "PowerPoint Presentation (.pptx)",
                    "Convert PDF pages into PowerPoint presentation slides."),

                DetectedFormat.Txt => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ExtractPdfText,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pageRange"] = pageRange },
                    "Plain Text (.txt)",
                    "Extract document text stream to clean UTF-8 plain text."),

                DetectedFormat.Png => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ExportPdfPages,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["format"] = "png",
                        ["dpi"] = opts.TryGetValue("dpi", out var dpi) && !string.IsNullOrWhiteSpace(dpi) ? dpi.Trim() : "150",
                        ["pageRange"] = pageRange
                    },
                    "PNG Images",
                    "Export PDF pages as high-resolution PNG image files."),

                DetectedFormat.Jpeg => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ExportPdfPages,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["format"] = "jpeg",
                        ["dpi"] = opts.TryGetValue("dpi", out var dpi) && !string.IsNullOrWhiteSpace(dpi) ? dpi.Trim() : "150",
                        ["quality"] = opts.TryGetValue("quality", out var q) && !string.IsNullOrWhiteSpace(q) ? q.Trim() : "85",
                        ["pageRange"] = pageRange
                    },
                    "JPEG Images",
                    "Export PDF pages as standard JPEG image files."),

                _ => throw new NotSupportedException($"Target format {targetFormat} is not supported for PDF conversion.")
            };
        }

        // 3. Word Document (DOCX) Source
        if (source.Format == DetectedFormat.Docx)
        {
            return targetFormat switch
            {
                DetectedFormat.Pdf => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertDocxToPdf,
                    opts,
                    "PDF Document (.pdf)",
                    "Convert Word document into a high-fidelity PDF, preserving paragraph structure, headings, bullet lists, and tables."),

                DetectedFormat.Pptx => new ConversionRoute(
                    source.Format,
                    targetFormat,
                    ActionIds.ConvertDocxToPptx,
                    opts,
                    "PowerPoint Presentation (.pptx)",
                    "Extract document headings and paragraphs to create an editable PowerPoint presentation deck."),

                _ => throw new NotSupportedException($"Target format {targetFormat} is not supported for Word document conversion.")
            };
        }

        throw new NotSupportedException($"Conversion from {source.Format} is not supported.");
    }

    public async Task<ProcessingResult> ConvertAsync(
        ClassifiedFile source,
        DetectedFormat targetFormat,
        IReadOnlyDictionary<string, string> userOptions,
        string outputDirectory,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessingResult.Cancelled();
        }

        if (!File.Exists(source.Path))
        {
            return ProcessingResult.Fail("The source file does not exist or has been moved.");
        }

        // Revalidate source classification to ensure file on disk matches expected format
        var freshClassified = _classifier.Classify(source.Path);
        if (freshClassified.Format != source.Format || !freshClassified.IsRecognized)
        {
            return ProcessingResult.Fail($"The file content format ({freshClassified.Format}) does not match the expected source format ({source.Format}).");
        }

        if (!CanConvert(freshClassified.Format, targetFormat))
        {
            return ProcessingResult.Fail($"Conversion from {freshClassified.Format} to {targetFormat} is not supported.");
        }

        var originalHash = FileHasher.Sha256Hex(source.Path);

        ConversionRoute route;
        try
        {
            route = ResolveRoute(freshClassified, targetFormat, userOptions);
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail($"Failed to resolve conversion route: {ex.Message}");
        }

        var validationErrors = ActionOptionValidator.Validate(route.ActionId, route.NormalizedOptions);
        if (validationErrors.Count > 0)
        {
            return ProcessingResult.Fail(string.Join(" ", validationErrors), validationErrors);
        }

        var processor = _processorCatalog.Find(route.ActionId);
        if (processor is null)
        {
            return ProcessingResult.Fail($"No registered processor found for action '{route.ActionId}'.");
        }

        var request = new ProcessingRequest(
            freshClassified,
            route.ActionId,
            new ProcessingOptions(route.NormalizedOptions),
            outputDirectory);

        try
        {
            var result = await processor.ProcessAsync(request, progress, cancellationToken).ConfigureAwait(false);

            // Re-verify strict source immutability
            var afterHash = FileHasher.Sha256Hex(source.Path);
            if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
            {
                return ProcessingResult.Fail("Source file integrity violation: source was modified during conversion.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ProcessingResult.Fail($"Conversion error: {ex.Message}");
        }
    }
}
