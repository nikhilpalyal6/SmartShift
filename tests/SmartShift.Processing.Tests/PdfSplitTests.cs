using System.IO;
using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Pdf;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfSplitTests : IDisposable
{
    private readonly string _rootDir;
    private readonly OutputPathAllocator _allocator;
    private readonly PdfSplitEngine _splitEngine;

    public PdfSplitTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-pdfsplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _allocator = new OutputPathAllocator(Path.Combine(_rootDir, "Output"));
        _splitEngine = new PdfSplitEngine(_allocator);
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

    private string CreatePdf(int pageCount, string label, string? fileName = null, double width = 595.0, double height = 842.0)
    {
        var path = Path.Combine(_rootDir, fileName ?? $"{label}_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pageCount; i++)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);
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
    public async Task ValidateSourceAsync_ValidPdf_ReturnsValidStatusAndPageCount()
    {
        var pdfPath = CreatePdf(5, "Doc");
        var validation = await _splitEngine.ValidateSourceAsync(pdfPath);

        Assert.True(validation.IsValid);
        Assert.False(validation.IsEncrypted);
        Assert.Equal(5, validation.TotalPages);
        Assert.Null(validation.ErrorReason);
    }

    [Fact]
    public async Task ValidateSourceAsync_CorruptPdf_ReturnsInvalidWithDescriptiveError()
    {
        var corruptPath = Path.Combine(_rootDir, "corrupt.pdf");
        File.WriteAllText(corruptPath, "Not a pdf header at all");

        var validation = await _splitEngine.ValidateSourceAsync(corruptPath);
        Assert.False(validation.IsValid);
        Assert.NotNull(validation.ErrorReason);
    }

    [Fact]
    public async Task ValidateSourceAsync_EmptyPdf_ReturnsInvalid()
    {
        var emptyPath = Path.Combine(_rootDir, "empty.pdf");
        File.WriteAllBytes(emptyPath, []);

        var validation = await _splitEngine.ValidateSourceAsync(emptyPath);
        Assert.False(validation.IsValid);
        Assert.Contains("empty", validation.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractRanges_SingleRange_GeneratesCorrectPageCountAndPreservesDimensions()
    {
        var sourcePdf = CreatePdf(10, "Report", "report.pdf", width: 612.0, height: 792.0);
        string beforeHash = ComputeSha256(sourcePdf);

        bool parsed = PdfPageRangeParser.TryParseRanges("2-4", 10, out var ranges, out var error);
        Assert.True(parsed);

        var request = new PdfSplitRequest(
            sourcePdf,
            Path.Combine(_rootDir, "OutRanges"),
            PdfSplitMode.ExtractRanges,
            ranges);

        var result = await _splitEngine.SplitAsync(request);

        Assert.True(result.Succeeded);
        Assert.False(result.WasCancelled);
        Assert.Single(result.GeneratedFiles);

        var outputFile = result.GeneratedFiles[0].OutputPath;
        Assert.True(File.Exists(outputFile));
        Assert.Contains("report_pages_002-004.pdf", Path.GetFileName(outputFile));

        using (var verifyDoc = PdfReader.Open(outputFile, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(3, verifyDoc.PageCount);
            Assert.Equal(612.0, verifyDoc.Pages[0].Width.Point, 1.0);
            Assert.Equal(792.0, verifyDoc.Pages[0].Height.Point, 1.0);
        }

        string afterHash = ComputeSha256(sourcePdf);
        Assert.Equal(beforeHash, afterHash);
    }

    [Fact]
    public async Task ExtractRanges_MultipleRanges_GeneratesSeparateFilesWithAccurateOrdering()
    {
        var sourcePdf = CreatePdf(12, "Multi", "document.pdf");
        string beforeHash = ComputeSha256(sourcePdf);

        bool parsed = PdfPageRangeParser.TryParseRanges("1-3, 5, 8-10", 12, out var ranges, out var error);
        Assert.True(parsed);
        Assert.Equal(3, ranges.Count);

        var request = new PdfSplitRequest(
            sourcePdf,
            Path.Combine(_rootDir, "OutMulti"),
            PdfSplitMode.ExtractRanges,
            ranges);

        var result = await _splitEngine.SplitAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.GeneratedFiles.Count);

        // Verify each generated file
        var file1 = result.GeneratedFiles[0].OutputPath;
        var file2 = result.GeneratedFiles[1].OutputPath;
        var file3 = result.GeneratedFiles[2].OutputPath;

        Assert.Contains("document_pages_001-003.pdf", Path.GetFileName(file1));
        Assert.Contains("document_page_005.pdf", Path.GetFileName(file2));
        Assert.Contains("document_pages_008-010.pdf", Path.GetFileName(file3));

        using (var doc1 = PdfReader.Open(file1, PdfDocumentOpenMode.Import))
            Assert.Equal(3, doc1.PageCount);

        using (var doc2 = PdfReader.Open(file2, PdfDocumentOpenMode.Import))
            Assert.Equal(1, doc2.PageCount);

        using (var doc3 = PdfReader.Open(file3, PdfDocumentOpenMode.Import))
            Assert.Equal(3, doc3.PageCount);

        Assert.Equal(beforeHash, ComputeSha256(sourcePdf));
    }

    [Fact]
    public async Task SplitEveryPage_ProducesExactOnePagePdfsForEachSourcePage()
    {
        var sourcePdf = CreatePdf(4, "Invoice", "invoice.pdf", width: 500.0, height: 700.0);
        string beforeHash = ComputeSha256(sourcePdf);

        var request = new PdfSplitRequest(
            sourcePdf,
            Path.Combine(_rootDir, "OutEvery"),
            PdfSplitMode.SplitEveryPage);

        var result = await _splitEngine.SplitAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.GeneratedFiles.Count);
        Assert.Equal(4, result.TotalSourcePages);

        for (int i = 0; i < 4; i++)
        {
            var file = result.GeneratedFiles[i].OutputPath;
            Assert.True(File.Exists(file));
            int pageNum = i + 1;
            Assert.Contains($"invoice_page_{pageNum:D3}.pdf", Path.GetFileName(file));

            using var doc = PdfReader.Open(file, PdfDocumentOpenMode.Import);
            Assert.Equal(1, doc.PageCount);
            Assert.Equal(500.0, doc.Pages[0].Width.Point, 1.0);
            Assert.Equal(700.0, doc.Pages[0].Height.Point, 1.0);
        }

        Assert.Equal(beforeHash, ComputeSha256(sourcePdf));
    }

    [Fact]
    public async Task Split_HandlesCollisionsSafely_WithoutOverwritingExistingFiles()
    {
        var sourcePdf = CreatePdf(2, "Test", "collision.pdf");
        var outDir = Path.Combine(_rootDir, "OutCollision");
        Directory.CreateDirectory(outDir);

        // Pre-create file that will collide with page 1
        var existingFile = Path.Combine(outDir, "collision_page_001.pdf");
        File.WriteAllText(existingFile, "Pre-existing content");

        var request = new PdfSplitRequest(
            sourcePdf,
            outDir,
            PdfSplitMode.SplitEveryPage);

        var result = await _splitEngine.SplitAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.GeneratedFiles.Count);

        // Pre-existing file content must NOT be overwritten
        Assert.Equal("Pre-existing content", File.ReadAllText(existingFile));

        // The newly generated page 1 must have collision suffix
        var page1Output = result.GeneratedFiles[0].OutputPath;
        Assert.Contains("collision_page_001 (1).pdf", Path.GetFileName(page1Output));
        Assert.True(File.Exists(page1Output));

        using var doc = PdfReader.Open(page1Output, PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task Split_Cancellation_CleansUpPartialsAndRollsBackNewOutputs()
    {
        var sourcePdf = CreatePdf(5, "CancelTest", "cancel.pdf");
        string beforeHash = ComputeSha256(sourcePdf);
        var outDir = Path.Combine(_rootDir, "OutCancel");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var request = new PdfSplitRequest(
            sourcePdf,
            outDir,
            PdfSplitMode.SplitEveryPage);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _splitEngine.SplitAsync(request, cancellationToken: cts.Token);
        });

        Assert.Equal(beforeHash, ComputeSha256(sourcePdf));

        // Ensure no partial or output files remain in output directory
        if (Directory.Exists(outDir))
        {
            var files = Directory.GetFiles(outDir);
            Assert.Empty(files);
        }
    }

    [Fact]
    public async Task PdfSplitProcessor_ExecutesSuccessfullyThroughProcessorCatalog()
    {
        var sourcePdf = CreatePdf(6, "Catalog", "catalog_test.pdf");
        var classifier = new FileClassifier();
        var catalog = new ProcessorCatalog(classifier, _allocator);

        var processor = catalog.Find(ActionIds.SplitPdf);
        Assert.NotNull(processor);
        Assert.True(processor.IsAvailable);

        var classified = classifier.Classify(sourcePdf);
        var request = new ProcessingRequest(
            classified,
            ActionIds.SplitPdf,
            new ProcessingOptions(new Dictionary<string, string>
            {
                ["mode"] = "extract",
                ["range"] = "1-2, 5"
            }),
            _allocator.RootDirectory);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.WasCancelled);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal("2", result.Details["FilesGenerated"]);
    }
}
