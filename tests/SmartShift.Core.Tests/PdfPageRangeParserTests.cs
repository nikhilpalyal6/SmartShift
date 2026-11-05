using SmartShift.Core.IO;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class PdfPageRangeParserTests
{
    [Fact]
    public void SinglePage_ParsesSuccessfully()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("5", 10, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(ranges);
        Assert.Equal(5, ranges[0].StartPage);
        Assert.Equal(5, ranges[0].EndPage);
        Assert.True(ranges[0].IsSinglePage);
        Assert.Equal([5], ranges[0].Pages);
        Assert.Equal("_page_005", ranges[0].ToSuggestedFileSuffix(3));
    }

    [Fact]
    public void SimpleRange_ParsesSuccessfully()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("1-3", 10, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(ranges);
        Assert.Equal(1, ranges[0].StartPage);
        Assert.Equal(3, ranges[0].EndPage);
        Assert.False(ranges[0].IsSinglePage);
        Assert.Equal([1, 2, 3], ranges[0].Pages);
        Assert.Equal("_pages_001-003", ranges[0].ToSuggestedFileSuffix(3));
    }

    [Fact]
    public void MultipleRanges_CommaSeparated_ProducesDistinctRanges()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("2-4, 7, 10-12", 20, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(3, ranges.Count);

        Assert.Equal(2, ranges[0].StartPage);
        Assert.Equal(4, ranges[0].EndPage);
        Assert.Equal([2, 3, 4], ranges[0].Pages);

        Assert.Equal(7, ranges[1].StartPage);
        Assert.Equal(7, ranges[1].EndPage);
        Assert.Equal([7], ranges[1].Pages);

        Assert.Equal(10, ranges[2].StartPage);
        Assert.Equal(12, ranges[2].EndPage);
        Assert.Equal([10, 11, 12], ranges[2].Pages);
    }

    [Fact]
    public void WhitespaceAroundTokens_IsHandledGracefully()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("  1 - 3 ,  5  ,  8 - 10  ", 15, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(3, ranges.Count);
        Assert.Equal([1, 2, 3], ranges[0].Pages);
        Assert.Equal([5], ranges[1].Pages);
        Assert.Equal([8, 9, 10], ranges[2].Pages);
    }

    [Fact]
    public void ValidFirstAndLastPages_BoundariesSucceed()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("1, 10", 10, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(1, ranges[0].StartPage);
        Assert.Equal(10, ranges[1].StartPage);
    }

    [Fact]
    public void KeywordAll_ExpandsToFullDocumentRange()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("all", 5, out var ranges, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(ranges);
        Assert.Equal(1, ranges[0].StartPage);
        Assert.Equal(5, ranges[0].EndPage);
        Assert.Equal([1, 2, 3, 4, 5], ranges[0].Pages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrNullInput_IsRejected(string? input)
    {
        bool ok = PdfPageRangeParser.TryParseRanges(input, 10, out var ranges, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(ranges);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1-")]
    [InlineData("-5")]
    [InlineData("1--3")]
    [InlineData("1;3")]
    [InlineData("1..3")]
    [InlineData("1, , 3")]
    public void MalformedExpressions_AreRejected(string malformed)
    {
        bool ok = PdfPageRangeParser.TryParseRanges(malformed, 10, out var ranges, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0-5")]
    [InlineData("-2-5")]
    [InlineData("2--4")]
    public void ZeroOrNegativePageNumbers_AreRejected(string invalid)
    {
        bool ok = PdfPageRangeParser.TryParseRanges(invalid, 10, out var ranges, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void InvertedRange_StartExceedsEnd_IsRejected()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("5-2", 10, out var ranges, out var error);
        Assert.False(ok);
        Assert.Contains("Inverted range", error);
    }

    [Theory]
    [InlineData("11", 10)]
    [InlineData("1-11", 10)]
    [InlineData("12-15", 10)]
    public void PagesBeyondDocumentCount_AreRejected(string outOfBounds, int totalPages)
    {
        bool ok = PdfPageRangeParser.TryParseRanges(outOfBounds, totalPages, out var ranges, out var error);
        Assert.False(ok);
        Assert.Contains("exceeds total document pages", error);
    }

    [Theory]
    [InlineData("3, 3")]
    [InlineData("1-3, 3")]
    [InlineData("2-5, 4-7")]
    [InlineData("1-10, 5")]
    public void DuplicateAndOverlappingRanges_AreRejected(string overlapping)
    {
        bool ok = PdfPageRangeParser.TryParseRanges(overlapping, 15, out var ranges, out var error);
        Assert.False(ok);
        Assert.Contains("already included", error);
    }

    [Fact]
    public void IntegerOverflow_SafelyRejected()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("99999999999999999999", 10, out var ranges, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TotalPagesZeroOrNegative_Rejected()
    {
        bool ok = PdfPageRangeParser.TryParseRanges("1-2", 0, out var ranges, out var error);
        Assert.False(ok);
        Assert.Contains("no pages", error);
    }

    [Fact]
    public void FormatSelectedPages_FormatsContinuousAndDiscontinuousRanges()
    {
        Assert.Equal("1-3, 5, 8-10", PdfPageRangeParser.FormatSelectedPages([1, 2, 3, 5, 8, 9, 10]));
        Assert.Equal("1-5", PdfPageRangeParser.FormatSelectedPages([1, 2, 3, 4, 5]));
        Assert.Equal("7", PdfPageRangeParser.FormatSelectedPages([7]));
        Assert.Equal(string.Empty, PdfPageRangeParser.FormatSelectedPages([]));
        Assert.Equal(string.Empty, PdfPageRangeParser.FormatSelectedPages(null));
    }
}
