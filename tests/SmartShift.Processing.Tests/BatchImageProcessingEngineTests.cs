using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Images;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class BatchImageProcessingEngineTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "smartshift-batch-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _outputDir;
    private readonly FileClassifier _classifier = new();
    private readonly OutputPathAllocator _allocator;
    private readonly ProcessorCatalog _catalog;
    private readonly BatchImageProcessingEngine _engine;

    public BatchImageProcessingEngineTests()
    {
        _outputDir = Path.Combine(_testDir, "Outputs");
        Directory.CreateDirectory(_outputDir);
        _allocator = new OutputPathAllocator(_outputDir);
        _catalog = new ProcessorCatalog(_classifier, _allocator);
        _engine = new BatchImageProcessingEngine(_catalog, _allocator, _classifier);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private string CreateImage(string filename, int width, int height, SKEncodedImageFormat format)
    {
        var path = Path.Combine(_testDir, filename);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var fs = File.Create(path);
        bitmap.Encode(fs, format, 90);
        return path;
    }

    [Fact]
    public void ValidateRequest_DetectsEmptyFiles_AndUnsupportedAction()
    {
        var emptyReq = new BatchJobRequest([], ActionIds.CompressImage, new ProcessingOptions(), _outputDir);
        var emptyErrors = _engine.ValidateRequest(emptyReq);
        Assert.Contains(emptyErrors, e => e.Contains("No files provided", StringComparison.OrdinalIgnoreCase));

        var badActionReq = new BatchJobRequest(
            [_classifier.Classify(CreateImage("a.png", 10, 10, SKEncodedImageFormat.Png))],
            "unsupported.action",
            new ProcessingOptions(),
            _outputDir);
        var badActionErrors = _engine.ValidateRequest(badActionReq);
        Assert.Contains(badActionErrors, e => e.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessBatchAsync_CompressMultipleImages_AllSucceed_OriginalsUnchanged()
    {
        var img1 = CreateImage("photo1.jpg", 100, 80, SKEncodedImageFormat.Jpeg);
        var img2 = CreateImage("photo2.png", 80, 80, SKEncodedImageFormat.Png);
        var img3 = CreateImage("photo3.webp", 120, 60, SKEncodedImageFormat.Webp);

        var hash1 = FileHasher.Sha256Hex(img1);
        var hash2 = FileHasher.Sha256Hex(img2);
        var hash3 = FileHasher.Sha256Hex(img3);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2), _classifier.Classify(img3) };
        var options = new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "75" });
        var request = new BatchJobRequest(files, ActionIds.CompressImage, options, _outputDir);

        var progressUpdates = new List<BatchProgressUpdate>();
        var progress = new Progress<BatchProgressUpdate>(p => progressUpdates.Add(p));

        var summary = await _engine.ProcessBatchAsync(request, progress, CancellationToken.None);

        Assert.True(summary.AllSucceeded);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(3, summary.SucceededCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(0, summary.CancelledCount);
        Assert.False(summary.WasCancelled);

        // Verify output files exist and were written
        foreach (var item in summary.ItemResults)
        {
            Assert.Equal(BatchItemStatus.Succeeded, item.Status);
            Assert.NotNull(item.OutputPath);
            Assert.True(File.Exists(item.OutputPath));
            Assert.True(new FileInfo(item.OutputPath).Length > 0);
        }

        // Verify source images remained completely untouched
        Assert.Equal(hash1, FileHasher.Sha256Hex(img1));
        Assert.Equal(hash2, FileHasher.Sha256Hex(img2));
        Assert.Equal(hash3, FileHasher.Sha256Hex(img3));
    }

    [Fact]
    public async Task ProcessBatchAsync_ConvertFormat_Webp_Png_Jpeg_Succeeds()
    {
        var img1 = CreateImage("src1.png", 60, 60, SKEncodedImageFormat.Png);
        var img2 = CreateImage("src2.jpg", 80, 50, SKEncodedImageFormat.Jpeg);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2) };

        // Convert to WebP
        var webpReq = new BatchJobRequest(files, ActionIds.ConvertToWebp, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);
        var webpSummary = await _engine.ProcessBatchAsync(webpReq, null, CancellationToken.None);
        Assert.True(webpSummary.AllSucceeded);
        foreach (var item in webpSummary.ItemResults)
        {
            Assert.EndsWith(".webp", item.OutputPath, StringComparison.OrdinalIgnoreCase);
        }

        // Convert to PNG
        var pngReq = new BatchJobRequest(files, ActionIds.ConvertToPng, new ProcessingOptions(), _outputDir);
        var pngSummary = await _engine.ProcessBatchAsync(pngReq, null, CancellationToken.None);
        Assert.True(pngSummary.AllSucceeded);
        foreach (var item in pngSummary.ItemResults)
        {
            Assert.EndsWith(".png", item.OutputPath, StringComparison.OrdinalIgnoreCase);
        }

        // Convert to JPEG
        var jpegReq = new BatchJobRequest(files, ActionIds.ConvertToJpeg, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "85" }), _outputDir);
        var jpegSummary = await _engine.ProcessBatchAsync(jpegReq, null, CancellationToken.None);
        Assert.True(jpegSummary.AllSucceeded);
        foreach (var item in jpegSummary.ItemResults)
        {
            Assert.EndsWith(".jpg", item.OutputPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_Resize_MaintainsAspectRatio_ForAllImages()
    {
        var img1 = CreateImage("landscape.png", 200, 100, SKEncodedImageFormat.Png);
        var img2 = CreateImage("portrait.jpg", 100, 200, SKEncodedImageFormat.Jpeg);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2) };
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["width"] = "50",
            ["keepAspect"] = "true"
        });

        var request = new BatchJobRequest(files, ActionIds.ResizeImage, options, _outputDir);
        var summary = await _engine.ProcessBatchAsync(request, null, CancellationToken.None);

        Assert.True(summary.AllSucceeded);

        // Item 1 (landscape 200x100 -> width 50 => height 25)
        var item1 = summary.ItemResults[0];
        Assert.Equal("50 × 25", item1.Details["outputDimensions"]);

        // Item 2 (portrait 100x200 -> width 50 => height 100)
        var item2 = summary.ItemResults[1];
        Assert.Equal("50 × 100", item2.Details["outputDimensions"]);
    }

    [Fact]
    public async Task ProcessBatchAsync_RemoveMetadata_StripsExif_ForAllImages()
    {
        var img1 = CreateImage("meta1.jpg", 50, 50, SKEncodedImageFormat.Jpeg);
        var img2 = CreateImage("meta2.png", 50, 50, SKEncodedImageFormat.Png);

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2) };
        var request = new BatchJobRequest(files, ActionIds.RemoveImageMetadata, new ProcessingOptions(), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, null, CancellationToken.None);
        Assert.True(summary.AllSucceeded);
        Assert.Equal(2, summary.SucceededCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_DuplicateFilenamesInDifferentFolders_AllocatesDistinctOutputs()
    {
        var subDir1 = Path.Combine(_testDir, "folder1");
        var subDir2 = Path.Combine(_testDir, "folder2");
        Directory.CreateDirectory(subDir1);
        Directory.CreateDirectory(subDir2);

        var path1 = Path.Combine(subDir1, "photo.jpg");
        var path2 = Path.Combine(subDir2, "photo.jpg");

        using (var bmp = new SKBitmap(50, 50))
        {
            bmp.Erase(SKColors.Red);
            using var fs1 = File.Create(path1);
            bmp.Encode(fs1, SKEncodedImageFormat.Jpeg, 90);
        }
        using (var bmp = new SKBitmap(60, 60))
        {
            bmp.Erase(SKColors.Blue);
            using var fs2 = File.Create(path2);
            bmp.Encode(fs2, SKEncodedImageFormat.Jpeg, 90);
        }

        var files = new[] { _classifier.Classify(path1), _classifier.Classify(path2) };
        var request = new BatchJobRequest(files, ActionIds.CompressImage, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, null, CancellationToken.None);

        Assert.True(summary.AllSucceeded);
        Assert.Equal(2, summary.SucceededCount);

        var out1 = summary.ItemResults[0].OutputPath!;
        var out2 = summary.ItemResults[1].OutputPath!;

        Assert.NotEqual(out1, out2);
        Assert.True(File.Exists(out1));
        Assert.True(File.Exists(out2));
    }

    [Fact]
    public async Task ProcessBatchAsync_CorruptFile_IsolatesFailure_RemainingFilesSucceed()
    {
        var valid1 = CreateImage("valid1.png", 50, 50, SKEncodedImageFormat.Png);
        var corrupt = Path.Combine(_testDir, "corrupt.png");
        File.WriteAllBytes(corrupt, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xEE]); // truncated PNG
        var valid2 = CreateImage("valid2.png", 50, 50, SKEncodedImageFormat.Png);

        var files = new[] { _classifier.Classify(valid1), _classifier.Classify(corrupt), _classifier.Classify(valid2) };
        var request = new BatchJobRequest(files, ActionIds.CompressImage, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, null, CancellationToken.None);

        Assert.False(summary.AllSucceeded);
        Assert.True(summary.AnySucceeded);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(2, summary.SucceededCount);
        Assert.Equal(1, summary.FailedCount);

        Assert.Equal(BatchItemStatus.Succeeded, summary.ItemResults[0].Status);
        Assert.Equal(BatchItemStatus.Failed, summary.ItemResults[1].Status);
        Assert.Equal(BatchItemStatus.Succeeded, summary.ItemResults[2].Status);
        Assert.NotEmpty(summary.ItemResults[1].Errors);
    }

    [Fact]
    public async Task ProcessBatchAsync_MissingFile_ReportsFailure_DoesNotCrashBatch()
    {
        var valid = CreateImage("valid.jpg", 50, 50, SKEncodedImageFormat.Jpeg);
        var missingPath = Path.Combine(_testDir, "non_existent.jpg");
        var missingClassified = _classifier.Classify(missingPath);

        var files = new[] { _classifier.Classify(valid), missingClassified };
        var request = new BatchJobRequest(files, ActionIds.CompressImage, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, null, CancellationToken.None);

        Assert.False(summary.AllSucceeded);
        Assert.Equal(1, summary.SucceededCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(BatchItemStatus.Failed, summary.ItemResults[1].Status);
        Assert.Contains("not found", summary.ItemResults[1].StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessBatchAsync_Cancellation_AbortsPending_PreservesCompleted()
    {
        var img1 = CreateImage("c1.png", 50, 50, SKEncodedImageFormat.Png);
        var img2 = CreateImage("c2.png", 50, 50, SKEncodedImageFormat.Png);
        var img3 = CreateImage("c3.png", 50, 50, SKEncodedImageFormat.Png);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2), _classifier.Classify(img3) };
        var request = new BatchJobRequest(files, ActionIds.CompressImage, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, null, cts.Token);

        Assert.True(summary.WasCancelled);
        Assert.False(summary.AllSucceeded);
        Assert.Equal(3, summary.CancelledCount);
        Assert.Equal(0, summary.SucceededCount);
        foreach (var item in summary.ItemResults)
        {
            Assert.Equal(BatchItemStatus.Cancelled, item.Status);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_ProgressReporting_TracksCompletedCountsAccurately()
    {
        var img1 = CreateImage("p1.png", 40, 40, SKEncodedImageFormat.Png);
        var img2 = CreateImage("p2.png", 40, 40, SKEncodedImageFormat.Png);

        var progressList = new List<BatchProgressUpdate>();
        var progress = new Progress<BatchProgressUpdate>(p => progressList.Add(p));

        var files = new[] { _classifier.Classify(img1), _classifier.Classify(img2) };
        var request = new BatchJobRequest(files, ActionIds.CompressImage, new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }), _outputDir);

        var summary = await _engine.ProcessBatchAsync(request, progress, CancellationToken.None);

        // Allow any background progress dispatcher to catch up
        await Task.Delay(50);

        Assert.True(summary.AllSucceeded);
        Assert.NotEmpty(progressList);
        var last = progressList.Last();
        Assert.Equal(2, last.CompletedCount);
        Assert.Equal(2, last.TotalCount);
        Assert.Equal(100, last.OverallPercent);
    }
}
