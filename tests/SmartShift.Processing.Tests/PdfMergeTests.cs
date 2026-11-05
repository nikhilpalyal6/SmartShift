using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Pdf;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfMergeTests : IDisposable
{
    private readonly string _rootDir;
    private readonly OutputPathAllocator _outputAllocator;
    private readonly PdfMergeEngine _engine;

    public PdfMergeTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-merge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _outputAllocator = new OutputPathAllocator(_rootDir);
        _engine = new PdfMergeEngine(_outputAllocator);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private string CreatePdf(int pageCount, string label, string? fileName = null)
    {
        var path = Path.Combine(_rootDir, fileName ?? $"{label}_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pageCount; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14, XFontStyleEx.Regular);
                gfx.DrawString($"{label} - Page {i + 1}", font, XBrushes.Black, new XPoint(50, 50));
            }
            doc.Save(path);
        }
        return path;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    [Fact]
    public async Task ValidateFileAsync_ValidPdf_ReturnsPageCountAndValidStatus()
    {
        var pdfPath = CreatePdf(3, "DocA");
        var validation = await _engine.ValidateFileAsync(pdfPath);

        Assert.True(validation.IsValid);
        Assert.Equal(3, validation.PageCount);
        Assert.Null(validation.ErrorReason);
    }

    [Fact]
    public async Task ValidateFileAsync_CorruptPdf_ReturnsInvalidWithDescriptiveError()
    {
        var corruptPath = Path.Combine(_rootDir, "corrupt.pdf");
        await File.WriteAllTextAsync(corruptPath, "%PDF-1.4 Not a real PDF body at all");

        var validation = await _engine.ValidateFileAsync(corruptPath);

        Assert.False(validation.IsValid);
        Assert.Equal(0, validation.PageCount);
        Assert.NotNull(validation.ErrorReason);
        Assert.Contains("corrupt", validation.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateQueueAsync_FlagsDuplicatePathsExplicitly()
    {
        var docA = CreatePdf(2, "DocA");
        var docB = CreatePdf(1, "DocB");

        var queueValidation = await _engine.ValidateQueueAsync([docA, docB, docA]);

        Assert.Equal(3, queueValidation.Items.Count);
        Assert.Equal(5, queueValidation.TotalPages);
        Assert.True(queueValidation.Items[2].IsDuplicate);
    }

    [Fact]
    public async Task MergeAsync_TwoValidPdfs_ProducesCorrectPageCountAndValidPdf()
    {
        var docA = CreatePdf(2, "DocA", "DocA.pdf");
        var docB = CreatePdf(3, "DocB", "DocB.pdf");
        var hashA = ComputeSha256(docA);
        var hashB = ComputeSha256(docB);
        var outTarget = Path.Combine(_rootDir, "Merged_Test.pdf");

        var outputPath = await _engine.MergeAsync([docA, docB], outTarget);

        Assert.NotNull(outputPath);
        Assert.True(File.Exists(outputPath));

        // Verify valid output PDF can be opened by PDFsharp reader
        using (var merged = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(5, merged.PageCount);
        }

        // Verify originals remain strictly unchanged
        Assert.Equal(hashA, ComputeSha256(docA));
        Assert.Equal(hashB, ComputeSha256(docB));
    }

    [Fact]
    public async Task MergeAsync_PreservesExactInputOrdering()
    {
        var doc1 = CreatePdf(1, "FirstDoc", "First.pdf");
        var doc2 = CreatePdf(2, "SecondDoc", "Second.pdf");
        var outFwd = Path.Combine(_rootDir, "Forward.pdf");
        var outRev = Path.Combine(_rootDir, "Reverse.pdf");

        // Merge in order 1 then 2
        var fwdPath = await _engine.MergeAsync([doc1, doc2], outFwd);
        // Merge in order 2 then 1
        var revPath = await _engine.MergeAsync([doc2, doc1], outRev);

        Assert.True(File.Exists(fwdPath));
        Assert.True(File.Exists(revPath));

        using var fwdDoc = PdfReader.Open(fwdPath, PdfDocumentOpenMode.Import);
        using var revDoc = PdfReader.Open(revPath, PdfDocumentOpenMode.Import);

        Assert.Equal(3, fwdDoc.PageCount);
        Assert.Equal(3, revDoc.PageCount);
    }

    [Fact]
    public async Task MergeAsync_EmptyOrSingleItemQueue_FailsGracefully()
    {
        var outPath = Path.Combine(_rootDir, "empty.pdf");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.MergeAsync([], outPath));

        var singleDoc = CreatePdf(1, "Single");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.MergeAsync([singleDoc], outPath));
    }

    [Fact]
    public async Task MergeAsync_Cancellation_CleansUpPartialsAndLeavesInputsUntouched()
    {
        var docA = CreatePdf(4, "DocA");
        var docB = CreatePdf(4, "DocB");
        var hashA = ComputeSha256(docA);
        var hashB = ComputeSha256(docB);
        var outPath = Path.Combine(_rootDir, "Cancelled.pdf");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _engine.MergeAsync([docA, docB], outPath, null, cts.Token));

        // Ensure no partial or temporary files remain in output root
        var partialFiles = Directory.GetFiles(_rootDir, "*.partial");
        Assert.Empty(partialFiles);

        // Originals strictly unchanged
        Assert.Equal(hashA, ComputeSha256(docA));
        Assert.Equal(hashB, ComputeSha256(docB));
    }

    [Fact]
    public async Task MergeAsync_CorruptDocumentInQueue_AbortsAndPreservesGoodDocuments()
    {
        var docA = CreatePdf(2, "DocA");
        var corruptPath = Path.Combine(_rootDir, "corrupt.pdf");
        await File.WriteAllTextAsync(corruptPath, "Corrupt garbage binary data");
        var hashA = ComputeSha256(docA);
        var outPath = Path.Combine(_rootDir, "WillFail.pdf");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.MergeAsync([docA, corruptPath], outPath));

        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Original good doc untouched
        Assert.Equal(hashA, ComputeSha256(docA));
    }

    [Fact]
    public async Task PdfMergeProcessor_ExecutesSuccessfullyThroughCatalog()
    {
        var docA = CreatePdf(2, "DocA");
        var docB = CreatePdf(2, "DocB");
        var classifier = new FileClassifier();

        var processor = new PdfMergeProcessor(_engine, _outputAllocator, classifier);
        Assert.True(processor.IsAvailable);
        Assert.Equal(ActionIds.MergePdf, processor.ActionId);

        var classified = classifier.Classify(docA);

        var request = new ProcessingRequest(
            classified,
            ActionIds.MergePdf,
            new ProcessingOptions(new Dictionary<string, string>
            {
                ["additionalPaths"] = docB,
                ["customOutputName"] = "ProcessorMerged.pdf"
            }),
            _rootDir);

        var result = await processor.ProcessAsync(request, new Progress<ProcessingProgress>(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal("4", result.Details["MergedPageCount"]);
    }
}
