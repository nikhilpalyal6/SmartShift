using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Pdf.Preview;

public sealed record PdfPreviewDocumentInfo(
    int TotalPages,
    double PageWidthPt,
    double PageHeightPt,
    bool IsEncrypted,
    string? ErrorMessage);

public sealed record PdfPreviewPageData(
    int PageIndex,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    byte[] BgraBytes,
    double PageWidthPt,
    double PageHeightPt);

public static class PdfPreviewRenderer
{
    private static readonly SemaphoreSlim NativeLock = new(1, 1);
    private const int MaxDimensionPixels = 4096;

    public static async Task<PdfPreviewDocumentInfo> GetDocumentInfoAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            return new PdfPreviewDocumentInfo(0, 0, 0, false, "The PDF file was not found.");
        }

        if (new FileInfo(filePath).Length == 0)
        {
            return new PdfPreviewDocumentInfo(0, 0, 0, false, "The PDF file is empty (0 bytes).");
        }

        await NativeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1.0));
            int pageCount = docReader.GetPageCount();
            if (pageCount <= 0)
            {
                return new PdfPreviewDocumentInfo(0, 0, 0, false, "The PDF contains no displayable pages.");
            }

            using var firstPageReader = docReader.GetPageReader(0);
            double widthPt = firstPageReader.GetPageWidth();
            double heightPt = firstPageReader.GetPageHeight();

            return new PdfPreviewDocumentInfo(pageCount, widthPt, heightPt, false, null);
        }
        catch (DocnetLoadDocumentException)
        {
            return new PdfPreviewDocumentInfo(0, 0, 0, true, "This PDF is password-protected or uses an unsupported encryption handler.");
        }
        catch (Exception ex)
        {
            return new PdfPreviewDocumentInfo(0, 0, 0, false, $"Could not load PDF: {ex.Message}");
        }
        finally
        {
            NativeLock.Release();
        }
    }

    public static async Task<PdfPreviewPageData> RenderPageAsync(
        string filePath,
        int pageIndex,
        double renderDpi,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The PDF file was not found.", filePath);
        }

        if (renderDpi < 36.0) renderDpi = 36.0;
        if (renderDpi > 400.0) renderDpi = 400.0;

        double scale = renderDpi / 72.0;

        await NativeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(scale));
            int totalPages = docReader.GetPageCount();
            if (pageIndex < 0 || pageIndex >= totalPages)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of bounds (0..{totalPages - 1}).");
            }

            using var pageReader = docReader.GetPageReader(pageIndex);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();

            // Clamp if exceeds safety threshold
            if (width > MaxDimensionPixels || height > MaxDimensionPixels)
            {
                double downscale = Math.Min((double)MaxDimensionPixels / width, (double)MaxDimensionPixels / height);
                scale *= downscale;
                using var downscaledDoc = DocLib.Instance.GetDocReader(filePath, new PageDimensions(scale));
                using var downscaledPage = downscaledDoc.GetPageReader(pageIndex);
                width = downscaledPage.GetPageWidth();
                height = downscaledPage.GetPageHeight();
                var downscaledBytes = downscaledPage.GetImage();
                return ProcessPageBytes(pageIndex, width, height, downscaledBytes, width / scale, height / scale);
            }

            var rawBgra = pageReader.GetImage();
            double originalWidthPt = width / scale;
            double originalHeightPt = height / scale;

            return ProcessPageBytes(pageIndex, width, height, rawBgra, originalWidthPt, originalHeightPt);
        }
        finally
        {
            NativeLock.Release();
        }
    }

    private static PdfPreviewPageData ProcessPageBytes(
        int pageIndex,
        int width,
        int height,
        byte[] rawBgra,
        double widthPt,
        double heightPt)
    {
        if (rawBgra == null || rawBgra.Length == 0)
        {
            throw new InvalidOperationException($"PDFium failed to render page {pageIndex + 1}.");
        }

        // Composite over solid white paper background in place
        for (int i = 0; i < rawBgra.Length; i += 4)
        {
            byte a = rawBgra[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                rawBgra[i] = 255;     // B
                rawBgra[i + 1] = 255; // G
                rawBgra[i + 2] = 255; // R
                rawBgra[i + 3] = 255; // A
            }
            else
            {
                int invA = 255 - a;
                rawBgra[i] = (byte)((rawBgra[i] * a + 255 * invA) / 255);
                rawBgra[i + 1] = (byte)((rawBgra[i + 1] * a + 255 * invA) / 255);
                rawBgra[i + 2] = (byte)((rawBgra[i + 2] * a + 255 * invA) / 255);
                rawBgra[i + 3] = 255;
            }
        }

        int stride = width * 4;
        return new PdfPreviewPageData(
            pageIndex,
            width,
            height,
            stride,
            rawBgra,
            widthPt,
            heightPt);
    }
}
