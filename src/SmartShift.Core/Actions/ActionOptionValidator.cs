using System.Globalization;
using SmartShift.Core.IO;

namespace SmartShift.Core.Actions;

public static class ActionOptionValidator
{
    public static IReadOnlyList<string> Validate(string actionId, IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        if (actionId is ActionIds.CompressImage or ActionIds.ConvertToWebp or ActionIds.ConvertToJpeg)
        {
            RequireInt(values, "quality", 1, 100, errors);
        }

        if (actionId is ActionIds.ConvertToPng && values.ContainsKey("quality"))
        {
            RequireInt(values, "quality", 1, 100, errors);
        }

        if (actionId == ActionIds.ResizeImage)
        {
            RequireInt(values, "width", 1, ImageSafetyLimits.MaxDimension, errors);
            var keepAspect = ParseBool(values, "keepAspect", errors);
            if (keepAspect != true)
            {
                RequireInt(values, "height", 1, ImageSafetyLimits.MaxDimension, errors);
            }
        }

        if (actionId == ActionIds.CompressPdf)
        {
            ValidatePdfCompression(values, errors);
        }

        if (actionId == ActionIds.ExportPdfPages)
        {
            ValidatePdfExport(values, errors);
        }

        if (actionId == ActionIds.ExtractPdfText)
        {
            ValidatePdfText(values, errors);
        }

        if (actionId == ActionIds.ConvertPdfToDocx)
        {
            ValidatePdfDocx(values, errors);
        }

        if (actionId == ActionIds.ConvertPdfToPptx)
        {
            ValidatePdfPptx(values, errors);
        }

        return errors;
    }

    private static void ValidatePdfDocx(IReadOnlyDictionary<string, string> values, List<string> errors)
    {
        if (values.TryGetValue("pageRange", out var rawRange) && !string.IsNullOrWhiteSpace(rawRange))
        {
            if (!PageRangeParser.TryValidateSyntax(rawRange, out var rangeError))
            {
                errors.Add(rangeError!);
            }
        }
    }

    private static void ValidatePdfPptx(IReadOnlyDictionary<string, string> values, List<string> errors)
    {
        if (values.TryGetValue("slideMode", out var rawMode) && !string.IsNullOrWhiteSpace(rawMode))
        {
            var mode = rawMode.Trim().ToLowerInvariant();
            if (mode is not ("pageasimage" or "editable"))
            {
                errors.Add("slideMode must be 'pageAsImage' or 'editable'.");
            }
        }

        if (values.TryGetValue("pageRange", out var rawRange) && !string.IsNullOrWhiteSpace(rawRange))
        {
            if (!PageRangeParser.TryValidateSyntax(rawRange, out var rangeError))
            {
                errors.Add(rangeError!);
            }
        }
    }

    private static void ValidatePdfExport(IReadOnlyDictionary<string, string> values, List<string> errors)
    {
        if (values.TryGetValue("format", out var rawFmt) && !string.IsNullOrWhiteSpace(rawFmt))
        {
            var fmt = rawFmt.Trim().ToLowerInvariant();
            if (fmt is not ("png" or "jpeg" or "jpg"))
            {
                errors.Add("format must be 'png' or 'jpeg'.");
            }
        }

        if (values.TryGetValue("dpi", out var rawDpi) && !string.IsNullOrWhiteSpace(rawDpi))
        {
            if (!int.TryParse(rawDpi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dpi) || (dpi != 100 && dpi != 150 && dpi != 300))
            {
                errors.Add("dpi must be 100, 150, or 300.");
            }
        }

        if (values.TryGetValue("quality", out var rawQuality) && !string.IsNullOrWhiteSpace(rawQuality))
        {
            RequireInt(values, "quality", 1, 100, errors);
        }

        if (values.TryGetValue("pageRange", out var rawRange) && !string.IsNullOrWhiteSpace(rawRange))
        {
            if (!PageRangeParser.TryValidateSyntax(rawRange, out var syntaxError))
            {
                errors.Add(syntaxError ?? "Invalid page range syntax.");
            }
        }
    }

    private static void ValidatePdfText(IReadOnlyDictionary<string, string> values, List<string> errors)
    {
        if (values.TryGetValue("pageRange", out var rawRange) && !string.IsNullOrWhiteSpace(rawRange))
        {
            if (!PageRangeParser.TryValidateSyntax(rawRange, out var syntaxError))
            {
                errors.Add(syntaxError ?? "Invalid page range syntax.");
            }
        }
    }

    private static void ValidatePdfCompression(IReadOnlyDictionary<string, string> values, List<string> errors)
    {
        if (values.TryGetValue("targetBytes", out var rawBytes) && !string.IsNullOrWhiteSpace(rawBytes))
        {
            if (!long.TryParse(rawBytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
            {
                errors.Add("targetBytes must be a positive number.");
            }
            else if (bytes > 1024L * 1024L * 1024L)
            {
                errors.Add("targetBytes must not exceed 1024 MB.");
            }
        }

        if (values.TryGetValue("targetSize", out var rawSize) && !string.IsNullOrWhiteSpace(rawSize))
        {
            if (!double.TryParse(rawSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size <= 0 || double.IsNaN(size) || double.IsInfinity(size))
            {
                errors.Add("targetSize must be a positive number.");
            }
            else
            {
                var unit = values.TryGetValue("targetUnit", out var u) ? u.Trim().ToUpperInvariant() : "MB";
                if (unit is not ("KB" or "MB"))
                {
                    errors.Add("targetUnit must be 'KB' or 'MB'.");
                }
                else
                {
                    var requestedMb = unit == "KB" ? size / 1024.0 : size;
                    if (requestedMb > 1024.0)
                    {
                        errors.Add("targetSize must not exceed 1024 MB.");
                    }
                }
            }
        }

        if (values.TryGetValue("imageQuality", out var rawQuality) && !string.IsNullOrWhiteSpace(rawQuality))
        {
            var q = rawQuality.Trim().ToLowerInvariant();
            if (q is not ("off" or "lossless" or "best" or "balanced" or "smallest"))
            {
                errors.Add("imageQuality must be 'off', 'best', 'balanced', or 'smallest'.");
            }
        }
    }

    private static void RequireInt(IReadOnlyDictionary<string, string> values, string key, int min, int max, List<string> errors)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            errors.Add($"{key} is required.");
            return;
        }

        if (!int.TryParse(raw, out var number) || number < min || number > max)
        {
            errors.Add($"{key} must be an integer from {min} to {max}.");
        }
    }

    private static bool? ParseBool(IReadOnlyDictionary<string, string> values, string key, List<string> errors)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        errors.Add($"{key} must be true or false.");
        return null;
    }
}
