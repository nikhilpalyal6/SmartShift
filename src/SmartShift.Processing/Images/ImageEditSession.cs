using System.IO;
using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Core.Logging;

namespace SmartShift.Processing.Images;

public sealed class ImageEditSession : IDisposable
{
    private readonly SKBitmap _originalBitmap;
    private SKBitmap _workingBitmap;
    private bool _disposed;

    public ImageEditSession(string sourcePath, CancellationToken cancellationToken = default)
    {
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source image was not found.", sourcePath);
        }

        using var decoded = SkiaImagePipeline.Decode(sourcePath, cancellationToken);
        SourceFormat = decoded.SourceFormat;
        OriginalOrigin = decoded.Origin;

        // Keep original pristine copy for reset
        _originalBitmap = decoded.Bitmap.Copy() ?? throw new InvalidOperationException("Failed to copy source image.");
        // Working bitmap starts as a copy of the decoded oriented bitmap
        _workingBitmap = decoded.Bitmap.Copy() ?? throw new InvalidOperationException("Failed to initialize working image.");
    }

    public ImageEditSession(SKBitmap sourceBitmap, SKEncodedImageFormat format, string sourcePath = "")
    {
        SourcePath = sourcePath;
        SourceFormat = format;
        OriginalOrigin = SKEncodedOrigin.TopLeft;
        _originalBitmap = sourceBitmap.Copy() ?? throw new InvalidOperationException("Failed to copy source image.");
        _workingBitmap = sourceBitmap.Copy() ?? throw new InvalidOperationException("Failed to initialize working image.");
    }

    public string SourcePath { get; }
    public SKEncodedImageFormat SourceFormat { get; }
    public SKEncodedOrigin OriginalOrigin { get; }

    public int OriginalWidth => _originalBitmap.Width;
    public int OriginalHeight => _originalBitmap.Height;

    public int CurrentWidth => _workingBitmap.Width;
    public int CurrentHeight => _workingBitmap.Height;

    public bool HasUnsavedChanges =>
        _workingBitmap.Width != _originalBitmap.Width ||
        _workingBitmap.Height != _originalBitmap.Height ||
        !BitmapsEqual(_workingBitmap, _originalBitmap);

    public SKBitmap WorkingBitmap => _workingBitmap;

    public void Crop(SKRectI cropRect)
    {
        ThrowIfDisposed();

        if (cropRect.Left < 0 || cropRect.Top < 0 ||
            cropRect.Width <= 0 || cropRect.Height <= 0 ||
            cropRect.Right > _workingBitmap.Width || cropRect.Bottom > _workingBitmap.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(cropRect),
                $"Crop rectangle ({cropRect.Left},{cropRect.Top},{cropRect.Width}×{cropRect.Height}) is out of bounds for image ({_workingBitmap.Width}×{_workingBitmap.Height}).");
        }

        var dest = new SKBitmap(cropRect.Width, cropRect.Height, _workingBitmap.ColorType, _workingBitmap.AlphaType);
        using (var canvas = new SKCanvas(dest))
        {
            canvas.Clear(SKColors.Transparent);
            using var image = SKImage.FromBitmap(_workingBitmap);
            var srcRect = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom);
            var dstRect = new SKRect(0, 0, cropRect.Width, cropRect.Height);
            canvas.DrawImage(image, srcRect, dstRect, new SKSamplingOptions(SKFilterMode.Linear));
        }

        ReplaceWorkingBitmap(dest);
    }

    public void Rotate(int degreesClockwise)
    {
        ThrowIfDisposed();

        var deg = ((degreesClockwise % 360) + 360) % 360;
        if (deg == 0) return;

        var swap = deg is 90 or 270;
        var destWidth = swap ? _workingBitmap.Height : _workingBitmap.Width;
        var destHeight = swap ? _workingBitmap.Width : _workingBitmap.Height;

        var dest = new SKBitmap(destWidth, destHeight, _workingBitmap.ColorType, _workingBitmap.AlphaType);
        using (var canvas = new SKCanvas(dest))
        {
            canvas.Clear(SKColors.Transparent);
            switch (deg)
            {
                case 90:
                    canvas.Translate(destWidth, 0);
                    canvas.RotateDegrees(90);
                    break;
                case 180:
                    canvas.Translate(destWidth, destHeight);
                    canvas.RotateDegrees(180);
                    break;
                case 270:
                    canvas.Translate(0, destHeight);
                    canvas.RotateDegrees(270);
                    break;
            }

            using var image = SKImage.FromBitmap(_workingBitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }

        ReplaceWorkingBitmap(dest);
    }

    public void Rotate90Clockwise() => Rotate(90);

    public void Rotate90CounterClockwise() => Rotate(270);

    public void Flip(bool horizontal, bool vertical)
    {
        ThrowIfDisposed();

        if (!horizontal && !vertical) return;

        var dest = new SKBitmap(_workingBitmap.Width, _workingBitmap.Height, _workingBitmap.ColorType, _workingBitmap.AlphaType);
        using (var canvas = new SKCanvas(dest))
        {
            canvas.Clear(SKColors.Transparent);

            var sx = horizontal ? -1.0f : 1.0f;
            var sy = vertical ? -1.0f : 1.0f;
            var tx = horizontal ? _workingBitmap.Width : 0.0f;
            var ty = vertical ? _workingBitmap.Height : 0.0f;

            canvas.Translate(tx, ty);
            canvas.Scale(sx, sy);

            using var image = SKImage.FromBitmap(_workingBitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }

        ReplaceWorkingBitmap(dest);
    }

    public void FlipHorizontal() => Flip(true, false);

    public void FlipVertical() => Flip(false, true);

    public void Resize(int targetWidth, int targetHeight, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentException("Target width and height must be strictly positive.");
        }

        if (ImageSafetyLimits.Exceeds(targetWidth, targetHeight))
        {
            throw new InvalidOperationException($"The requested dimensions ({targetWidth}×{targetHeight}) exceed SmartShift safety limits.");
        }

        var resized = SkiaImagePipeline.Resize(_workingBitmap, targetWidth, targetHeight, cancellationToken);
        ReplaceWorkingBitmap(resized);
    }

    public void ResizePercentage(double percentage, CancellationToken cancellationToken = default)
    {
        if (percentage <= 0 || percentage > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), "Percentage must be between 1 and 500.");
        }

        var w = Math.Max(1, (int)Math.Round(_workingBitmap.Width * (percentage / 100.0)));
        var h = Math.Max(1, (int)Math.Round(_workingBitmap.Height * (percentage / 100.0)));
        Resize(w, h, cancellationToken);
    }

    public void ResizeToLongestEdge(int targetLength, CancellationToken cancellationToken = default)
    {
        if (targetLength <= 0 || targetLength > ImageSafetyLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(targetLength), $"Target length must be between 1 and {ImageSafetyLimits.MaxDimension}px.");
        }

        int w, h;
        if (_workingBitmap.Width >= _workingBitmap.Height)
        {
            w = targetLength;
            h = Math.Max(1, (int)Math.Round(targetLength * (_workingBitmap.Height / (double)_workingBitmap.Width)));
        }
        else
        {
            h = targetLength;
            w = Math.Max(1, (int)Math.Round(targetLength * (_workingBitmap.Width / (double)_workingBitmap.Height)));
        }

        Resize(w, h, cancellationToken);
    }

    public void Reset()
    {
        ThrowIfDisposed();
        var freshCopy = _originalBitmap.Copy() ?? throw new InvalidOperationException("Failed to copy original bitmap for reset.");
        ReplaceWorkingBitmap(freshCopy);
    }

    public ProcessingResult Save(
        IOutputPathAllocator allocator,
        string? outputDirectory = null,
        SKEncodedImageFormat? outputFormat = null,
        int quality = 80,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
        {
            throw new InvalidOperationException("Cannot save: source path is not set or original file does not exist.");
        }

        var originalHash = FileHasher.Sha256Hex(SourcePath);
        var targetFormat = outputFormat ?? SourceFormat;
        var ext = SkiaImagePipeline.Extension(targetFormat);
        var baseName = Path.GetFileNameWithoutExtension(SourcePath);
        var desiredName = $"{baseName}-edited{ext}";

        allocator.EnsureRoot();
        var targetDir = outputDirectory ?? allocator.RootDirectory;
        if (!string.Equals(Path.GetFullPath(allocator.RootDirectory), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
        {
            return ProcessingResult.Fail("The output directory does not match the SmartShift output policy.");
        }

        var finalPath = allocator.Allocate(SourcePath, desiredName);
        var tempPath = finalPath + ".partial";

        try
        {
            using var encoded = SkiaImagePipeline.Encode(_workingBitmap, targetFormat, quality, out var settingNote);
            cancellationToken.ThrowIfCancellationRequested();

            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoded.SaveTo(fs);
                fs.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SkiaImagePipeline.ValidateEncoded(tempPath, targetFormat, out var outW, out var outH);

            File.Move(tempPath, finalPath);
            tempPath = null;

            var afterHash = FileHasher.Sha256Hex(SourcePath);
            if (!string.Equals(originalHash, afterHash, StringComparison.Ordinal))
            {
                TryDelete(finalPath);
                return ProcessingResult.Fail("The original file changed during editing. Operation aborted.");
            }

            var outputLength = new FileInfo(finalPath).Length;
            var details = new Dictionary<string, string>
            {
                ["outputPath"] = finalPath,
                ["outputBytes"] = outputLength.ToString(),
                ["outputSize"] = FileSizeFormatter.Format(outputLength),
                ["outputDimensions"] = $"{outW} × {outH}",
                ["outputFormat"] = targetFormat.ToString(),
                ["encoder"] = settingNote,
                ["originalUnchanged"] = "true"
            };

            return ProcessingResult.Success(
                $"Wrote {Path.GetFileName(finalPath)} ({FileSizeFormatter.Format(outputLength)}, {outW}×{outH}). The original file was not modified.",
                finalPath,
                details);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            TryDelete(finalPath);
            if (ex is OperationCanceledException)
            {
                return ProcessingResult.Cancelled();
            }
            return ProcessingResult.Fail(ex.Message, [ex.Message]);
        }
    }

    private void ReplaceWorkingBitmap(SKBitmap newBitmap)
    {
        if (!ReferenceEquals(_workingBitmap, _originalBitmap))
        {
            _workingBitmap.Dispose();
        }
        _workingBitmap = newBitmap;
    }

    private static bool BitmapsEqual(SKBitmap a, SKBitmap b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Width != b.Width || a.Height != b.Height || a.ColorType != b.ColorType) return false;
        var spanA = a.GetPixelSpan();
        var spanB = b.GetPixelSpan();
        return spanA.SequenceEqual(spanB);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); } catch { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!ReferenceEquals(_workingBitmap, _originalBitmap))
        {
            _workingBitmap.Dispose();
        }
        _originalBitmap.Dispose();
    }
}
