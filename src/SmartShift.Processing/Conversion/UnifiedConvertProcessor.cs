using SmartShift.Core.Actions;
using SmartShift.Core.Classification;

namespace SmartShift.Processing.Conversion;

/// <summary>
/// Universal processor for ActionIds.UnifiedConvert.
/// Delegates execution through the UnifiedConversionService.
/// </summary>
public sealed class UnifiedConvertProcessor : IFileProcessor
{
    private readonly IUnifiedConversionService _conversionService;

    public UnifiedConvertProcessor(IUnifiedConversionService conversionService)
    {
        _conversionService = conversionService;
    }

    public string ActionId => ActionIds.UnifiedConvert;

    public bool IsAvailable => true;

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessingResult.Cancelled();
        }

        if (!request.Options.Values.TryGetValue("targetFormat", out var rawTarget) || string.IsNullOrWhiteSpace(rawTarget))
        {
            return ProcessingResult.Fail("A target format must be specified for unified conversion.");
        }

        var targetFormat = ParseTargetFormat(rawTarget.Trim());
        if (targetFormat == DetectedFormat.Unknown)
        {
            return ProcessingResult.Fail($"Target format '{rawTarget}' is not recognized.");
        }

        return await _conversionService.ConvertAsync(
            request.Input,
            targetFormat,
            request.Options.Values,
            request.OutputDirectory,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static DetectedFormat ParseTargetFormat(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "webp" => DetectedFormat.Webp,
            "png" => DetectedFormat.Png,
            "jpg" or "jpeg" => DetectedFormat.Jpeg,
            "docx" => DetectedFormat.Docx,
            "pptx" => DetectedFormat.Pptx,
            "txt" or "text" => DetectedFormat.Txt,
            _ => DetectedFormat.Unknown
        };
    }
}
