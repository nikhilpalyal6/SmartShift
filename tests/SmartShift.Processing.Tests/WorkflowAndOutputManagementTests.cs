using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.IO;
using SmartShift.Core.Workflows;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.History;
using SmartShift.Processing.Workflows;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class WorkflowAndOutputManagementTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;
    private readonly ProcessorCatalog _catalog;

    public WorkflowAndOutputManagementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SmartShift_WfHistoryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _allocator = new OutputPathAllocator(Path.Combine(_tempDir, "Outputs"));
        _classifier = new FileClassifier();
        _catalog = new ProcessorCatalog(_classifier, _allocator);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private string CreateTestImage(int width = 120, int height = 90, string format = "png")
    {
        var path = Path.Combine(_tempDir, $"img_{Guid.NewGuid():N}.{format}");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.SteelBlue);
        using var img = surface.Snapshot();
        using var data = img.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void HistoryStore_RecordsOperationAndPersistsAcrossReloads()
    {
        var storeFile = Path.Combine(_tempDir, "history.json");
        var store = new ProcessingHistoryStore(storeFile);

        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        var completed = DateTimeOffset.UtcNow;
        var op = new OperationRecord(
            Id: "op-1",
            ActionId: ActionIds.CompressImage,
            ActionName: "Compress Image",
            Category: OperationCategory.Image,
            InputPaths: ["C:\\test\\photo.jpg"],
            OutputPaths: ["C:\\test\\photo-compressed.jpg"],
            StartedAtUtc: started,
            CompletedAtUtc: completed,
            Duration: completed - started,
            Status: OperationStatus.Completed,
            Options: new Dictionary<string, string> { ["quality"] = "80" });

        store.RecordOperation(op);

        Assert.Single(store.GetAll());
        Assert.Equal("op-1", store.FindById("op-1")?.Id);

        // Reload from disk with new store instance
        var reloadedStore = new ProcessingHistoryStore(storeFile);
        var all = reloadedStore.GetAll();
        Assert.Single(all);
        Assert.Equal("op-1", all[0].Id);
        Assert.Equal("Compress Image", all[0].ActionName);
        Assert.Equal(OperationStatus.Completed, all[0].Status);
    }

    [Fact]
    public void HistoryStore_HandlesCorruptedJsonGracefully()
    {
        var storeFile = Path.Combine(_tempDir, "corrupted_history.json");
        File.WriteAllText(storeFile, "{ INVALID JSON PAYLOAD NOT AN ARRAY }");

        var store = new ProcessingHistoryStore(storeFile);
        var records = store.GetAll();

        Assert.Empty(records);

        // Can still write new records safely
        var op = new OperationRecord(
            Id: "op-recovered",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert to WebP",
            Category: OperationCategory.Image,
            InputPaths: ["test.png"],
            OutputPaths: ["test.webp"],
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromMilliseconds(50),
            Status: OperationStatus.Completed,
            Options: new Dictionary<string, string>());

        store.RecordOperation(op);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void HistoryStore_PreventsDuplicatesFromMultipleCallbacks()
    {
        var storeFile = Path.Combine(_tempDir, "idempotent_history.json");
        var store = new ProcessingHistoryStore(storeFile);

        var op = new OperationRecord(
            Id: "op-idempotent",
            ActionId: ActionIds.CompressPdf,
            ActionName: "Compress PDF",
            Category: OperationCategory.Pdf,
            InputPaths: ["doc.pdf"],
            OutputPaths: ["doc-compressed.pdf"],
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromMilliseconds(100),
            Status: OperationStatus.Completed,
            Options: new Dictionary<string, string>());

        store.RecordOperation(op);
        store.RecordOperation(op); // duplicate callback

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void HistoryStore_ClearHistoryPreservesFilesOnDisk()
    {
        var storeFile = Path.Combine(_tempDir, "clear_history.json");
        var store = new ProcessingHistoryStore(storeFile);

        var sampleFile = CreateTestImage(100, 100);
        var op = new OperationRecord(
            Id: "op-clear",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert WebP",
            Category: OperationCategory.Image,
            InputPaths: [sampleFile],
            OutputPaths: [sampleFile],
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            Status: OperationStatus.Completed,
            Options: new Dictionary<string, string>());

        store.RecordOperation(op);
        Assert.Single(store.GetAll());

        store.Clear();
        Assert.Empty(store.GetAll());

        // Source and output files must NOT be deleted
        Assert.True(File.Exists(sampleFile));
    }

    [Fact]
    public void HistoryStore_QueryFiltering_MatchesCriteriaCorrectly()
    {
        var store = new ProcessingHistoryStore(Path.Combine(_tempDir, "filter_history.json"));
        var now = DateTimeOffset.UtcNow;

        var rec1 = new OperationRecord(
            Id: "1",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert to WebP",
            Category: OperationCategory.Image,
            InputPaths: ["hero.png"],
            OutputPaths: ["hero.webp"],
            StartedAtUtc: now.AddDays(-5),
            CompletedAtUtc: now.AddDays(-5).AddSeconds(1),
            Duration: TimeSpan.FromSeconds(1),
            Status: OperationStatus.Completed,
            Options: new Dictionary<string, string>());

        var rec2 = new OperationRecord(
            Id: "2",
            ActionId: ActionIds.CompressPdf,
            ActionName: "Compress PDF",
            Category: OperationCategory.Pdf,
            InputPaths: ["invoice.pdf"],
            OutputPaths: ["invoice-compressed.pdf"],
            StartedAtUtc: now.AddHours(-1),
            CompletedAtUtc: now.AddHours(-1).AddSeconds(2),
            Duration: TimeSpan.FromSeconds(2),
            Status: OperationStatus.Failed,
            Options: new Dictionary<string, string>(),
            FailureReason: "File format was damaged");

        store.RecordOperation(rec1);
        store.RecordOperation(rec2);

        // Status filter
        var failedOnly = store.Query(new OperationFilterCriteria(Status: OperationStatus.Failed));
        Assert.Single(failedOnly);
        Assert.Equal("2", failedOnly[0].Id);

        // Category filter
        var pdfOnly = store.Query(new OperationFilterCriteria(Category: OperationCategory.Pdf));
        Assert.Single(pdfOnly);
        Assert.Equal("2", pdfOnly[0].Id);

        // Search text matching failure reason
        var errorSearch = store.Query(new OperationFilterCriteria(SearchText: "damaged"));
        Assert.Single(errorSearch);
        Assert.Equal("2", errorSearch[0].Id);

        // Search text matching filename
        var nameSearch = store.Query(new OperationFilterCriteria(SearchText: "hero"));
        Assert.Single(nameSearch);
        Assert.Equal("1", nameSearch[0].Id);

        // Date range filter
        var recentOnly = store.Query(new OperationFilterCriteria(FromDateUtc: now.AddDays(-1)));
        Assert.Single(recentOnly);
        Assert.Equal("2", recentOnly[0].Id);
    }

    [Fact]
    public void SavedWorkflowStore_CRUDAndBuiltIns()
    {
        var storeFile = Path.Combine(_tempDir, "workflows.json");
        var store = new SavedWorkflowStore(storeFile);

        var builtIns = store.GetAll();
        Assert.NotEmpty(builtIns);
        Assert.Contains(builtIns, w => w.Id == "wf-webp-web");
        Assert.Contains(builtIns, w => w.Id == "wf-pdf-compress");

        // Built-ins cannot be deleted
        Assert.False(store.Delete("wf-webp-web"));

        // Save custom workflow
        var custom = new SavedWorkflow(
            Id: "custom-social-webp",
            Name: "Social Media WebP",
            Description: "WebP 85% with locked aspect",
            Category: OperationCategory.Image,
            ActionId: ActionIds.ConvertToWebp,
            Options: new Dictionary<string, string> { ["quality"] = "85" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        store.Save(custom);
        Assert.NotNull(store.FindById("custom-social-webp"));

        // Duplicate
        var dup = store.Duplicate("custom-social-webp", "Social Media WebP v2");
        Assert.NotNull(dup);
        Assert.Equal("Social Media WebP v2", dup.Name);
        Assert.NotEqual("custom-social-webp", dup.Id);

        // Delete custom
        Assert.True(store.Delete(dup.Id));
        Assert.Null(store.FindById(dup.Id));
    }

    [Fact]
    public async Task WorkflowExecutionService_ExecutesSavedWorkflowSuccessfully()
    {
        var historyStore = new ProcessingHistoryStore(Path.Combine(_tempDir, "wf_exec_history.json"));
        var executionService = new WorkflowExecutionService(_catalog, _allocator, historyStore);

        var imgPath = CreateTestImage(150, 150);
        var classified = _classifier.Classify(imgPath);

        var workflow = new SavedWorkflow(
            Id: "test-wf-webp",
            Name: "Test WebP Conversion",
            Description: "Converts to WebP 80",
            Category: OperationCategory.Image,
            ActionId: ActionIds.ConvertToWebp,
            Options: new Dictionary<string, string> { ["quality"] = "80" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var result = await executionService.ExecuteAsync(workflow, classified, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith(".webp", result.OutputPath, StringComparison.OrdinalIgnoreCase);

        // Verifies operation was recorded in history
        var history = historyStore.GetAll();
        Assert.Single(history);
        Assert.Equal(OperationStatus.Completed, history[0].Status);
        Assert.Equal(result.OutputPath, history[0].PrimaryOutputPath);
    }

    [Fact]
    public async Task OperationRetryService_RetriesFailedOperationAndLinksRecord()
    {
        var historyStore = new ProcessingHistoryStore(Path.Combine(_tempDir, "retry_history.json"));
        var retryService = new OperationRetryService(_catalog, _allocator, _classifier, historyStore);

        var imgPath = CreateTestImage(200, 200);

        // Create a failed record
        var failedOp = new OperationRecord(
            Id: "failed-op-1",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert to WebP",
            Category: OperationCategory.Image,
            InputPaths: [imgPath],
            OutputPaths: [],
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1),
            Duration: TimeSpan.FromSeconds(1),
            Status: OperationStatus.Failed,
            Options: new Dictionary<string, string> { ["quality"] = "75" },
            FailureReason: "Simulated prior failure");

        historyStore.RecordOperation(failedOp);

        Assert.True(retryService.CanRetry(failedOp, out var readyReason));
        Assert.Equal("Ready to retry.", readyReason);

        var retryResult = await retryService.RetryAsync(failedOp, CancellationToken.None);

        Assert.True(retryResult.Succeeded);
        Assert.NotNull(retryResult.NewRecord);
        Assert.Equal(OperationStatus.Completed, retryResult.NewRecord.Status);
        Assert.Equal("failed-op-1", retryResult.NewRecord.RetryOfOperationId);
        Assert.True(File.Exists(retryResult.OutputPath));

        // Both original failed record and linked retry record exist in history
        var allHistory = historyStore.GetAll();
        Assert.Equal(2, allHistory.Count);
        Assert.Contains(allHistory, h => h.Id == "failed-op-1");
        Assert.Contains(allHistory, h => h.RetryOfOperationId == "failed-op-1");
    }

    [Fact]
    public void OperationRetryService_RejectsMissingInputOrCancelled()
    {
        var historyStore = new ProcessingHistoryStore(Path.Combine(_tempDir, "reject_retry_history.json"));
        var retryService = new OperationRetryService(_catalog, _allocator, _classifier, historyStore);

        // Missing file
        var missingOp = new OperationRecord(
            Id: "missing-input-op",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert to WebP",
            Category: OperationCategory.Image,
            InputPaths: [Path.Combine(_tempDir, "non_existent_file.png")],
            OutputPaths: [],
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            Status: OperationStatus.Failed,
            Options: new Dictionary<string, string> { ["quality"] = "80" });

        Assert.False(retryService.CanRetry(missingOp, out var missingReason));
        Assert.Contains("no longer exists", missingReason);

        // Cancelled op
        var cancelledOp = new OperationRecord(
            Id: "cancelled-op",
            ActionId: ActionIds.ConvertToWebp,
            ActionName: "Convert to WebP",
            Category: OperationCategory.Image,
            InputPaths: [CreateTestImage(100, 100)],
            OutputPaths: [],
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            Status: OperationStatus.Cancelled,
            Options: new Dictionary<string, string> { ["quality"] = "80" });

        Assert.False(retryService.CanRetry(cancelledOp, out var cancelReason));
        Assert.Contains("Cancelled operations cannot be retried", cancelReason);
    }
}
