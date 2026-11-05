using System.Text.Json.Serialization;

namespace SmartShift.Core.Images;

/// <summary>
/// Reusable image optimization configuration preset.
/// </summary>
public sealed record ImageOptimizationPreset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("format")] string Format, // "jpeg", "png", "webp"
    [property: JsonPropertyName("quality")] int Quality, // 1 - 100
    [property: JsonPropertyName("targetSizeKb")] long? TargetSizeKb = null,
    [property: JsonPropertyName("stripMetadata")] bool StripMetadata = false,
    [property: JsonPropertyName("resizeWidth")] int? ResizeWidth = null,
    [property: JsonPropertyName("resizeHeight")] int? ResizeHeight = null,
    [property: JsonPropertyName("keepAspect")] bool KeepAspect = true,
    [property: JsonPropertyName("namingTemplate")] string? NamingTemplate = null,
    [property: JsonPropertyName("isBuiltIn")] bool IsBuiltIn = false)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("Preset ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Preset name cannot be empty.");
        }

        var normalizedFmt = Format?.Trim().ToLowerInvariant();
        if (normalizedFmt is not ("jpeg" or "jpg" or "png" or "webp"))
        {
            errors.Add($"Unsupported format '{Format}'. Supported formats are JPEG, PNG, and WebP.");
        }

        if (Quality < 1 || Quality > 100)
        {
            errors.Add("Quality must be between 1 and 100.");
        }

        if (TargetSizeKb.HasValue && TargetSizeKb.Value <= 0)
        {
            errors.Add("Target size must be a positive integer.");
        }

        if (ResizeWidth.HasValue && (ResizeWidth.Value <= 0 || ResizeWidth.Value > 16384))
        {
            errors.Add("Resize width must be between 1 and 16,384.");
        }

        if (ResizeHeight.HasValue && (ResizeHeight.Value <= 0 || ResizeHeight.Value > 16384))
        {
            errors.Add("Resize height must be between 1 and 16,384.");
        }

        return errors;
    }

    public static IReadOnlyList<ImageOptimizationPreset> GetBuiltInPresets() =>
    [
        new(
            Id: "preset-web-balanced",
            Name: "Web Balanced (WebP 80)",
            Description: "Balanced WebP compression optimal for modern web delivery and fast page load.",
            Format: "webp",
            Quality: 80,
            StripMetadata: true,
            IsBuiltIn: true),

        new(
            Id: "preset-web-lossless",
            Name: "Lossless Archive (PNG)",
            Description: "Maximum lossless PNG compression preserving full pixel fidelity and transparency.",
            Format: "png",
            Quality: 95,
            StripMetadata: false,
            IsBuiltIn: true),

        new(
            Id: "preset-photo-jpeg",
            Name: "High Quality JPEG (90)",
            Description: "Near-original photographic JPEG quality with EXIF preserved.",
            Format: "jpeg",
            Quality: 90,
            StripMetadata: false,
            IsBuiltIn: true),

        new(
            Id: "preset-compact-email",
            Name: "Compact Email (JPEG 60)",
            Description: "Aggressive compression with metadata stripped for email attachments and messaging.",
            Format: "jpeg",
            Quality: 60,
            StripMetadata: true,
            NamingTemplate: "{name}-compact.{ext}",
            IsBuiltIn: true),

        new(
            Id: "preset-social-hd",
            Name: "Social Media HD (1920px)",
            Description: "Standard 1080p/Full HD width with locked aspect ratio and WebP 85% quality.",
            Format: "webp",
            Quality: 85,
            ResizeWidth: 1920,
            KeepAspect: true,
            StripMetadata: true,
            IsBuiltIn: true),

        new(
            Id: "preset-target-500kb",
            Name: "Target 500 KB",
            Description: "Bounded search optimizing output file size toward 500 KB.",
            Format: "webp",
            Quality: 80,
            TargetSizeKb: 500,
            StripMetadata: true,
            IsBuiltIn: true)
    ];
}
