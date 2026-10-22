using System.Diagnostics;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Batch;

public sealed class BatchImageProcessingEngine : IBatchImageProcessingEngine
{
    private static readonly HashSet<string> SupportedBatchActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ActionIds.CompressImage,
        ActionIds.ConvertToWebp,
        ActionIds.ConvertToPng,
        ActionIds.ConvertToJpeg,
        ActionIds.ResizeImage,
        ActionIds.RemoveImageMetadata
    };

    private readonly ProcessorCatalog _catalog;
    private readonly IOutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public BatchImageProcessingEngine(ProcessorCatalog catalog, IOutputPathAllocator allocator, FileClassifier classifier)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public IReadOnlyList<string> ValidateRequest(BatchJobRequest request)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Batch request cannot be null.");
            return errors;
        }

        if (request.Files is null || request.Files.Count == 0)
        {
            errors.Add("No files provided for batch processing.");
        }

        if (string.IsNullOrWhiteSpace(request.ActionId) || !SupportedBatchActions.Contains(request.ActionId))
        {
            errors.Add($"Action '{request.ActionId}' is not supported for batch image processing.");
        }
        else
        {
            var optionErrors = ActionOptionValidator.Validate(request.ActionId, request.Options.Values);
            errors.AddRange(optionErrors);
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            errors.Add("Output directory must be specified.");
        }

        return errors;
    }

    public async Task<BatchSummaryResult> ProcessBatchAsync(
        BatchJobRequest request,
        IProgress<BatchProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid batch request: {string.Join(" ", validationErrors)}");
        }

        var stopwatch = Stopwatch.StartNew();
        var files = request.Files;
        var totalCount = files.Count;
        var itemResults = new BatchItemResult[totalCount];

        int completedCount = 0;
        int succeededCount = 0;
        int failedCount = 0;
        int cancelledCount = 0;
        object progressLock = new();

        void ReportProgress(string? currentFile, string statusMessage)
        {
            if (progress is null) return;

            int pct = totalCount == 0 ? 100 : (int)Math.Round((completedCount / (double)totalCount) * 100);
            progress.Report(new BatchProgressUpdate(
                CompletedCount: completedCount,
                TotalCount: totalCount,
                SucceededCount: succeededCount,
                FailedCount: failedCount,
                CancelledCount: cancelledCount,
                OverallPercent: Math.Clamp(pct, 0, 100),
                CurrentFileName: currentFile,
                StatusMessage: statusMessage));
        }

        ReportProgress(null, "Starting batch image processing…");

        int concurrency = Math.Max(1, Math.Min(request.MaxConcurrency, 4));
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        var tasks = new Task[totalCount];

        for (int i = 0; i < totalCount; i++)
        {
            int index = i;
            var inputFile = files[index];

            tasks[index] = Task.Run(async () =>
            {
                var itemSw = Stopwatch.StartNew();

                if (cancellationToken.IsCancellationRequested)
                {
                    lock (progressLock)
                    {
                        cancelledCount++;
                        completedCount++;
                        itemResults[index] = new BatchItemResult(
                            inputFile,
                            BatchItemStatus.Cancelled,
                            null,
                            "Cancelled before processing started.",
                            ["Cancelled."],
                            new Dictionary<string, string>(),
                            itemSw.Elapsed);
                        ReportProgress(inputFile.FileName, $"Cancelled {inputFile.FileName}");
                    }
                    return;
                }

                bool acquired = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquired = true;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        lock (progressLock)
                        {
                            cancelledCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Cancelled,
                                null,
                                "Cancelled before processing started.",
                                ["Cancelled."],
                                new Dictionary<string, string>(),
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Cancelled {inputFile.FileName}");
                        }
                        return;
                    }

                    lock (progressLock)
                    {
                        ReportProgress(inputFile.FileName, $"Processing ({completedCount + 1}/{totalCount}): {inputFile.FileName}…");
                    }

                    if (!File.Exists(inputFile.Path))
                    {
                        lock (progressLock)
                        {
                            failedCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Failed,
                                null,
                                "Source file not found on disk.",
                                ["Source file not found."],
                                new Dictionary<string, string>(),
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Failed {inputFile.FileName}: File not found");
                        }
                        return;
                    }

                    if (inputFile.Format is not (DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp))
                    {
                        lock (progressLock)
                        {
                            failedCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Failed,
                                null,
                                $"Unsupported format '{inputFile.DisplayType}'. Only JPEG, PNG, and WebP are supported.",
                                ["Unsupported format."],
                                new Dictionary<string, string>(),
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Skipped {inputFile.FileName}: Unsupported format");
                        }
                        return;
                    }

                    var originalHash = FileHasher.Sha256Hex(inputFile.Path);
                    var processor = _catalog.Find(request.ActionId);
                    if (processor is null)
                    {
                        lock (progressLock)
                        {
                            failedCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Failed,
                                null,
                                $"No processor registered for action '{request.ActionId}'.",
                                ["No processor registered."],
                                new Dictionary<string, string>(),
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Failed {inputFile.FileName}: No processor");
                        }
                        return;
                    }

                    var singleRequest = new ProcessingRequest(
                        inputFile,
                        request.ActionId,
                        request.Options,
                        request.OutputDirectory);

                    var result = await processor.ProcessAsync(singleRequest, null, cancellationToken).ConfigureAwait(false);

                    if (result.WasCancelled || cancellationToken.IsCancellationRequested)
                    {
                        lock (progressLock)
                        {
                            cancelledCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Cancelled,
                                null,
                                result.Status,
                                ["Cancelled."],
                                result.Details,
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Cancelled {inputFile.FileName}");
                        }
                    }
                    else if (result.Succeeded && !string.IsNullOrWhiteSpace(result.OutputPath) && File.Exists(result.OutputPath))
                    {
                        // Verify source file remained unchanged
                        var afterHash = FileHasher.Sha256Hex(inputFile.Path);
                        if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
                        {
                            try { File.Delete(result.OutputPath); } catch { }
                            lock (progressLock)
                            {
                                failedCount++;
                                completedCount++;
                                itemResults[index] = new BatchItemResult(
                                    inputFile,
                                    BatchItemStatus.Failed,
                                    null,
                                    "Source file was modified during processing. Output deleted for safety.",
                                    ["Source file mutation detected."],
                                    new Dictionary<string, string>(),
                                    itemSw.Elapsed);
                                ReportProgress(inputFile.FileName, $"Failed {inputFile.FileName}: Source modified");
                            }
                        }
                        else
                        {
                            lock (progressLock)
                            {
                                succeededCount++;
                                completedCount++;
                                itemResults[index] = new BatchItemResult(
                                    inputFile,
                                    BatchItemStatus.Succeeded,
                                    result.OutputPath,
                                    result.Status,
                                    Array.Empty<string>(),
                                    result.Details,
                                    itemSw.Elapsed);
                                ReportProgress(inputFile.FileName, $"Completed {inputFile.FileName}");
                            }
                        }
                    }
                    else
                    {
                        lock (progressLock)
                        {
                            failedCount++;
                            completedCount++;
                            itemResults[index] = new BatchItemResult(
                                inputFile,
                                BatchItemStatus.Failed,
                                null,
                                result.Status,
                                result.Errors,
                                result.Details,
                                itemSw.Elapsed);
                            ReportProgress(inputFile.FileName, $"Failed {inputFile.FileName}: {result.Status}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (progressLock)
                    {
                        cancelledCount++;
                        completedCount++;
                        itemResults[index] = new BatchItemResult(
                            inputFile,
                            BatchItemStatus.Cancelled,
                            null,
                            "Cancelled.",
                            ["Cancelled."],
                            new Dictionary<string, string>(),
                            itemSw.Elapsed);
                        ReportProgress(inputFile.FileName, $"Cancelled {inputFile.FileName}");
                    }
                }
                catch (Exception ex)
                {
                    lock (progressLock)
                    {
                        failedCount++;
                        completedCount++;
                        itemResults[index] = new BatchItemResult(
                            inputFile,
                            BatchItemStatus.Failed,
                            null,
                            ex.Message,
                            [ex.Message],
                            new Dictionary<string, string>(),
                            itemSw.Elapsed);
                        ReportProgress(inputFile.FileName, $"Failed {inputFile.FileName}: {ex.Message}");
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        semaphore.Release();
                    }
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        bool wasCancelled = cancellationToken.IsCancellationRequested || cancelledCount > 0;
        bool allSucceeded = succeededCount == totalCount && totalCount > 0;
        bool anySucceeded = succeededCount > 0;

        string summaryMsg;
        if (wasCancelled)
        {
            summaryMsg = $"Batch cancelled: {succeededCount} succeeded, {failedCount} failed, {cancelledCount} cancelled ({stopwatch.Elapsed.TotalSeconds:F1}s).";
        }
        else if (allSucceeded)
        {
            summaryMsg = $"Batch completed: all {totalCount} images processed successfully ({stopwatch.Elapsed.TotalSeconds:F1}s).";
        }
        else
        {
            summaryMsg = $"Batch finished: {succeededCount} succeeded, {failedCount} failed out of {totalCount} images ({stopwatch.Elapsed.TotalSeconds:F1}s).";
        }

        ReportProgress(null, summaryMsg);

        return new BatchSummaryResult(
            AllSucceeded: allSucceeded,
            AnySucceeded: anySucceeded,
            WasCancelled: wasCancelled,
            TotalCount: totalCount,
            SucceededCount: succeededCount,
            FailedCount: failedCount,
            CancelledCount: cancelledCount,
            ItemResults: itemResults.ToList(),
            Elapsed: stopwatch.Elapsed,
            SummaryMessage: summaryMsg);
    }
}
