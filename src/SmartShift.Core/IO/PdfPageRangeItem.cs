namespace SmartShift.Core.IO;

/// <summary>
/// Represents a validated, continuous 1-based page range or single page extracted from a source PDF.
/// </summary>
public sealed record PdfPageRangeItem(
    int StartPage,
    int EndPage,
    IReadOnlyList<int> Pages,
    string RawText)
{
    public bool IsSinglePage => StartPage == EndPage;

    public int PageCount => Pages.Count;

    public string ToSuggestedFileSuffix(int totalDigits = 3)
    {
        string fmt = $"D{Math.Max(1, totalDigits)}";
        return IsSinglePage
            ? $"_page_{StartPage.ToString(fmt)}"
            : $"_pages_{StartPage.ToString(fmt)}-{EndPage.ToString(fmt)}";
    }

    public override string ToString() => IsSinglePage ? $"{StartPage}" : $"{StartPage}-{EndPage}";
}
