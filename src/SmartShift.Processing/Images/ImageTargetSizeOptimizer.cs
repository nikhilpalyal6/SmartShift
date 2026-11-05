using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Images;

/// <summary>
/// Result of a bounded target file size optimization search.
/// </summary>
public sealed record TargetSizeOptimizationResult(
    bool TargetAchieved,
    long TargetBytes,
    long ActualBytes,
    int SelectedQuality,
    int Iterations,
    string SummaryReason,
    SKData EncodedData);

/// <summary>
/// Bounded binary-search optimizer that adjusts encoding quality to reach a target file size.
/// </summary>
public static class ImageTargetSizeOptimizer
{
    private const int MinQualityFloor = 5;
    private const int MaxQualityCeiling = 100;
    private const int MaxIterations = 7;

    /// <summary>
    /// Searches for the optimal quality level (bounded to max 7 iterations) to meet the requested target size.
    /// </summary>
    public static TargetSizeOptimizationResult Optimize(
        SKBitmap bitmap,
        SKEncodedImageFormat format,
        long targetBytes,
        CancellationToken cancellationToken = default)
    {
        if (targetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBytes), "Target bytes must be positive.");
        }

        // PNG is lossless zlib, quality doesn't drastically change size like lossy JPEG/WebP.
        // If PNG is passed, test low and high zlib compression.
        if (format == SKEncodedImageFormat.Png)
        {
            return OptimizePng(bitmap, targetBytes, cancellationToken);
        }

        // First test at maximum quality (100)
        cancellationToken.ThrowIfCancellationRequested();
        var highData = SkiaImagePipeline.Encode(bitmap, format, MaxQualityCeiling, out _);
        if (highData.Size <= targetBytes)
        {
            return new TargetSizeOptimizationResult(
                TargetAchieved: true,
                TargetBytes: targetBytes,
                ActualBytes: highData.Size,
                SelectedQuality: MaxQualityCeiling,
                Iterations: 1,
                SummaryReason: $"Target achieved at maximum quality ({FileSizeFormatter.Format(highData.Size)} <= {FileSizeFormatter.Format(targetBytes)}).",
                EncodedData: highData);
        }

        // Test at minimum quality floor (5)
        cancellationToken.ThrowIfCancellationRequested();
        var lowData = SkiaImagePipeline.Encode(bitmap, format, MinQualityFloor, out _);
        if (lowData.Size > targetBytes)
        {
            // Even at minimum quality, cannot reach target without downscaling dimensions
            return new TargetSizeOptimizationResult(
                TargetAchieved: false,
                TargetBytes: targetBytes,
                ActualBytes: lowData.Size,
                SelectedQuality: MinQualityFloor,
                Iterations: 2,
                SummaryReason: $"Target size {FileSizeFormatter.Format(targetBytes)} is unreachable without resizing dimensions. Smallest output at quality {MinQualityFloor} is {FileSizeFormatter.Format(lowData.Size)}.",
                EncodedData: lowData);
        }

        // Bounded binary search between [MinQualityFloor, MaxQualityCeiling]
        int low = MinQualityFloor;
        int high = MaxQualityCeiling;
        SKData bestData = lowData;
        int bestQuality = MinQualityFloor;
        int iterations = 2; // already tested 100 and 5

        while (low <= high && iterations < MaxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int mid = low + (high - low) / 2;
            iterations++;

            var currentData = SkiaImagePipeline.Encode(bitmap, format, mid, out _);

            if (currentData.Size <= targetBytes)
            {
                // Fits within target! Can we go higher in quality?
                if (bestData != lowData && bestData != currentData)
                {
                    bestData.Dispose();
                }
                bestData = currentData;
                bestQuality = mid;
                low = mid + 1; // Try to get higher quality that still fits
            }
            else
            {
                // Too large, decrease quality
                currentData.Dispose();
                high = mid - 1;
            }
        }

        // Clean up highData if it wasn't selected
        if (bestData != highData)
        {
            highData.Dispose();
        }

        bool achieved = bestData.Size <= targetBytes;
        string reason = achieved
            ? $"Target achieved at quality {bestQuality} ({FileSizeFormatter.Format(bestData.Size)} of target {FileSizeFormatter.Format(targetBytes)} in {iterations} iterations)."
            : $"Could not fit within target {FileSizeFormatter.Format(targetBytes)}. Best attempt was {FileSizeFormatter.Format(bestData.Size)}.";

        return new TargetSizeOptimizationResult(
            TargetAchieved: achieved,
            TargetBytes: targetBytes,
            ActualBytes: bestData.Size,
            SelectedQuality: bestQuality,
            Iterations: iterations,
            SummaryReason: reason,
            EncodedData: bestData);
    }

    private static TargetSizeOptimizationResult OptimizePng(
        SKBitmap bitmap,
        long targetBytes,
        CancellationToken cancellationToken)
    {
        // Try highest compression (quality 90+ => zlib 9)
        cancellationToken.ThrowIfCancellationRequested();
        var bestData = SkiaImagePipeline.Encode(bitmap, SKEncodedImageFormat.Png, 95, out _);

        bool achieved = bestData.Size <= targetBytes;
        string reason = achieved
            ? $"Target achieved with PNG lossless compression ({FileSizeFormatter.Format(bestData.Size)} <= {FileSizeFormatter.Format(targetBytes)})."
            : $"PNG is a lossless format and cannot be compressed below {FileSizeFormatter.Format(bestData.Size)} to hit {FileSizeFormatter.Format(targetBytes)} without lossy conversion (e.g. WebP or JPEG) or dimension resizing.";

        return new TargetSizeOptimizationResult(
            TargetAchieved: achieved,
            TargetBytes: targetBytes,
            ActualBytes: bestData.Size,
            SelectedQuality: 95,
            Iterations: 1,
            SummaryReason: reason,
            EncodedData: bestData);
    }
}
