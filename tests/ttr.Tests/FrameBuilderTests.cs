using Ttr.Core;
using Ttr.Ui;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

public class FrameBuilderTests
{
    private static AppState WithPathologicalNames()
    {
        var longName = new string('X', 140);
        var s = AppState.Initial("default");
        s = Discover(s,
            Id("Contoso.Sample", "WideTests", longName),
            Id("Contoso.Sample", "WideTests", "計算機は二つの数値を正しく加算する"),
            Id("Contoso.Sample", "WideTests", "adds_🚀_and_✅"),
            Id("Contoso.Sample", "PlainTests", "Passes"));
        return s with { Selection = s.Root.Id };
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(200, 50)]
    [InlineData(100, 30)]
    [InlineData(60, 15)]
    [InlineData(41, 11)]
    public void No_rendered_line_ever_exceeds_the_width(int width, int height)
    {
        var s = WithPathologicalNames();
        var frame = FrameBuilder.Build(s, new RenderInfo(30, 12, 3), width, height);

        foreach (var row in VisibleRows(frame))
            Assert.True(Cells.Width(row) <= width,
                $"row '{row}' had width {Cells.Width(row)} > {width}");
    }

    [Fact]
    public void Frame_writes_exactly_height_rows()
    {
        const int height = 24;
        var frame = FrameBuilder.Build(WithPathologicalNames(), new RenderInfo(0, 0, 0), 80, height);
        Assert.Equal(height, VisibleRows(frame).Count());
    }

    [Theory]
    [InlineData(40, 10)]   // the AC3 boundary — exactly 40×10 is too small
    [InlineData(39, 20)]
    [InlineData(80, 9)]
    [InlineData(20, 5)]
    public void Below_minimum_size_shows_the_placeholder(int width, int height)
    {
        var frame = FrameBuilder.Build(WithPathologicalNames(), new RenderInfo(0, 0, 0), width, height);
        Assert.Contains("terminal too small", frame, StringComparison.Ordinal);
        foreach (var row in VisibleRows(frame))
            Assert.True(Cells.Width(row) <= width);
    }

    [Fact]
    public void Header_shows_scenario_and_totals()
    {
        var s = WithPathologicalNames();
        var frame = FrameBuilder.Build(s, new RenderInfo(30, 12, 0), 120, 24);
        var header = VisibleRows(frame).First();
        Assert.Contains("default", header, StringComparison.Ordinal);
        Assert.Contains("4 tests", header, StringComparison.Ordinal);
        Assert.Contains("fps", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Footer_shows_key_hints()
    {
        var frame = FrameBuilder.Build(WithPathologicalNames(), new RenderInfo(0, 0, 0), 120, 24);
        var footer = VisibleRows(frame).Last();
        Assert.Contains("quit", footer, StringComparison.Ordinal);
    }
}
