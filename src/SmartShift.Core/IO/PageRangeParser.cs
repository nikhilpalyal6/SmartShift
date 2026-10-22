using System.Globalization;

namespace SmartShift.Core.IO;

public static class PageRangeParser
{
    public static bool TryValidateSyntax(string? rangeText, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rangeText) || string.Equals(rangeText.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rawTokens = rangeText.Split(',');
        if (rawTokens.Length == 0)
        {
            error = "Page range cannot be empty.";
            return false;
        }

        foreach (var raw in rawTokens)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Page range contains an empty segment.";
                return false;
            }
        }

        var tokens = rangeText.Split(',', StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (token.Contains('-'))
            {
                var parts = token.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    error = $"Invalid range syntax '{token}'. Expected format like '1-5'.";
                    return false;
                }

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || start <= 0)
                {
                    error = $"Invalid start page '{parts[0]}' in range '{token}'. Must be a positive integer.";
                    return false;
                }

                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) || end <= 0)
                {
                    error = $"Invalid end page '{parts[1]}' in range '{token}'. Must be a positive integer.";
                    return false;
                }

                if (start > end)
                {
                    error = $"Inverted range '{token}'. Start page ({start}) cannot exceed end page ({end}).";
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) || single <= 0)
                {
                    error = $"Invalid page number '{token}'. Must be a positive integer.";
                    return false;
                }
            }
        }

        return true;
    }

    public static IReadOnlyList<int> Parse(string? rangeText, int totalPages)
    {
        if (totalPages <= 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(rangeText) || string.Equals(rangeText.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(1, totalPages).ToList();
        }

        var tokens = rangeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pages = new SortedSet<int>();

        foreach (var token in tokens)
        {
            if (token.Contains('-'))
            {
                var parts = token.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    throw new ArgumentException($"Invalid range format '{token}'. Expected 'start-end'.");
                }

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || start <= 0)
                {
                    throw new ArgumentException($"Invalid start page '{parts[0]}'.");
                }

                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) || end <= 0)
                {
                    throw new ArgumentException($"Invalid end page '{parts[1]}'.");
                }

                if (start > end)
                {
                    throw new ArgumentException($"Inverted range '{token}' ({start} > {end}).");
                }

                if (start > totalPages)
                {
                    throw new ArgumentException($"Page {start} exceeds document page count ({totalPages}).");
                }

                var cappedEnd = Math.Min(end, totalPages);
                for (int p = start; p <= cappedEnd; p++)
                {
                    pages.Add(p);
                }
            }
            else
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) || single <= 0)
                {
                    throw new ArgumentException($"Invalid page number '{token}'.");
                }

                if (single > totalPages)
                {
                    throw new ArgumentException($"Page {single} exceeds document page count ({totalPages}).");
                }

                pages.Add(single);
            }
        }

        return pages.ToList();
    }
}
