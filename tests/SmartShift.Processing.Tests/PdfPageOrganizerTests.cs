using System.IO;
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

public sealed class PdfPageOrganizerTests : IDisposable
{
    private readonly string _rootDir;
    private readonly OutputPathAllocator _allocator;
    private readonly PdfPageOrganizerEngine _engine;
    private readonly FileClassifier _classifier;
    private readonly PdfOrganizeProcessor _processor;

    public PdfPageOrganizerTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-organizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _allocator = new OutputPathAllocator(Path.Combine(_rootDir, "Output"));
        _engine = new PdfPageOrganizerEngine(_allocator);
        _classifier = new FileClassifier();
        _processor = new PdfOrganizeProcessor(_engine, _allocator, _classifier);
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

    private string CreatePdf(
        int pageCount,
        string label = "Doc",
        string? fileName = null,
        double width = 595.0,
        double height = 842.0,
        int initialRotation = 0)
    {
        var path = Path.Combine(_rootDir, fileName ?? $"{label}_{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pageCount; i++)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);
                page.Rotate = initialRotation;
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

    // =========================================================================
    // 1. VALIDATION TESTS
    // =========================================================================

    [Fact]
    public async Task ValidateSourceAsync_ValidPdf_ReturnsExpectedProperties()
    {
        var pdfPath = CreatePdf(4, "Test");
        var validation = await _engine.ValidateSourceAsync(pdfPath);

        Assert.True(validation.IsValid);
        Assert.False(validation.IsEncrypted);
        Assert.Equal(4, validation.TotalPages);
        Assert.Null(validation.ErrorReason);
        Assert.NotNull(validation.PageRotations);
        Assert.Equal(4, validation.PageRotations.Count);
        Assert.NotNull(validation.PageDimensions);
        Assert.Equal(4, validation.PageDimensions.Count);
    }

    [Fact]
    public async Task ValidateSourceAsync_CorruptPdf_ReturnsInvalid()
    {
        var corruptPath = Path.Combine(_rootDir, "corrupt.pdf");
        File.WriteAllText(corruptPath, "Definitely not a valid PDF header content");

        var validation = await _engine.ValidateSourceAsync(corruptPath);
        Assert.False(validation.IsValid);
        Assert.NotNull(validation.ErrorReason);
    }

    [Fact]
    public async Task ValidateSourceAsync_EmptyPdf_ReturnsInvalid()
    {
        var emptyPath = Path.Combine(_rootDir, "empty.pdf");
        File.WriteAllBytes(emptyPath, []);

        var validation = await _engine.ValidateSourceAsync(emptyPath);
        Assert.False(validation.IsValid);
        Assert.Contains("empty", validation.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSourceAsync_NonExistentFile_ReturnsInvalid()
    {
        var missingPath = Path.Combine(_rootDir, "non_existent.pdf");
        var validation = await _engine.ValidateSourceAsync(missingPath);

        Assert.False(validation.IsValid);
        Assert.Contains("does not exist", validation.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 2. PAGE-ORDER TESTS
    // =========================================================================

    [Fact]
    public async Task OrganizeAsync_PreservesDefaultOrder_WhenTransformsAreSequential()
    {
        var pdfPath = CreatePdf(3, "Sequential");
        var transforms = new List<PdfPageTransform>
        {
            new(0, 0),
            new(1, 0),
            new(2, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.OutputPageCount);
        Assert.True(File.Exists(result.OutputPath));

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public async Task OrganizeAsync_ReordersPages_CorrectlyAppliesNewSequence()
    {
        // Source pages: 1, 2, 3, 4, 5
        // Reordered: 3, 1, 2, 5, 4 (0-based: 2, 0, 1, 4, 3)
        var pdfPath = CreatePdf(5, "ReorderDoc");
        var transforms = new List<PdfPageTransform>
        {
            new(2, 0),
            new(0, 0),
            new(1, 0),
            new(4, 0),
            new(3, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.OutputPageCount);

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(5, verifyDoc.PageCount);
    }

    [Fact]
    public async Task OrganizeAsync_MovePageToFirstAndLastPositions()
    {
        var pdfPath = CreatePdf(4, "MovePositions");
        // Move page 4 (idx 3) to first position, and page 1 (idx 0) to last: 3, 1, 2, 0
        var transforms = new List<PdfPageTransform>
        {
            new(3, 0),
            new(1, 0),
            new(2, 0),
            new(0, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.OutputPageCount);
    }

    [Fact]
    public async Task OrganizeAsync_RejectsInvalidPageIndices()
    {
        var pdfPath = CreatePdf(3, "InvalidIndices");
        var transforms = new List<PdfPageTransform>
        {
            new(0, 0),
            new(99, 0) // Out of bounds
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("out of bounds", result.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 3. ROTATION TESTS
    // =========================================================================

    [Fact]
    public async Task OrganizeAsync_RotatesPagesClockwiseAndCounterClockwise()
    {
        var pdfPath = CreatePdf(3, "RotationDoc");
        var transforms = new List<PdfPageTransform>
        {
            new(0, 90),   // 90° CW
            new(1, 180),  // 180°
            new(2, 270)   // 270° CW (90° CCW)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.OutputPageCount);

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(90, verifyDoc.Pages[0].Rotate);
        Assert.Equal(180, verifyDoc.Pages[1].Rotate);
        Assert.Equal(270, verifyDoc.Pages[2].Rotate);
    }

    [Fact]
    public async Task OrganizeAsync_PreservesExistingPageRotation()
    {
        // PDF with inherent 90° rotation
        var pdfPath = CreatePdf(2, "InherentRotation", initialRotation: 90);
        var transforms = new List<PdfPageTransform>
        {
            new(0, 90),  // Inherent 90 + 90 = 180
            new(1, 180)  // Inherent 90 + 180 = 270
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(180, verifyDoc.Pages[0].Rotate);
        Assert.Equal(270, verifyDoc.Pages[1].Rotate);
    }

    [Fact]
    public async Task OrganizeAsync_RejectsInvalidRotationAngles()
    {
        var pdfPath = CreatePdf(2, "InvalidRotation");
        var transforms = new List<PdfPageTransform>
        {
            new(0, 45) // 45° is not a multiple of 90
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("multiple of 90", result.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 4. DELETION TESTS
    // =========================================================================

    [Fact]
    public async Task OrganizeAsync_DeletesSinglePage()
    {
        var pdfPath = CreatePdf(4, "DeleteSingle");
        // Delete page 2 (idx 1): keep 0, 2, 3
        var transforms = new List<PdfPageTransform>
        {
            new(0, 0),
            new(2, 0),
            new(3, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.OutputPageCount);

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public async Task OrganizeAsync_DeletesFirstAndLastPages()
    {
        var pdfPath = CreatePdf(5, "DeleteFirstLast");
        // Keep only middle pages: 1, 2, 3
        var transforms = new List<PdfPageTransform>
        {
            new(1, 0),
            new(2, 0),
            new(3, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.OutputPageCount);

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public async Task OrganizeAsync_RejectsDeletingAllPages()
    {
        var pdfPath = CreatePdf(3, "DeleteAll");
        var transforms = new List<PdfPageTransform>(); // Empty

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("at least one page", result.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 5. COMBINED TRANSFORMATION TESTS
    // =========================================================================

    [Fact]
    public async Task OrganizeAsync_CombinedReorderRotateAndDelete_SucceedsWithExactState()
    {
        // 6-page document: 0, 1, 2, 3, 4, 5
        // Reorder + Rotate + Delete:
        // Delete page 1 (idx 0) and page 4 (idx 3)
        // Keep: page 5 (idx 4) with 90° CW, page 2 (idx 1) with 180°, page 6 (idx 5) with 0°, page 3 (idx 2) with 270°
        var pdfPath = CreatePdf(6, "CombinedWorkflow");
        var transforms = new List<PdfPageTransform>
        {
            new(4, 90),
            new(1, 180),
            new(5, 0),
            new(2, 270)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.OutputPageCount);

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(4, verifyDoc.PageCount);
        Assert.Equal(90, verifyDoc.Pages[0].Rotate);
        Assert.Equal(180, verifyDoc.Pages[1].Rotate);
        Assert.Equal(0, verifyDoc.Pages[2].Rotate);
        Assert.Equal(270, verifyDoc.Pages[3].Rotate);
    }

    // =========================================================================
    // 6. SOURCE INTEGRITY & OUTPUT VALIDATION TESTS
    // =========================================================================

    [Fact]
    public async Task OrganizeAsync_LeavesOriginalPdfUntouched_HashMatches()
    {
        var pdfPath = CreatePdf(4, "HashIntegrityDoc");
        string originalHash = ComputeSha256(pdfPath);

        var transforms = new List<PdfPageTransform>
        {
            new(2, 90),
            new(0, 180),
            new(1, 0)
        };

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);

        // Verify original file's hash before and after
        string postHash = ComputeSha256(pdfPath);
        Assert.Equal(originalHash, postHash);
    }

    [Fact]
    public async Task OrganizeAsync_CollisionHandling_AllocatesNumberedFile()
    {
        var pdfPath = CreatePdf(2, "DocCollision", fileName: "DocCollision.pdf");
        var transforms = new List<PdfPageTransform> { new(0, 0), new(1, 0) };

        // Pre-create the target file
        string targetPath = Path.Combine(_allocator.RootDirectory, "DocCollision_organized.pdf");
        Directory.CreateDirectory(_allocator.RootDirectory);
        File.WriteAllText(targetPath, "Pre-existing content");

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotEqual(targetPath, result.OutputPath);
        Assert.Contains("DocCollision_organized (1).pdf", result.OutputPath);
        Assert.Equal("Pre-existing content", File.ReadAllText(targetPath)); // Pre-existing file untouched!
    }

    [Fact]
    public async Task OrganizeAsync_Cancellation_CleansUpPartialsAndReportsCancelled()
    {
        var pdfPath = CreatePdf(10, "CancelDoc");
        var transforms = new List<PdfPageTransform>();
        for (int i = 0; i < 10; i++) transforms.Add(new(i, 0));

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var request = new PdfOrganizeRequest(pdfPath, _allocator.RootDirectory, transforms);
        var result = await _engine.OrganizeAsync(request, cancellationToken: cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.WasCancelled);

        // Verify no leftover partial files in output directory
        if (Directory.Exists(_allocator.RootDirectory))
        {
            var partialFiles = Directory.GetFiles(_allocator.RootDirectory, "*.partial");
            Assert.Empty(partialFiles);
        }
    }

    // =========================================================================
    // 7. PROCESSOR INTEGRATION TESTS
    // =========================================================================

    [Fact]
    public async Task Processor_OrganizePdf_ExecutesWithOrderOption()
    {
        var pdfPath = CreatePdf(4, "ProcessorDoc");
        var request = new ProcessingRequest(
            _classifier.Classify(pdfPath),
            ActionIds.OrganizePdf,
            new ProcessingOptions(new Dictionary<string, string>
            {
                ["order"] = "3,1:90,4:180" // 1-based page numbers with rotations
            }),
            _rootDir);

        var result = await _processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        using var verifyDoc = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, verifyDoc.PageCount);
        Assert.Equal(0, verifyDoc.Pages[0].Rotate);
        Assert.Equal(90, verifyDoc.Pages[1].Rotate);
        Assert.Equal(180, verifyDoc.Pages[2].Rotate);
    }
}
