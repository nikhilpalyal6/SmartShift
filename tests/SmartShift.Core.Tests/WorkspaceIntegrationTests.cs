using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.Queueing;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class WorkspaceIntegrationTests
{
    private readonly FileClassifier _classifier = new();
    private readonly RecommendationEngine _universalEngine = new(new UniversalActionCapabilities());
    private readonly RecommendationEngine _phase1Engine = new(new Phase1ActionCapabilities());

    [Fact]
    public void UniversalEngine_RecommendsDocumentActionsForDocxAndText()
    {
        using var docx = SampleFiles.Bytes("report.docx", [0x50, 0x4B, 0x03, 0x04]);
        // For classified file with Docx
        var classifiedDocx = new ClassifiedFile("report.docx", "report.docx", ".docx", 100, null, DetectedFormat.Docx, DetectedFormat.Docx, ClassificationIssue.None, "Detected Word (DOCX).", null, null, null);
        var recs = _universalEngine.Recommend(classifiedDocx);

        Assert.NotEmpty(recs);
        Assert.Contains(recs, r => r.ActionId == ActionIds.ShowDocumentInfo);
        Assert.Contains(recs, r => r.ActionId == ActionIds.ImportToPresentationStudio);
        Assert.DoesNotContain(recs, r => r.ActionId == ActionIds.CompressPdf);
        Assert.DoesNotContain(recs, r => r.ActionId == ActionIds.EditImage);

        // Phase 1 engine does not recommend document actions
        Assert.Empty(_phase1Engine.Recommend(classifiedDocx));
    }

    [Fact]
    public void UniversalEngine_RecommendsOnlyInspectionForPptxAndXlsx()
    {
        var classifiedPptx = new ClassifiedFile("deck.pptx", "deck.pptx", ".pptx", 100, null, DetectedFormat.Pptx, DetectedFormat.Pptx, ClassificationIssue.None, "Detected PowerPoint (PPTX).", null, null, null);
        var pptxRecs = _universalEngine.Recommend(classifiedPptx);
        Assert.Single(pptxRecs);
        Assert.Equal(ActionIds.ShowDocumentInfo, pptxRecs[0].ActionId);

        var classifiedXlsx = new ClassifiedFile("sheet.xlsx", "sheet.xlsx", ".xlsx", 100, null, DetectedFormat.Xlsx, DetectedFormat.Xlsx, ClassificationIssue.None, "Detected Excel (XLSX).", null, null, null);
        var xlsxRecs = _universalEngine.Recommend(classifiedXlsx);
        Assert.Single(xlsxRecs);
        Assert.Equal(ActionIds.ShowDocumentInfo, xlsxRecs[0].ActionId);
    }

    [Fact]
    public void UniversalEngine_RecommendsNothingForUnsupportedOrMismatch()
    {
        var classifiedZip = new ClassifiedFile("archive.zip", "archive.zip", ".zip", 100, null, DetectedFormat.Zip, DetectedFormat.Zip, ClassificationIssue.None, "Detected ZIP.", null, null, null);
        Assert.Empty(_universalEngine.Recommend(classifiedZip));

        var classifiedUnknown = new ClassifiedFile("data.bin", "data.bin", ".bin", 100, null, DetectedFormat.Unknown, DetectedFormat.Unknown, ClassificationIssue.UnknownFormat, "Unknown.", null, null, null);
        Assert.Empty(_universalEngine.Recommend(classifiedUnknown));
    }

    [Fact]
    public void FileQueue_MultiFormatIngest_HandlesMixedFilesSafely()
    {
        using var png = SampleFiles.Bytes("image.png", SampleFiles.Png1x1());
        using var pdf = SampleFiles.Bytes("doc.pdf", SampleFiles.Pdf());
        using var txt = SampleFiles.Text("notes.txt", "text content");
        using var unknown = SampleFiles.Bytes("bad.bin", [1, 2, 3]);

        var queue = new FileQueue();
        queue.AddOrReplace(_classifier.Classify(png.Path));
        queue.AddOrReplace(_classifier.Classify(pdf.Path));
        queue.AddOrReplace(_classifier.Classify(txt.Path));
        queue.AddOrReplace(_classifier.Classify(unknown.Path));

        Assert.Equal(4, queue.Items.Count);
        Assert.Equal(3, queue.Items.Count(i => i.IsSupported));
        Assert.Equal(1, queue.Items.Count(i => !i.IsSupported));

        // Source files must remain intact
        Assert.True(File.Exists(png.Path));
        Assert.True(File.Exists(pdf.Path));
        Assert.True(File.Exists(txt.Path));
        Assert.True(File.Exists(unknown.Path));

        queue.Remove(txt.Path);
        Assert.Equal(3, queue.Items.Count);
        Assert.True(File.Exists(txt.Path));
    }
}
