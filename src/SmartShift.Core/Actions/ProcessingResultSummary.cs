using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Core.Actions;

public enum ResultState
{
    TargetReached,
    TargetNotReached,
    Completed,
    Failed,
    Cancelled
}

public sealed class ProcessingResultSummary
{
    public string? OutputPath { get; }
    public string? OutputFileName { get; }
    public long OriginalSizeBytes { get; }
    public long OutputSizeBytes { get; }
    public string OriginalSizeText { get; }
    public string OutputSizeText { get; }
    public string? OutputDimensions { get; }
    public string? OutputFormat { get; }
    public long DeltaBytes { get; }
    public double DeltaPercentage { get; }
    public string DeltaText { get; }
    public bool IsReduction { get; }
    public bool IsIncrease { get; }
    public bool IsIdentical { get; }
    public string SizeComparisonSummary { get; }
    public bool OriginalUnchanged { get; }
    public string? EncoderNote { get; }
    public string? MetadataNote { get; }
    public string? SizeNote { get; }
    public string Status { get; }
    public bool Succeeded { get; }
    public bool WasCancelled { get; }
    public long? TargetSizeBytes { get; }
    public string? TargetSizeText { get; }
    public bool? TargetReached { get; }
    public string? TargetStatusNote { get; }
    public int ImagesOptimizedCount { get; }
    public int ImagesDiscoveredCount { get; }
    public int ImagesEligibleCount { get; }
    public string? SkippedReasonsSummary { get; }
    public string? LimitsExplanation { get; }
    public string? StageApplied { get; }
    public ResultState ResultState { get; }
    public string StateTitle { get; }
    public string StateBadgeText { get; }
    public string? ActionId { get; }
    public string? CustomSummaryNote { get; }
    public int ExportedPagesCount { get; }
    public int TotalPagesCount { get; }
    public int ExtractedCharCount { get; }
    public bool IsScannedOrEmpty { get; }

    public ProcessingResultSummary(
        string status,
        bool succeeded,
        bool wasCancelled,
        bool originalUnchanged,
        string? outputPath,
        long originalSizeBytes,
        long outputSizeBytes,
        string? outputDimensions,
        string? outputFormat,
        string? encoderNote,
        string? metadataNote,
        string? sizeNote,
        long? targetSizeBytes = null,
        string? targetSizeText = null,
        bool? targetReached = null,
        string? targetStatusNote = null,
        int imagesOptimizedCount = 0,
        int imagesDiscoveredCount = 0,
        int imagesEligibleCount = 0,
        string? skippedReasonsSummary = null,
        string? limitsExplanation = null,
        string? stageApplied = null,
        string? actionId = null,
        string? customSummaryNote = null,
        int exportedPagesCount = 0,
        int totalPagesCount = 0,
        int extractedCharCount = 0,
        bool isScannedOrEmpty = false)
    {
        Status = status;
        Succeeded = succeeded;
        WasCancelled = wasCancelled;
        OriginalUnchanged = originalUnchanged;
        OutputPath = outputPath;
        OutputFileName = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFileName(outputPath);
        OriginalSizeBytes = originalSizeBytes;
        OutputSizeBytes = outputSizeBytes;
        OriginalSizeText = FileSizeFormatter.Format(originalSizeBytes);
        OutputSizeText = FileSizeFormatter.Format(outputSizeBytes);
        OutputDimensions = outputDimensions;
        OutputFormat = outputFormat;
        EncoderNote = encoderNote;
        MetadataNote = metadataNote;
        SizeNote = sizeNote;
        TargetSizeBytes = targetSizeBytes;
        TargetSizeText = targetSizeText;
        TargetReached = targetReached;
        TargetStatusNote = targetStatusNote;
        ImagesOptimizedCount = imagesOptimizedCount;
        ImagesDiscoveredCount = imagesDiscoveredCount;
        ImagesEligibleCount = imagesEligibleCount;
        SkippedReasonsSummary = skippedReasonsSummary;
        LimitsExplanation = limitsExplanation;
        StageApplied = stageApplied;
        ActionId = actionId;
        CustomSummaryNote = customSummaryNote;
        ExportedPagesCount = exportedPagesCount;
        TotalPagesCount = totalPagesCount;
        ExtractedCharCount = extractedCharCount;
        IsScannedOrEmpty = isScannedOrEmpty;

        if (!succeeded)
        {
            ResultState = wasCancelled ? ResultState.Cancelled : ResultState.Failed;
            StateTitle = wasCancelled ? "Cancelled" : "Failed — No valid output produced";
            StateBadgeText = wasCancelled ? "CANCELLED" : "FAILED";
        }
        else if (targetSizeBytes.HasValue)
        {
            if (targetReached == true)
            {
                ResultState = ResultState.TargetReached;
                StateTitle = "Completed — Target reached";
                StateBadgeText = "TARGET REACHED";
            }
            else
            {
                ResultState = ResultState.TargetNotReached;
                StateTitle = "Completed — Target not reached";
                StateBadgeText = "TARGET NOT REACHED";
            }
        }
        else
        {
            ResultState = ResultState.Completed;
            StateTitle = "Operation Complete";
            StateBadgeText = "COMPLETED";
        }

        DeltaText = "";

        if (!string.IsNullOrWhiteSpace(customSummaryNote))
        {
            SizeComparisonSummary = customSummaryNote;
        }
        else if (succeeded && !string.IsNullOrWhiteSpace(outputPath) && originalSizeBytes > 0 && outputSizeBytes > 0)
        {
            DeltaBytes = outputSizeBytes - originalSizeBytes;
            DeltaPercentage = Math.Round((DeltaBytes / (double)originalSizeBytes) * 100.0, 1);

            if (DeltaBytes < 0)
            {
                IsReduction = true;
                DeltaText = $"-{FileSizeFormatter.Format(Math.Abs(DeltaBytes))} ({DeltaPercentage:0.#}%)";
                if (targetReached == true && !string.IsNullOrWhiteSpace(targetSizeText))
                {
                    SizeComparisonSummary = $"Target {targetSizeText} met! Reduced by {Math.Abs(DeltaPercentage):0.#}%";
                }
                else if (targetReached == false && !string.IsNullOrWhiteSpace(targetSizeText))
                {
                    SizeComparisonSummary = $"Target {targetSizeText} not reached. Closest safe reduction: {OutputSizeText} ({Math.Abs(DeltaPercentage):0.#}% reduction)";
                }
                else
                {
                    SizeComparisonSummary = $"Reduced file size by {Math.Abs(DeltaPercentage):0.#}%";
                }
            }
            else if (DeltaBytes > 0)
            {
                IsIncrease = true;
                DeltaText = $"+{FileSizeFormatter.Format(DeltaBytes)} (+{DeltaPercentage:0.#}%)";
                SizeComparisonSummary = $"Re-encoding increased file size by {DeltaPercentage:0.#}%";
            }
            else
            {
                IsIdentical = true;
                DeltaText = "0 B (0.0%)";
                SizeComparisonSummary = "Output size is identical to original";
            }
        }
        else
        {
            DeltaBytes = 0;
            DeltaPercentage = 0.0;
            DeltaText = "";
            SizeComparisonSummary = succeeded ? "Inspection only. No file was written." : "No output generated.";
        }
    }

