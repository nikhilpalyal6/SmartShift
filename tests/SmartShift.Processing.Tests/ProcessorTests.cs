using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class ProcessorTests
{
    [Fact]
    public async Task Inspect_DoesNotWriteOutputAndLeavesOriginal()
    {
        var path = Write("info.png", Png());
        try
        {
            var before = File.ReadAllBytes(path);
            var classifier = new FileClassifier();
            var catalog = new ProcessorCatalog(classifier, TempAllocator());
            var processor = catalog.Find(ActionIds.ShowImageInfo);
            Assert.NotNull(processor);
            Assert.True(processor!.IsAvailable);
            var input = classifier.Classify(path);
            var result = await processor.ProcessAsync(
                new ProcessingRequest(input, ActionIds.ShowImageInfo, new ProcessingOptions(), Path.GetTempPath()),
                null,
                CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Null(result.OutputPath);
            Assert.True(result.OriginalUnchanged);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc, input.LastWriteTimeUtc?.UtcDateTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unavailable_DoesNotReportSuccessfulConversion()
    {
        var path = Write("x.png", Png());
        var outputDir = Path.Combine(Path.GetTempPath(), "smartshift-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        try
        {
            var classifier = new FileClassifier();
            var catalog = new ProcessorCatalog(classifier, TempAllocator());
            var processor = new UnavailableTransformationProcessor("ocr.extract", "Tesseract OCR");
            Assert.NotNull(processor);
            Assert.False(processor.IsAvailable);
            var result = await processor.ProcessAsync(
                new ProcessingRequest(classifier.Classify(path), "ocr.extract", new ProcessingOptions(), outputDir),
                null,
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Null(result.OutputPath);
            Assert.True(result.OriginalUnchanged);
            Assert.Empty(Directory.GetFiles(outputDir));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Process_HonoursCancellation()
    {
        var catalog = new ProcessorCatalog(new FileClassifier(), TempAllocator());
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var dummy = new ClassifiedFile("n", "n", "", 0, null, DetectedFormat.Png, DetectedFormat.Png, ClassificationIssue.None, "", null, null, null);
        var inspect = await catalog.Find(ActionIds.ShowImageInfo)!.ProcessAsync(
            new ProcessingRequest(dummy, ActionIds.ShowImageInfo, new ProcessingOptions(), "."),
            null,
            cts.Token);
        Assert.True(inspect.WasCancelled);
        var missing = await catalog.Find(ActionIds.PreviewPdf)!.ProcessAsync(
            new ProcessingRequest(dummy, ActionIds.PreviewPdf, new ProcessingOptions(), "."),
            null,
            cts.Token);
        Assert.True(missing.WasCancelled);
    }

    [Fact]
    public async Task Inspect_ReportsProgress()
    {
        var path = Write("prog.png", Png());
        try
        {
            var reports = new List<ProcessingProgress>();
            var classifier = new FileClassifier();
            var processor = new ProcessorCatalog(classifier, TempAllocator()).Find(ActionIds.ShowImageInfo)!;
            await processor.ProcessAsync(
                new ProcessingRequest(classifier.Classify(path), ActionIds.ShowImageInfo, new ProcessingOptions(), Path.GetTempPath()),
                new Progress<ProcessingProgress>(reports.Add),
                CancellationToken.None);
            Assert.Contains(reports, r => r.Percent == 100);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IOutputPathAllocator TempAllocator()
        => new OutputPathAllocator(Path.Combine(Path.GetTempPath(), "smartshift-out-" + Guid.NewGuid().ToString("N")));

    private static string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "smartshift-proc-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
