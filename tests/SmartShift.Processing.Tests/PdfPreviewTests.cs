using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SmartShift.Processing.Pdf.Preview;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfPreviewTests : IDisposable
{
    private readonly string _rootDir;

    public PdfPreviewTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-preview-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
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

    private string CreatePdf(Action<PdfDocument> setup, string? name = null)
    {
        var path = Path.Combine(_rootDir, name ?? $"preview-test-{Guid.NewGuid():N}.pdf");
        using var doc = new PdfDocument();
        setup(doc);
        doc.Save(path);
        return path;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // ==========================================
    // 1. DOCUMENT INFO EXTRACTION
    // ==========================================

    [Fact]
    public async Task GetDocumentInfo_SinglePagePdf_ExtractsAccurateMetrics()
    {
        var path = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("Sample Single Page", new XFont("Arial", 12, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
        });

        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(path);

        Assert.Equal(1, info.TotalPages);
        Assert.Equal(612.0, info.PageWidthPt, 1);
        Assert.Equal(792.0, info.PageHeightPt, 1);
        Assert.False(info.IsEncrypted);
        Assert.Null(info.ErrorMessage);
    }

    [Fact]
    public async Task GetDocumentInfo_MultiPagePdf_ReturnsTotalPageCount()
    {
        var path = CreatePdf(doc =>
        {
            for (int i = 0; i < 5; i++)
            {
                var p = doc.AddPage();
                p.Width = XUnit.FromPoint(500);
                p.Height = XUnit.FromPoint(700);
                using var gfx = XGraphics.FromPdfPage(p);
                gfx.DrawString($"Page {i + 1}", new XFont("Arial", 14, XFontStyleEx.Bold), XBrushes.DarkBlue, new XPoint(40, 40));
            }
        });

        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(path);

        Assert.Equal(5, info.TotalPages);
        Assert.Equal(500.0, info.PageWidthPt, 1);
        Assert.Equal(700.0, info.PageHeightPt, 1);
        Assert.False(info.IsEncrypted);
        Assert.Null(info.ErrorMessage);
    }

    [Fact]
    public async Task GetDocumentInfo_PasswordProtectedPdf_DetectsEncryption()
    {
        var path = CreatePdf(doc =>
        {
            doc.AddPage();
            doc.SecuritySettings.UserPassword = "SecretUserPassword123";
        });

        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(path);

        Assert.Equal(0, info.TotalPages);
        Assert.True(info.IsEncrypted);
        Assert.NotNull(info.ErrorMessage);
        Assert.Contains("password", info.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDocumentInfo_MissingFile_ReturnsFriendlyError()
    {
        var missingPath = Path.Combine(_rootDir, "non_existent_file.pdf");
        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(missingPath);

        Assert.Equal(0, info.TotalPages);
        Assert.False(info.IsEncrypted);
        Assert.NotNull(info.ErrorMessage);
        Assert.Contains("not found", info.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDocumentInfo_EmptyZeroByteFile_ReturnsFriendlyError()
    {
        var emptyPath = Path.Combine(_rootDir, "empty.pdf");
        await File.WriteAllBytesAsync(emptyPath, Array.Empty<byte>());

        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(emptyPath);

        Assert.Equal(0, info.TotalPages);
        Assert.False(info.IsEncrypted);
        Assert.NotNull(info.ErrorMessage);
        Assert.Contains("0 bytes", info.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDocumentInfo_CorruptedPdf_ReturnsFriendlyError()
    {
        var corruptPath = Path.Combine(_rootDir, "corrupt.pdf");
        await File.WriteAllBytesAsync(corruptPath, [0x25, 0x50, 0x44, 0x46, 0x2D, 0xFF, 0x00, 0x12, 0x34]);

        var info = await PdfPreviewRenderer.GetDocumentInfoAsync(corruptPath);

        Assert.Equal(0, info.TotalPages);
        Assert.NotNull(info.ErrorMessage);
    }

    // ==========================================
    // 2. PAGE RENDERING AT MULTIPLE DPIS
    // ==========================================

    [Theory]
    [InlineData(72.0)]
    [InlineData(144.0)]
    [InlineData(200.0)]
    public async Task RenderPageAsync_ScalesDimensionsAccuratelyToRequestedDpi(double dpi)
    {
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            p.Width = XUnit.FromPoint(300);
            p.Height = XUnit.FromPoint(400);
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawRectangle(XPens.Black, XBrushes.LightGray, 10, 10, 280, 380);
        });

        var pageData = await PdfPreviewRenderer.RenderPageAsync(path, pageIndex: 0, renderDpi: dpi);

        int expectedW = (int)Math.Round(300.0 * dpi / 72.0);
        int expectedH = (int)Math.Round(400.0 * dpi / 72.0);

        Assert.Equal(0, pageData.PageIndex);
        Assert.Equal(expectedW, pageData.PixelWidth);
        Assert.Equal(expectedH, pageData.PixelHeight);
        Assert.Equal(expectedW * 4, pageData.Stride);
        Assert.Equal(expectedW * 4 * expectedH, pageData.BgraBytes.Length);
    }

    [Fact]
    public async Task RenderPageAsync_CompositesWhiteBackground_NoBlackAlphaBleed()
    {
        // Transparent PDF canvas should composite onto solid white paper (A=255, R=255, G=255, B=255)
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            p.Width = XUnit.FromPoint(200);
            p.Height = XUnit.FromPoint(200);
            // Intentionally no background drawn
        });

        var pageData = await PdfPreviewRenderer.RenderPageAsync(path, pageIndex: 0, renderDpi: 72.0);

        // Check top-left corner pixel (Bgra32 format: B, G, R, A)
        byte b = pageData.BgraBytes[0];
        byte g = pageData.BgraBytes[1];
        byte r = pageData.BgraBytes[2];
        byte a = pageData.BgraBytes[3];

        Assert.Equal(255, b);
        Assert.Equal(255, g);
        Assert.Equal(255, r);
        Assert.Equal(255, a);
    }

    [Fact]
    public async Task RenderPageAsync_InvalidPageIndex_ThrowsArgumentOutOfRangeException()
    {
        var path = CreatePdf(doc => doc.AddPage());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PdfPreviewRenderer.RenderPageAsync(path, pageIndex: -1, renderDpi: 96.0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PdfPreviewRenderer.RenderPageAsync(path, pageIndex: 5, renderDpi: 96.0));
    }

    [Fact]
    public async Task RenderPageAsync_HonoursCancellationToken()
    {
        var path = CreatePdf(doc => doc.AddPage());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PdfPreviewRenderer.RenderPageAsync(path, pageIndex: 0, renderDpi: 96.0, cancellationToken: cts.Token));
    }

    // ==========================================
    // 3. SOURCE FILE IMMUTABILITY (SHA-256)
    // ==========================================

    [Fact]
    public async Task PdfPreview_StrictlyPreservesSourceFileImmutability()
    {
        var path = CreatePdf(doc =>
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString("Immutability Test", new XFont("Arial", 16, XFontStyleEx.Regular), XBrushes.Navy, new XPoint(20, 40));
        });

        var initialHash = ComputeSha256(path);

        // Perform document inspect
        var docInfo = await PdfPreviewRenderer.GetDocumentInfoAsync(path);
        Assert.Equal(1, docInfo.TotalPages);

        // Perform multiple page renders at different DPIs
        await PdfPreviewRenderer.RenderPageAsync(path, 0, renderDpi: 72.0);
        await PdfPreviewRenderer.RenderPageAsync(path, 0, renderDpi: 150.0);
        await PdfPreviewRenderer.RenderPageAsync(path, 0, renderDpi: 300.0);

        var finalHash = ComputeSha256(path);

        Assert.Equal(initialHash, finalHash);
    }

    // ==========================================
    // 4. PREVIEW CACHE HIT, MISS, & LRU EVICTION
    // ==========================================

    [Fact]
    public void PdfPreviewCache_HitMissAndLruEviction_WorkCorrectly()
    {
        var cache = new PdfPreviewCache(maxEntries: 2);
        var page1 = new PdfPreviewPageData(0, 100, 100, 400, new byte[40000], 100, 100);
        var page2 = new PdfPreviewPageData(1, 100, 100, 400, new byte[40000], 100, 100);
        var page3 = new PdfPreviewPageData(2, 100, 100, 400, new byte[40000], 100, 100);

        // Initially miss
        Assert.False(cache.TryGet("doc.pdf", 0, 96, out var missed));
        Assert.Null(missed);

        // Populate entries 0 and 1
        cache.Set("doc.pdf", 0, 96, page1);
        cache.Set("doc.pdf", 1, 96, page2);

        // Cache hit
        Assert.True(cache.TryGet("doc.pdf", 0, 96, out var hit1));
        Assert.Same(page1, hit1);

        // Accessing page 0 made page 1 the LRU!
        // Adding page 3 should now evict page 1, keeping page 0 and page 3
        cache.Set("doc.pdf", 2, 96, page3);

        // Page 1 must now be evicted (miss)
        Assert.False(cache.TryGet("doc.pdf", 1, 96, out _));

        // Page 0 and Page 2 must remain in cache (hit)
        Assert.True(cache.TryGet("doc.pdf", 0, 96, out var hit0));
        Assert.Same(page1, hit0);
        Assert.True(cache.TryGet("doc.pdf", 2, 96, out var hit2));
        Assert.Same(page3, hit2);
    }

    [Fact]
    public void PdfPreviewCache_Clear_RemovesAllEntries()
    {
        var cache = new PdfPreviewCache(maxEntries: 3);
        var page = new PdfPreviewPageData(0, 50, 50, 200, new byte[10000], 50, 50);

        cache.Set("doc.pdf", 0, 96, page);
        Assert.True(cache.TryGet("doc.pdf", 0, 96, out _));

        cache.Clear();
        Assert.False(cache.TryGet("doc.pdf", 0, 96, out _));
    }
}
