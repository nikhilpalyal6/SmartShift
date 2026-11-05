using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartShift.Core.Images;

/// <summary>
/// Handles batch naming templates with token replacement, path traversal protection,
/// invalid character sanitation, and live preview generation.
/// </summary>
public static partial class ImageNamingTemplate
{
    private static readonly Regex TokenPattern = new(@"\{(?<token>[^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Applies the naming template to generate a sanitized output filename.
    /// If template is null or whitespace, returns the default name: "{originalName}{suffix}{extension}".
    /// </summary>
    public static string Format(
        string? template,
        string originalFileName,
        string targetExtension,
        int itemIndex,
        string defaultSuffix = "",
        int? width = null,
        int? height = null,
        DateTime? timestamp = null)
    {
        var rawBaseName = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = targetExtension.StartsWith('.') ? targetExtension : "." + targetExtension;
        var now = timestamp ?? DateTime.Now;

        if (string.IsNullOrWhiteSpace(template))
        {
            var fallback = $"{rawBaseName}{defaultSuffix}{ext}";
            return SanitizeFileName(fallback);
        }

        var result = TokenPattern.Replace(template, match =>
        {
            var token = match.Groups["token"].Value.Trim();

            if (token.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                return rawBaseName;
            }

            if (token.Equals("ext", StringComparison.OrdinalIgnoreCase))
            {
                return ext.TrimStart('.');
            }

            if (token.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (token.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                return now.ToString("HHmmss", CultureInfo.InvariantCulture);
            }

            if (token.Equals("width", StringComparison.OrdinalIgnoreCase))
            {
                return width.HasValue ? width.Value.ToString(CultureInfo.InvariantCulture) : "";
            }

            if (token.Equals("height", StringComparison.OrdinalIgnoreCase))
            {
                return height.HasValue ? height.Value.ToString(CultureInfo.InvariantCulture) : "";
            }

            if (token.StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                var number = itemIndex + 1; // 1-based numbering
                var colonIndex = token.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < token.Length - 1)
                {
                    var formatSpecifier = token[(colonIndex + 1)..].Trim();
                    try
                    {
                        return number.ToString(formatSpecifier, CultureInfo.InvariantCulture);
                    }
                    catch (FormatException)
                    {
                        return number.ToString(CultureInfo.InvariantCulture);
                    }
                }
                return number.ToString(CultureInfo.InvariantCulture);
            }

            // Unknown token: preserve as-is
            return match.Value;
        });

        // Ensure target extension is preserved if user did not include an extension
        if (!result.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            // If user ended with a dot, strip it before appending extension
            result = result.TrimEnd('.') + ext;
        }

        return SanitizeFileName(result);
    }

    /// <summary>
    /// Generates a live preview of the template for the UI.
    /// </summary>
    public static string GeneratePreview(
        string? template,
        string sampleFileName = "photo.jpg",
        string targetExtension = ".webp",
        int sampleIndex = 0)
    {
        try
        {
            return Format(template, sampleFileName, targetExtension, sampleIndex, defaultSuffix: "-processed", width: 1920, height: 1080);
        }
        catch (Exception ex)
        {
            return $"Invalid template: {ex.Message}";
        }
    }

    /// <summary>
    /// Strictly sanitizes a candidate filename to prevent directory traversal and invalid characters.
    /// </summary>
    public static string SanitizeFileName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "output";
        }

        // Prevent path traversal
        var sanitized = candidate.Replace("..", "").Replace("/", "").Replace("\\", "");

        // Remove any invalid filename chars
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }

        sanitized = sanitized.Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "output";
        }

        return sanitized;
    }
}