    public static ProcessingResultSummary FromResult(
        bool succeeded,
        bool wasCancelled,
        bool originalUnchanged,
        string status,
        string? outputPath,
        IReadOnlyDictionary<string, string> details,
        ClassifiedFile originalFile)
    {
        long outputBytes = 0;
        if (details.TryGetValue("outputBytes", out var bytesRaw) && long.TryParse(bytesRaw, out var parsedBytes))
        {
            outputBytes = parsedBytes;
        }
        else if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
        {
            try
            {
                outputBytes = new FileInfo(outputPath).Length;
            }
            catch
            {
                outputBytes = 0;
            }
        }

        details.TryGetValue("outputDimensions", out var dims);
        details.TryGetValue("outputFormat", out var format);
        details.TryGetValue("encoder", out var encoder);
        details.TryGetValue("metadata", out var metadata);
        details.TryGetValue("sizeNote", out var sizeNote);

        long? targetBytes = null;
        if (details.TryGetValue("targetSizeBytes", out var targetBytesStr) && long.TryParse(targetBytesStr, out var tb))
        {
            targetBytes = tb;
        }

        details.TryGetValue("targetSize", out var targetSize);

        bool? targetReached = null;
        if (details.TryGetValue("targetReached", out var targetReachedStr) && bool.TryParse(targetReachedStr, out var tr))
        {
            targetReached = tr;
        }

        details.TryGetValue("targetStatus", out var targetStatus);

        int imagesOptimized = 0;
        if ((details.TryGetValue("imagesOptimized", out var imgOptStr) || details.TryGetValue("imagesOptimizedCount", out imgOptStr)) && int.TryParse(imgOptStr, out var io))
        {
            imagesOptimized = io;
        }

        int imagesDiscovered = 0;
        if (details.TryGetValue("imagesDiscovered", out var imgDiscStr) && int.TryParse(imgDiscStr, out var id))
        {
            imagesDiscovered = id;
        }

        int imagesEligible = 0;
        if (details.TryGetValue("imagesEligible", out var imgEligStr) && int.TryParse(imgEligStr, out var ie))
        {
            imagesEligible = ie;
        }

        details.TryGetValue("skippedReasons", out var skippedReasons);
        details.TryGetValue("limitsExplanation", out var limitsExplanation);
        details.TryGetValue("stage", out var stage);
        details.TryGetValue("actionId", out var actionId);
        details.TryGetValue("summaryNote", out var summaryNote);

        int exportedPages = 0;
        if (details.TryGetValue("exportedPages", out var epStr) && int.TryParse(epStr, out var ep))
        {
            exportedPages = ep;
        }

        int totalPages = 0;
        if (details.TryGetValue("totalPages", out var tpStr) && int.TryParse(tpStr, out var tp))
        {
            totalPages = tp;
        }

        int extractedChars = 0;
        if (details.TryGetValue("extractedChars", out var ecStr) && int.TryParse(ecStr, out var ec))
        {
            extractedChars = ec;
        }

        bool isScanned = string.Equals(details.GetValueOrDefault("isScanned"), "true", StringComparison.OrdinalIgnoreCase);

        return new ProcessingResultSummary(
            status: status,
            succeeded: succeeded,
            wasCancelled: wasCancelled,
            originalUnchanged: originalUnchanged,
            outputPath: outputPath,
            originalSizeBytes: originalFile.LengthBytes,
            outputSizeBytes: outputBytes,
            outputDimensions: dims,
            outputFormat: format,
            encoderNote: encoder,
            metadataNote: metadata,
            sizeNote: sizeNote,
            targetSizeBytes: targetBytes,
            targetSizeText: targetSize,
            targetReached: targetReached,
            targetStatusNote: targetStatus,
            imagesOptimizedCount: imagesOptimized,
            imagesDiscoveredCount: imagesDiscovered,
            imagesEligibleCount: imagesEligible,
            skippedReasonsSummary: skippedReasons,
            limitsExplanation: limitsExplanation,
            stageApplied: stage,
            actionId: actionId,
            customSummaryNote: summaryNote,
            exportedPagesCount: exportedPages,
            totalPagesCount: totalPages,
            extractedCharCount: extractedChars,
            isScannedOrEmpty: isScanned);
    }
}
