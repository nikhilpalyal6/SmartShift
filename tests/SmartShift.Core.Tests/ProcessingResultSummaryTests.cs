using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class ProcessingResultSummaryTests
{
    [Fact]
    public void FromResult_WhenOutputIsSmaller_ComputesReductionCorrectly()
    {
        var file = DummyFile("test.png", 1_000_000);
        var details = new Dictionary<string, string>
        {
            ["outputPath"] = @"C:\Output\test-compressed.png",
            ["outputBytes"] = "400000",
            ["outputDimensions"] = "1920 × 1080",
            ["outputFormat"] = "Png",
            ["encoder"] = "PNG lossless",
            ["originalUnchanged"] = "true"
        };

        var summary = ProcessingResultSummary.FromResult(
            succeeded: true,
            wasCancelled: false,
            originalUnchanged: true,
            status: "Wrote test-compressed.png",
            outputPath: @"C:\Output\test-compressed.png",
            details: details,
            originalFile: file);

        Assert.True(summary.Succeeded);
        Assert.False(summary.WasCancelled);
        Assert.True(summary.IsReduction);
        Assert.False(summary.IsIncrease);
        Assert.False(summary.IsIdentical);
        Assert.Equal(-600_000, summary.DeltaBytes);
        Assert.Equal(-60.0, summary.DeltaPercentage);
        Assert.Contains("-", summary.DeltaText);
        Assert.Contains("60%", summary.DeltaText);
        Assert.Contains("Reduced file size by 60%", summary.SizeComparisonSummary);
        Assert.Equal("1920 × 1080", summary.OutputDimensions);
        Assert.Equal("test-compressed.png", summary.OutputFileName);
    }

    [Fact]
    public void FromResult_WhenOutputIsLarger_ComputesIncreaseCorrectly()
    {
        var file = DummyFile("photo.png", 100_000);
        var details = new Dictionary<string, string>
        {
            ["outputPath"] = @"C:\Output\photo-compressed.png",
            ["outputBytes"] = "105000",
            ["outputFormat"] = "Png",
            ["sizeNote"] = "PNG recompression is lossless and may not reduce size."
        };

        var summary = ProcessingResultSummary.FromResult(
            succeeded: true,
            wasCancelled: false,
            originalUnchanged: true,
            status: "Wrote photo-compressed.png",
            outputPath: @"C:\Output\photo-compressed.png",
            details: details,
            originalFile: file);

        Assert.True(summary.Succeeded);
        Assert.True(summary.IsIncrease);
        Assert.False(summary.IsReduction);
        Assert.Equal(5_000, summary.DeltaBytes);
        Assert.Equal(5.0, summary.DeltaPercentage);
        Assert.Contains("+", summary.DeltaText);
        Assert.Contains("Re-encoding increased file size by 5%", summary.SizeComparisonSummary);
        Assert.NotNull(summary.SizeNote);
    }

    [Fact]
    public void FromResult_WhenInspectionOnly_ReportsInspection()
    {
        var file = DummyFile("info.png", 50_000);
        var summary = ProcessingResultSummary.FromResult(
            succeeded: true,
            wasCancelled: false,
            originalUnchanged: true,
            status: "PNG image inspection",
            outputPath: null,
            details: new Dictionary<string, string>(),
            originalFile: file);

        Assert.True(summary.Succeeded);
        Assert.Null(summary.OutputPath);
        Assert.Null(summary.OutputFileName);
        Assert.False(summary.IsReduction);
        Assert.False(summary.IsIncrease);
        Assert.Contains("Inspection only", summary.SizeComparisonSummary);
    }

    [Fact]
    public void FromResult_WhenCancelled_ReportsCancellation()
    {
        var file = DummyFile("test.jpg", 200_000);
        var summary = ProcessingResultSummary.FromResult(
            succeeded: false,
            wasCancelled: true,
            originalUnchanged: true,
            status: "Operation cancelled",
            outputPath: null,
            details: new Dictionary<string, string>(),
            originalFile: file);

        Assert.False(summary.Succeeded);
        Assert.True(summary.WasCancelled);
        Assert.True(summary.OriginalUnchanged);
    }

    private static ClassifiedFile DummyFile(string name, long length)
        => new(
            Path: @"C:\Temp\" + name,
            FileName: name,
            Extension: Path.GetExtension(name),
            LengthBytes: length,
            LastWriteTimeUtc: DateTimeOffset.UtcNow,
            Format: DetectedFormat.Png,
            ExtensionHint: DetectedFormat.Png,
            Issue: ClassificationIssue.None,
            Status: "Detected PNG",
            PixelWidth: 100,
            PixelHeight: 100,
            PdfVersion: null);
}
