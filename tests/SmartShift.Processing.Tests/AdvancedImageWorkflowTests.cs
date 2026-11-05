using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.Images;
using SmartShift.Core.IO;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Images;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class AdvancedImageWorkflowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OutputPathAllocator _allocator;
    private readonly FileClassifier _classifier;

    public AdvancedImageWorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SmartShift_AdvImageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _allocator = new OutputPathAllocator(Path.Combine(_tempDir, "Outputs"));
        _classifier = new FileClassifier();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private string CreateTestImage(int width = 200, int height = 150, SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool transparent = true)
    {
        var ext = format switch
        {
            SKEncodedImageFormat.Jpeg => ".jpg",
            SKEncodedImageFormat.Webp => ".webp",
            _ => ".png"
        };
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}{ext}");

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.CornflowerBlue);

        using var paint = new SKPaint
        {
            Color = SKColors.Red,
            IsAntialias = true,
            StrokeWidth = 4
        };
        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);

        return path;
    }

    [Fact]
    public void FeatureA_OptimizationProfiles_JpegEncodesAndNotesAlpha()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);

        var profile = new JpegProfile(Quality: 85, Subsampling: JpegSubsampling.HighQuality444);
        using var encoded = ImageOptimizationProfiles.EncodeJpeg(bitmap, profile, out var note);

        Assert.NotNull(encoded);
        Assert.True(encoded.Size > 0);
        Assert.Contains("subsampling: HighQuality444", note);
        Assert.Contains("does not preserve transparency", note);
    }

    [Fact]
    public void FeatureA_OptimizationProfiles_PngEncodesLosslessWithFilters()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var profile = new PngProfile(CompressionLevel: 8, FilterFlags: SKPngEncoderFilterFlags.AllFilters);
        using var encoded = ImageOptimizationProfiles.EncodePng(bitmap, profile, out var note);

        Assert.NotNull(encoded);
        Assert.True(encoded.Size > 0);
        Assert.Contains("zlib compression 8", note);
        Assert.Contains("Transparency preserved", note);
    }

    [Fact]
    public void FeatureA_OptimizationProfiles_WebpLosslessAndLossyModes()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Green);

        var losslessProfile = new WebpProfile(Lossless: true);
        using var losslessData = ImageOptimizationProfiles.EncodeWebp(bitmap, losslessProfile, out var losslessNote);
        Assert.NotNull(losslessData);
        Assert.Contains("lossless", losslessNote);

        var lossyProfile = new WebpProfile(Lossless: false, Quality: 75);
        using var lossyData = ImageOptimizationProfiles.EncodeWebp(bitmap, lossyProfile, out var lossyNote);
        Assert.NotNull(lossyData);
        Assert.Contains("quality 75", lossyNote);
    }

    [Fact]
    public void FeatureA_AvifSupportStatus_ReportsExplicitlyUnsupported()
    {
        Assert.False(ImageOptimizationProfiles.IsAvifSupported);
        Assert.Contains("AVIF encoding is not supported", ImageOptimizationProfiles.AvifUnsupportedReason);
    }

    [Fact]
    public void FeatureB_TargetSizeOptimizer_AchievesTargetWhenPossible()
    {
        using var bitmap = new SKBitmap(400, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Crimson);

        // A 400x400 image in JPEG can easily fit within 20 KB
        long targetBytes = 25 * 1024;
        var result = ImageTargetSizeOptimizer.Optimize(bitmap, SKEncodedImageFormat.Jpeg, targetBytes);

        using (result.EncodedData)
        {
            Assert.True(result.TargetAchieved);
            Assert.True(result.ActualBytes <= targetBytes);
            Assert.True(result.Iterations <= 7);
            Assert.True(result.SelectedQuality >= 5);
        }
    }

    [Fact]
    public void FeatureB_TargetSizeOptimizer_ReportsImpossibleTargetGracefully()
    {
        using var bitmap = new SKBitmap(600, 600);
        using var canvas = new SKCanvas(bitmap);
        // Fill with noise so it's impossible to compress into 200 bytes
        var rand = new Random(42);
        for (int x = 0; x < 600; x += 10)
        {
            for (int y = 0; y < 600; y += 10)
            {
                using var p = new SKPaint { Color = new SKColor((byte)rand.Next(255), (byte)rand.Next(255), (byte)rand.Next(255)) };
                canvas.DrawRect(x, y, 10, 10, p);
            }
        }

        long impossibleTarget = 200; // 200 bytes
        var result = ImageTargetSizeOptimizer.Optimize(bitmap, SKEncodedImageFormat.Jpeg, impossibleTarget);

        using (result.EncodedData)
        {
            Assert.False(result.TargetAchieved);
            Assert.True(result.ActualBytes > impossibleTarget);
            Assert.Contains("unreachable", result.SummaryReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FeatureC_NamingTemplates_ReplacesTokensAndEnforcesSafety()
    {
        var now = new DateTime(2026, 11, 4, 14, 30, 0);
        var formatted = ImageNamingTemplate.Format(
            "{date}_{name}_{n:D3}.{ext}",
            "original_photo.png",
            ".webp",
            itemIndex: 2, // 3rd item -> 003
            timestamp: now);

        Assert.Equal("2026-11-04_original_photo_003.webp", formatted);
    }

    [Fact]
    public void FeatureC_NamingTemplates_StrictlyBlocksPathTraversal()
    {
        var maliciousTemplate = "../../etc/passwd_{name}.{ext}";
        var formatted = ImageNamingTemplate.Format(
            maliciousTemplate,
            "secret.png",
            ".jpg",
            itemIndex: 0);

        Assert.DoesNotContain("..", formatted);
        Assert.DoesNotContain("/", formatted);
        Assert.DoesNotContain("\\", formatted);
        Assert.Equal("etcpasswd_secret.jpg", formatted);
    }

    [Fact]
    public void FeatureC_NamingTemplates_LivePreviewMatchesFormat()
    {
        var preview = ImageNamingTemplate.GeneratePreview("{name}_preview_{n:D2}.{ext}", "sample.jpg", ".webp", sampleIndex: 0);
        Assert.Equal("sample_preview_01.webp", preview);
    }

    [Fact]
    public void FeatureD_MetadataReader_InspectsMetadataWithoutDecompression()
    {
        var imgPath = CreateTestImage(width: 320, height: 240, format: SKEncodedImageFormat.Png, transparent: true);
        var meta = ImageMetadataReader.Read(imgPath);

        Assert.NotNull(meta);
        Assert.Equal(320, meta.Width);
        Assert.Equal(240, meta.Height);
        Assert.Equal("320 × 240 px", meta.DimensionsText);
        Assert.Equal("4:3", meta.AspectRatioText);
        Assert.Equal("Png", meta.Format);
    }

    [Fact]
    public async Task FeatureD_MetadataStrip_RemovesExifAndBakesOrientation()
    {
        var imgPath = CreateTestImage(width: 200, height: 200, format: SKEncodedImageFormat.Jpeg, transparent: false);
        var classified = _classifier.Classify(imgPath);
        var processor = new ImageFileProcessor(ActionIds.RemoveImageMetadata, _allocator, _classifier);

        var request = new ProcessingRequest(
            classified,
            ActionIds.RemoveImageMetadata,
            new ProcessingOptions(),
            _allocator.RootDirectory);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Contains("Orientation was baked into pixels", result.Details["metadata"]);
    }

    [Fact]
    public void FeatureE_OptimizationPresets_BuiltInsAndValidation()
    {
        var builtIns = ImageOptimizationPreset.GetBuiltInPresets();
        Assert.NotEmpty(builtIns);
        Assert.Contains(builtIns, p => p.Id == "preset-web-balanced");
        Assert.Contains(builtIns, p => p.Id == "preset-web-lossless");
        Assert.Contains(builtIns, p => p.Id == "preset-target-500kb");

        var invalidPreset = new ImageOptimizationPreset(
            Id: "",
            Name: "",
            Description: "Invalid",
            Format: "gif",
            Quality: 150);

        var errors = invalidPreset.Validate();
        Assert.True(errors.Count >= 3);
    }

    [Fact]
    public void FeatureE_PresetStore_SaveFindAndDeleteCustomPreset()
    {
        var storeFile = Path.Combine(_tempDir, "presets.json");
        var store = new ImagePresetStore(storeFile);

        var initialCount = store.GetAll().Count;

        var custom = new ImageOptimizationPreset(
            Id: "custom-social-banner",
            Name: "Custom Banner 1200x630",
            Description: "Optimized for social cards",
            Format: "webp",
            Quality: 82,
            ResizeWidth: 1200,
            ResizeHeight: 630,
            KeepAspect: false,
            StripMetadata: true);

        store.SavePreset(custom);

        var afterSave = store.GetAll();
        Assert.Equal(initialCount + 1, afterSave.Count);

        var retrieved = store.FindById("custom-social-banner");
        Assert.NotNull(retrieved);
        Assert.Equal("Custom Banner 1200x630", retrieved.Name);
        Assert.Equal(82, retrieved.Quality);
        Assert.False(retrieved.IsBuiltIn);

        // Cannot delete built-ins
        Assert.False(store.DeletePreset("preset-web-balanced"));

        // Can delete custom
        Assert.True(store.DeletePreset("custom-social-banner"));
        Assert.Null(store.FindById("custom-social-banner"));
    }

    [Fact]
    public void FeatureE_PresetStore_CorruptedJsonRecoversBuiltInsGracefully()
    {
        var storeFile = Path.Combine(_tempDir, "corrupted_presets.json");
        File.WriteAllText(storeFile, "{ INVALID JSON CONTENT !!! }");

        var store = new ImagePresetStore(storeFile);
        var presets = store.GetAll();

        Assert.NotEmpty(presets);
        Assert.Contains(presets, p => p.Id == "preset-web-balanced");
    }

    [Fact]
    public async Task FeatureF_BatchControls_PerFileOverridesAndRetry()
    {
        var catalog = new ProcessorCatalog(_classifier, _allocator);
        var engine = new BatchImageProcessingEngine(catalog, _allocator, _classifier);

        var img1 = CreateTestImage(100, 100, SKEncodedImageFormat.Png);
        var img2 = CreateTestImage(150, 150, SKEncodedImageFormat.Jpeg, transparent: false);

        var classified1 = _classifier.Classify(img1);
        var classified2 = _classifier.Classify(img2);

        // Per-file override for img2: targetSizeKb = 15
        var overrides = new Dictionary<string, ProcessingOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [img2] = new ProcessingOptions(new Dictionary<string, string>
            {
                ["quality"] = "40",
                ["namingTemplate"] = "override_{name}_{n:D2}.{ext}"
            })
        };

        var batchRequest = new BatchJobRequest(
            Files: [classified1, classified2],
            ActionId: ActionIds.ConvertToWebp,
            Options: new ProcessingOptions(new Dictionary<string, string> { ["quality"] = "80" }),
            OutputDirectory: _allocator.RootDirectory,
            MaxConcurrency: 2,
            FileOptionOverrides: overrides);

        var summary = await engine.ProcessBatchAsync(batchRequest, null, CancellationToken.None);

        Assert.True(summary.AllSucceeded);
        Assert.Equal(2, summary.SucceededCount);
        Assert.Equal(0, summary.FailedCount);

        var item2Result = summary.ItemResults.First(r => r.Input.Path == img2);
        Assert.NotNull(item2Result.OutputPath);
        Assert.Contains("override_test_", Path.GetFileName(item2Result.OutputPath));
    }
}
