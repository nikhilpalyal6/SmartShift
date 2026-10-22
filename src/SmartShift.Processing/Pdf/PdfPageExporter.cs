using System.Runtime.InteropServices;
using System.Text;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using SkiaSharp;
using SmartShift.Core.IO;

namespace SmartShift.Processing.Pdf;

public enum ExportImageFormat
{
    Png,
    Jpeg
}

public sealed record PdfExportResult(
    IReadOnlyList<string> ExportedFiles,
    int TotalPagesInDoc,
    int ExportedCount,
    long TotalBytesWritten,
    ExportImageFormat Format,
    int Dpi,
    string SummaryNote);

public static class PdfPageExporter
{
    public static PdfExportResult ExportPages(
        string inputPath,
        IOutputPathAllocator allocator,
        ExportImageFormat format,
        int dpi,
        int jpegQuality,
        string? pageRange,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The source PDF file was not found.", inputPath);
        }

        if (new FileInfo(inputPath).Length == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        dpi = dpi switch
        {
            100 => 100,
            300 => 300,
            _ => 150
        };

        jpegQuality = Math.Clamp(jpegQuality, 1, 100);

        // Standard PDF resolution is 72 points per inch
        double scale = dpi / 72.0;
        var dimensions = new PageDimensions(scale);

        IDocReader docReader;
        try
        {
            docReader = DocLib.Instance.GetDocReader(inputPath, dimensions);
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw new InvalidOperationException(
                "This PDF document is password-protected or uses an unsupported encryption handler. Encrypted PDFs cannot be exported.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The PDF document is malformed or corrupted and could not be rendered.",
                ex);
        }

        using (docReader)
        {
            int totalPages = docReader.GetPageCount();
            if (totalPages == 0)
            {
                throw new InvalidOperationException("The PDF document contains no pages.");
            }

            var pagesToExport = PageRangeParser.Parse(pageRange, totalPages);
            if (pagesToExport.Count == 0)
            {
                throw new InvalidOperationException("No valid pages were selected for export.");
            }

            allocator.EnsureRoot();
            var baseName = Path.GetFileNameWithoutExtension(inputPath);
            var ext = format == ExportImageFormat.Jpeg ? ".jpg" : ".png";
            var exportedFiles = new List<string>();
            var tempPartials = new List<string>();

            try
            {
                int count = pagesToExport.Count;
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageNum = pagesToExport[i];
                    int percent = (int)Math.Round(((i + 1) / (double)count) * 100.0);
                    progress?.Report(new ProcessingProgress(percent, $"Rendering page {pageNum} of {totalPages} ({i + 1}/{count})…"));

                    int pageIndex = pageNum - 1;
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();

                    if (ImageSafetyLimits.Exceeds(width, height))
                    {
                        throw new InvalidOperationException(
                            $"Rendered page {pageNum} dimensions ({width} × {height} px) exceed application safety limits.");
                    }

                    byte[] rawBgra = pageReader.GetImage();
                    if (rawBgra == null || rawBgra.Length == 0)
                    {
                        throw new InvalidOperationException($"Failed to render page {pageNum}.");
                    }

                    var desiredName = $"{baseName}-page-{pageNum:D3}{ext}";
                    var finalPath = allocator.Allocate(inputPath, desiredName);
                    var tempPartial = finalPath + ".partial";
                    tempPartials.Add(tempPartial);

                    var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var bitmap = new SKBitmap(info);
                    Marshal.Copy(rawBgra, 0, bitmap.GetPixels(), rawBgra.Length);

                    if (format == ExportImageFormat.Jpeg)
                    {
                        // Composite over white background so transparency is not converted to black
                        using var surface = SKSurface.Create(info);
                        using var canvas = surface.Canvas;
                        canvas.Clear(SKColors.White);
                        using var skImage = SKImage.FromBitmap(bitmap);
                        canvas.DrawImage(skImage, 0, 0, SKSamplingOptions.Default, null);
                        using var image = surface.Snapshot();
                        using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
                        if (data == null || data.Size == 0)
                        {
                            throw new InvalidOperationException($"Failed to encode page {pageNum} as JPEG.");
                        }
                        File.WriteAllBytes(tempPartial, data.ToArray());
                    }
                    else
                    {
                        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                        if (data == null || data.Size == 0)
                        {
                            throw new InvalidOperationException($"Failed to encode page {pageNum} as PNG.");
                        }
                        File.WriteAllBytes(tempPartial, data.ToArray());
                    }

                    // Validate output image can be decoded and has non-zero dimensions
                    using (var checkCodec = SKCodec.Create(tempPartial))
                    {
                        if (checkCodec == null || checkCodec.Info.Width <= 0 || checkCodec.Info.Height <= 0)
                        {
                            throw new InvalidOperationException($"Output validation failed for rendered page {pageNum}.");
                        }
                    }

                    // Promote atomically
                    File.Move(tempPartial, finalPath, overwrite: false);
                    tempPartials.Remove(tempPartial);
                    exportedFiles.Add(finalPath);
                }

                long totalBytes = exportedFiles.Sum(f => new FileInfo(f).Length);
                string fmtName = format == ExportImageFormat.Jpeg ? "JPEG" : "PNG";
                string summary = $"Exported {exportedFiles.Count} of {totalPages} page(s) as {fmtName} ({dpi} DPI).";

                progress?.Report(new ProcessingProgress(100, "Done."));

                return new PdfExportResult(
                    exportedFiles,
                    totalPages,
                    exportedFiles.Count,
                    totalBytes,
                    format,
                    dpi,
                    summary);
            }
            catch
            {
                // Clean up any remaining .partial files
                foreach (var p in tempPartials)
                {
                    TryDelete(p);
                }

                // Clean up any exported files from this failed run to prevent orphaned partial results
                foreach (var f in exportedFiles)
                {
                    TryDelete(f);
                }

                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record PdfTextExtractionResult(
    string OutputPath,
    int TotalPagesInDoc,
    int PagesExtractedCount,
    int TotalCharactersExtracted,
    int NonEmptyPagesCount,
    bool IsScannedOrImageOnly,
    string SummaryNote);

public static class PdfTextExtractor
{
    public static PdfTextExtractionResult ExtractText(
        string inputPath,
        IOutputPathAllocator allocator,
        string? pageRange,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The source PDF file was not found.", inputPath);
        }

        if (new FileInfo(inputPath).Length == 0)
        {
            throw new InvalidOperationException("The source PDF file is empty (0 bytes).");
        }

        IDocReader docReader;
        try
        {
            docReader = DocLib.Instance.GetDocReader(inputPath, new PageDimensions(1.0));
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw new InvalidOperationException(
                "This PDF document is password-protected or uses an unsupported encryption handler. Encrypted PDFs cannot be processed.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The PDF document is malformed or corrupted and could not be parsed.",
                ex);
        }

        using (docReader)
        {
            int totalPages = docReader.GetPageCount();
            if (totalPages == 0)
            {
                throw new InvalidOperationException("The PDF document contains no pages.");
            }

            var pagesToExtract = PageRangeParser.Parse(pageRange, totalPages);
            if (pagesToExtract.Count == 0)
            {
                throw new InvalidOperationException("No valid pages were selected for text extraction.");
            }

            allocator.EnsureRoot();
            var desiredName = Path.GetFileNameWithoutExtension(inputPath) + ".txt";
            var finalPath = allocator.Allocate(inputPath, desiredName);
            var tempPartial = finalPath + ".partial";

            try
            {
                var sb = new StringBuilder();
                int totalChars = 0;
                int nonEmptyPages = 0;
                int count = pagesToExtract.Count;

                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageNum = pagesToExtract[i];
                    int percent = (int)Math.Round(((i + 1) / (double)count) * 100.0);
                    progress?.Report(new ProcessingProgress(percent, $"Extracting text from page {pageNum} of {totalPages} ({i + 1}/{count})…"));

                    int pageIndex = pageNum - 1;
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    string rawText = pageReader.GetText()?.TrimEnd() ?? "";

                    sb.AppendLine($"--- Page {pageNum} ---");
                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        sb.AppendLine(rawText);
                        totalChars += rawText.Length;
                        nonEmptyPages++;
                    }
                    else
                    {
                        sb.AppendLine("[No extractable text on this page]");
                    }
                    sb.AppendLine();
                }

                bool isScanned = (totalChars == 0);

                File.WriteAllText(tempPartial, sb.ToString(), Encoding.UTF8);

                if (!File.Exists(tempPartial) || new FileInfo(tempPartial).Length == 0)
                {
                    throw new InvalidOperationException("Failed to generate text output file.");
                }

                File.Move(tempPartial, finalPath, overwrite: false);

                string summary = isScanned
                    ? $"Extracted 0 characters across {count} page(s). Document appears to be scanned or image-only without an extractable text layer."
                    : $"Extracted {totalChars:N0} characters across {nonEmptyPages} page(s) ({count} page(s) scanned).";

                progress?.Report(new ProcessingProgress(100, "Done."));

                return new PdfTextExtractionResult(
                    finalPath,
                    totalPages,
                    count,
                    totalChars,
                    nonEmptyPages,
                    isScanned,
                    summary);
            }
            catch
            {
                TryDelete(tempPartial);
                TryDelete(finalPath);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
