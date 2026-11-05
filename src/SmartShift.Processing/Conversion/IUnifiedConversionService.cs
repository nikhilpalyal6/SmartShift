using SmartShift.Core.Classification;

namespace SmartShift.Processing.Conversion;

/// <summary>
/// Service contract for the unified conversion workflow.
/// Dynamically queries supported conversion targets, validates paths, and coordinates execution.
/// </summary>
public interface IUnifiedConversionService
{
    /// <summary>
    /// Gets all genuinely supported target formats for a given source format.
    /// Excludes the source format itself and formats without registered converters.
    /// </summary>
    IReadOnlyList<DetectedFormat> GetSupportedTargets(DetectedFormat sourceFormat);

    /// <summary>
    /// Checks whether conversion between source and target format is genuinely supported by registered processors.
    /// </summary>
    bool CanConvert(DetectedFormat sourceFormat, DetectedFormat targetFormat);

    /// <summary>
    /// Resolves the concrete processor action ID and normalized options for a given conversion request.
    /// </summary>
    ConversionRoute ResolveRoute(ClassifiedFile source, DetectedFormat targetFormat, IReadOnlyDictionary<string, string>? userOptions = null);

    /// <summary>
    /// Executes the conversion asynchronously off the UI thread with progress reporting and cancellation.
    /// </summary>
    Task<ProcessingResult> ConvertAsync(
        ClassifiedFile source,
        DetectedFormat targetFormat,
        IReadOnlyDictionary<string, string> userOptions,
        string outputDirectory,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);
}
