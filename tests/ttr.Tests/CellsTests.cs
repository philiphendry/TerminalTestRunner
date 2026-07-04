using Ttr.Ui;
using Xunit;

namespace Ttr.Tests;

public class CellsTests
{
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("計算", 4)]          // CJK: 2 cells each
    [InlineData("🚀", 2)]            // emoji: 2 cells
    [InlineData("a計b", 4)]          // mixed
    [InlineData("한국어", 6)]        // Hangul: 2 cells each
    public void Width_counts_cells_not_chars(string text, int expected)
        => Assert.Equal(expected, Cells.Width(text));

    [Theory]
    [InlineData("hello world", 8)]
    [InlineData("hello world", 1)]
    [InlineData("hello world", 2)]
    [InlineData("計算機は加算する", 5)]
    [InlineData("計算機は加算する", 4)]
    [InlineData("計算機は加算する", 3)]
    [InlineData("adds_🚀_and_✅", 7)]
    [InlineData("adds_🚀_and_✅", 6)]
    [InlineData("mix計a算b", 5)]
    public void Truncate_never_exceeds_budget(string text, int max)
        => Assert.True(Cells.Width(Cells.Truncate(text, max)) <= max,
            $"'{text}' truncated to {max} was wider than {max}");

    [Fact]
    public void Truncate_short_text_is_unchanged()
        => Assert.Equal("hi", Cells.Truncate("hi", 10));

    [Fact]
    public void Truncate_appends_ellipsis_when_cutting()
        => Assert.EndsWith("…", Cells.Truncate("hello world", 6), StringComparison.Ordinal);

    [Theory]
    [InlineData("abc", 10)]
    [InlineData("計算機は加算する", 10)]
    [InlineData("adds_🚀", 3)]
    public void FitPad_produces_exact_width(string text, int width)
        => Assert.Equal(width, Cells.Width(Cells.FitPad(text, width)));

    [Fact]
    public void Truncate_zero_or_negative_is_empty()
    {
        Assert.Equal("", Cells.Truncate("anything", 0));
        Assert.Equal("", Cells.Truncate("anything", -3));
    }
}
