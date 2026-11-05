using System.Globalization;
using System.Text;

namespace SmartShift.Core.IO;

/// <summary>
/// Validates and parses user-entered page ranges for PDF splitting and extraction.
/// Supports single pages ("5"), ranges ("1-3"), and comma-separated combinations ("1-3, 5, 8-10").
/// Also formats sets of selected page indices back into canonical range expressions.
/// </summary>
public static class PdfPageRangeParser
{
    /// <summary>
    /// Validates the range text against a document's total page count and parses it into distinct range items.
    /// Rejects empty input, malformed syntax, inverted ranges, out-of-bounds pages, duplicates, and overlapping ranges.
    /// </summary>
    public static bool TryParseRanges(
        string? rangeText,
        int totalPages,
        out IReadOnlyList<PdfPageRangeItem> ranges,
        out string? errorMessage)
    {
        ranges = [];
        errorMessage = null;

        if (totalPages <= 0)
        {
            errorMessage = "The document has no pages.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rangeText))
        {
            errorMessage = "Page range input cannot be empty. Specify page numbers such as '1-3' or '1, 3, 5-7'.";
            return false;
        }

        var trimmed = rangeText.Trim();
        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
        {
            ranges = [new PdfPageRangeItem(1, totalPages, Enumerable.Range(1, totalPages).ToList(), "1-" + totalPages)];
            return true;
        }

        var tokens = trimmed.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            errorMessage = "No page ranges found in input.";
            return false;
        }

        var parsedItems = new List<PdfPageRangeItem>();
        var claimedPages = new HashSet<int>();

        foreach (var rawToken in tokens)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                errorMessage = "Page range contains an empty segment between commas.";
                return false;
            }

            if (rawToken.Contains('-'))
            {
                var parts = rawToken.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    errorMessage = $"Invalid range syntax '{rawToken}'. Expected format 'start-end' (e.g. '1-5').";
                    return false;
                }

                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long startLong) || startLong <= 0)
                {
                    errorMessage = $"Invalid start page '{parts[0]}' in range '{rawToken}'. Must be a positive integer.";
                    return false;
                }

                if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long endLong) || endLong <= 0)
                {
                    errorMessage = $"Invalid end page '{parts[1]}' in range '{rawToken}'. Must be a positive integer.";
                    return false;
                }

                if (startLong > int.MaxValue || endLong > int.MaxValue)
                {
                    errorMessage = $"Page number exceeds maximum supported limit in '{rawToken}'.";
                    return false;
                }

                int start = (int)startLong;
                int end = (int)endLong;

                if (start > end)
                {
                    errorMessage = $"Inverted range '{rawToken}'. Start page ({start}) cannot exceed end page ({end}).";
                    return false;
                }

                if (start > totalPages)
                {
                    errorMessage = $"Start page {start} in range '{rawToken}' exceeds total document pages ({totalPages}).";
                    return false;
                }

                if (end > totalPages)
                {
                    errorMessage = $"End page {end} in range '{rawToken}' exceeds total document pages ({totalPages}).";
                    return false;
                }

                var pageList = new List<int>(end - start + 1);
                for (int p = start; p <= end; p++)
                {
                    if (!claimedPages.Add(p))
                    {
                        errorMessage = $"Page {p} in range '{rawToken}' was already included in a prior range or selection.";
                        return false;
                    }
                    pageList.Add(p);
                }

                parsedItems.Add(new PdfPageRangeItem(start, end, pageList, rawToken));
            }
            else
            {
                if (!long.TryParse(rawToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pageLong) || pageLong <= 0)
                {
                    errorMessage = $"Invalid page number '{rawToken}'. Must be a positive integer.";
                    return false;
                }

                if (pageLong > int.MaxValue)
                {
                    errorMessage = $"Page number '{rawToken}' exceeds maximum supported limit.";
                    return false;
                }

                int singlePage = (int)pageLong;

                if (singlePage > totalPages)
                {
                    errorMessage = $"Page number {singlePage} exceeds total document pages ({totalPages}).";
                    return false;
                }

                if (!claimedPages.Add(singlePage))
                {
                    errorMessage = $"Page {singlePage} was already included in a prior range or selection.";
                    return false;
                }

                parsedItems.Add(new PdfPageRangeItem(singlePage, singlePage, [singlePage], rawToken));
            }
        }

        if (parsedItems.Count == 0)
        {
            errorMessage = "No valid page ranges could be parsed.";
            return false;
        }

        ranges = parsedItems;
        return true;
    }

    /// <summary>
    /// Formats a collection of 1-based page numbers into a canonical comma-separated range string (e.g. {1,2,3,5,7,8} -> "1-3, 5, 7-8").
    /// </summary>
    public static string FormatSelectedPages(IEnumerable<int>? selectedPages)
    {
        if (selectedPages is null) return string.Empty;

        var sorted = selectedPages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        if (sorted.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        int rangeStart = sorted[0];
        int prev = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            int current = sorted[i];
            if (current == prev + 1)
            {
                prev = current;
            }
            else
            {
                AppendSegment(sb, rangeStart, prev);
                rangeStart = current;
                prev = current;
            }
        }

        AppendSegment(sb, rangeStart, prev);
        return sb.ToString();
    }

    private static void AppendSegment(StringBuilder sb, int start, int end)
    {
        if (sb.Length > 0)
        {
            sb.Append(", ");
        }

        if (start == end)
        {
            sb.Append(start);
        }
        else
        {
            sb.Append(start).Append('-').Append(end);
        }
    }
}
