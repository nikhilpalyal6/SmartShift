using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.Queueing;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class RecommendationEngineTests
{
    private readonly RecommendationEngine _engine = new(new Phase1ActionCapabilities());

    [Fact]
    public void ImageRecommendations_EnableImplementedTransforms()
    {
        using var file = SampleFiles.Bytes("a.png", SampleFiles.Png1x1());
        var classified = new FileClassifier().Classify(file.Path);
        var recs = _engine.Recommend(classified);
        Assert.Contains(recs, r => r.ActionId == ActionIds.ShowImageInfo && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.CompressImage && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.ConvertToWebp && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.ResizeImage && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.RemoveImageMetadata && r.IsAvailable);
        Assert.Contains(recs, r => r.GoalId == GoalIds.ReduceImageForUpload);
    }

    [Fact]
    public void PdfRecommendations_InfoCompressAndPreviewAreAvailable()
    {
        using var file = SampleFiles.Bytes("a.pdf", SampleFiles.Pdf());
        var recs = _engine.Recommend(new FileClassifier().Classify(file.Path));
        Assert.Contains(recs, r => r.ActionId == ActionIds.ShowPdfInfo && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.CompressPdf && r.IsAvailable);
        Assert.Contains(recs, r => r.ActionId == ActionIds.PreviewPdf && r.IsAvailable);
    }

    [Fact]
    public void UnsupportedAndGif_HaveNoRecommendations()
    {
        using var txt = SampleFiles.Text("a.txt", "nope");
        using var gif = SampleFiles.Bytes("a.gif", SampleFiles.Gif());
        var classifier = new FileClassifier();
        Assert.Empty(_engine.Recommend(classifier.Classify(txt.Path)));
        Assert.Empty(_engine.Recommend(classifier.Classify(gif.Path)));
    }

    [Fact]
    public void FileQueue_MixedTypes_RemoveDoesNotDelete()
    {
        using var png = SampleFiles.Bytes("a.png", SampleFiles.Png1x1());
        using var pdf = SampleFiles.Bytes("a.pdf", SampleFiles.Pdf());
        using var txt = SampleFiles.Text("a.txt", "x");
        var classifier = new FileClassifier();
        var queue = new FileQueue();
        queue.AddOrReplace(classifier.Classify(png.Path));
        queue.AddOrReplace(classifier.Classify(pdf.Path));
        queue.AddOrReplace(classifier.Classify(txt.Path));
        Assert.Equal(3, queue.Items.Count);
        Assert.Equal(2, queue.Items.Count(i => i.IsSupportedCategory));
        Assert.True(queue.Remove(txt.Path));
        Assert.Equal(2, queue.Items.Count);
        Assert.True(File.Exists(txt.Path));
        queue.Clear();
        Assert.True(File.Exists(png.Path));
        Assert.True(File.Exists(pdf.Path));

        var first = classifier.Classify(png.Path);
        queue.AddOrReplace(first);
        var again = classifier.Classify(png.Path);
        queue.AddOrReplace(again);
        Assert.Single(queue.Items);
        Assert.Equal(again.LengthBytes, queue.Items[0].LengthBytes);
    }

    [Fact]
    public void OptionValidator_RejectsInvalidValues()
    {
        var quality = ActionOptionValidator.Validate(ActionIds.CompressImage, new Dictionary<string, string> { ["quality"] = "0" });
        Assert.Contains(quality, m => m.Contains("quality", StringComparison.OrdinalIgnoreCase));
        var resize = ActionOptionValidator.Validate(ActionIds.ResizeImage, new Dictionary<string, string>
        {
            ["width"] = "abc",
            ["keepAspect"] = "false"
        });
        Assert.NotEmpty(resize);
        var ok = ActionOptionValidator.Validate(ActionIds.ShowImageInfo, new Dictionary<string, string>());
        Assert.Empty(ok);
        var tooBig = ActionOptionValidator.Validate(ActionIds.ResizeImage, new Dictionary<string, string>
        {
            ["width"] = "9000",
            ["keepAspect"] = "true"
        });
        Assert.NotEmpty(tooBig);
    }
}
