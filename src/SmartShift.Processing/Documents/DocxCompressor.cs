using System.IO;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using SmartShift.Core.IO;
using SmartShift.Processing;

namespace SmartShift.Processing.Documents;

public sealed record DocxCompressionReport(
    string OutputPath,
    long OriginalSizeBytes,
    long OutputSizeBytes,
    long BytesSaved,
    double PercentChange,
    bool IsReduction,
    int ImagesOptimizedCount,
    string SummaryMessage);

public static class DocxCompressor
{
    public static DocxCompressionReport Compress(
        string sourceDocxPath,
        string outputPath,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceDocxPath) || !File.Exists(sourceDocxPath))
        {
            throw new FileNotFoundException("Source Word document does not exist.", sourceDocxPath);
        }

        var sourceInfo = new FileInfo(sourceDocxPath);
        long originalSize = sourceInfo.Length;
        if (originalSize == 0)
        {
            throw new InvalidOperationException("Source Word document is empty (0 bytes).");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        int imagesOptimized = 0;

        try
        {
            progress?.Report(new ProcessingProgress(15, "Analyzing OpenXML package structure…"));

            using (var sourceStream = new FileStream(sourceDocxPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sourceZip = new ZipArchive(sourceStream, ZipArchiveMode.Read))
            using (var targetStream = new FileStream(tempPartial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var targetZip = new ZipArchive(targetStream, ZipArchiveMode.Create))
            {
                int totalEntries = sourceZip.Entries.Count;
                int currentEntry = 0;

                foreach (var entry in sourceZip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    currentEntry++;

                    if (currentEntry % 10 == 0)
                    {
                        int pct = 20 + (int)(65.0 * currentEntry / Math.Max(1, totalEntries));
                        progress?.Report(new ProcessingProgress(pct, $"Recompressing package parts ({currentEntry}/{totalEntries})…"));
                    }

                    // Check if entry is embedded media
                    var entryName = entry.FullName;
                    var isMedia = entryName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase);

                    byte[] entryBytes;
                    using (var entryStream = entry.Open())
                    using (var mem = new MemoryStream())
                    {
                        entryStream.CopyTo(mem);
                        entryBytes = mem.ToArray();
                    }

                    if (isMedia && entryBytes.Length > 2048)
                    {
                        // Attempt safe media optimization
                        var optBytes = TryOptimizeMedia(entryName, entryBytes);
                        if (optBytes is not null && optBytes.Length < entryBytes.Length)
                        {
                            entryBytes = optBytes;
                            imagesOptimized++;
                        }
                    }

                    // Write with highest standard compression level
                    var newEntry = targetZip.CreateEntry(entry.FullName, CompressionLevel.SmallestSize);
                    using (var outEntryStream = newEntry.Open())
                    {
                        outEntryStream.Write(entryBytes, 0, entryBytes.Length);
                    }
                }
            }

            progress?.Report(new ProcessingProgress(88, "Validating compressed package integrity…"));

            // Validate that the output package is a structurally sound DOCX
            using (var verifyDoc = WordprocessingDocument.Open(tempPartial, false))
            {
                if (verifyDoc.MainDocumentPart?.Document?.Body is null)
                {
                    throw new InvalidOperationException("Compressed DOCX package validation failed: missing document body.");
                }
            }

            // Measure final size
            var outputInfo = new FileInfo(tempPartial);
            long outputSize = outputInfo.Length;
            long bytesSaved = originalSize - outputSize;
            double percentSaved = originalSize > 0 ? (double)bytesSaved / originalSize * 100.0 : 0.0;
            bool isReduction = bytesSaved > 0;

            string summaryMsg;
            if (isReduction)
            {
                summaryMsg = $"Reduced file by {FileSizeFormatter.Format(bytesSaved)} ({percentSaved:0.1}% reduction). Original: {FileSizeFormatter.Format(originalSize)} → Output: {FileSizeFormatter.Format(outputSize)}.";
                if (imagesOptimized > 0)
                {
                    summaryMsg += $" (Optimized {imagesOptimized} embedded media part{(imagesOptimized > 1 ? "s" : "")}).";
                }
            }
            else if (bytesSaved == 0)
            {
                summaryMsg = $"Document was already optimally compressed ({FileSizeFormatter.Format(originalSize)}). Output matches original size.";
            }
            else
            {
                double percentGrowth = (double)(outputSize - originalSize) / originalSize * 100.0;
                summaryMsg = $"Notice: Document was already compactly packaged. Output grew by {FileSizeFormatter.Format(-bytesSaved)} (+{percentGrowth:0.1}%). Original: {FileSizeFormatter.Format(originalSize)} → Output: {FileSizeFormatter.Format(outputSize)}.";
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(tempPartial, outputPath);

            progress?.Report(new ProcessingProgress(100, "DOCX compression completed."));

            return new DocxCompressionReport(
                OutputPath: outputPath,
                OriginalSizeBytes: originalSize,
                OutputSizeBytes: outputSize,
                BytesSaved: bytesSaved,
                PercentChange: percentSaved,
                IsReduction: isReduction,
                ImagesOptimizedCount: imagesOptimized,
                SummaryMessage: summaryMsg);
        }
        catch
        {
            if (File.Exists(tempPartial))
            {
                try { File.Delete(tempPartial); } catch { }
            }
            throw;
        }
    }

    private static byte[]? TryOptimizeMedia(string entryName, byte[] originalBytes)
    {
        var ext = Path.GetExtension(entryName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp")) return null;

        try
        {
            using var codec = SKCodec.Create(new MemoryStream(originalBytes));
            if (codec is null) return null;

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null) return null;

            if (ext is ".jpg" or ".jpeg")
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                if (data is not null && data.Size < originalBytes.Length)
                {
                    return data.ToArray();
                }
            }
            else if (ext is ".png")
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is not null && data.Size < originalBytes.Length)
                {
                    return data.ToArray();
                }
            }
        }
        catch
        {
            // If SK decoding fails, keep original
        }

        return null;
    }
}
