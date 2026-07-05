using Ttr.Ui;
using Xunit;

namespace Ttr.Tests;

/// <summary>The <see cref="Caps.Apply"/> degrade transform (brief M2): glyph transliteration + colour strip.</summary>
public class CapsTests
{
    [Fact]
    public void Full_tier_is_identity()
    {
        var frame = $"{Ansi.Red}✗{Ansi.Reset} test · {Ansi.Green}✓{Ansi.Reset}";
        Assert.Equal(frame, Caps.Full.Apply(frame));
    }

    [Fact]
    public void Ascii_tier_transliterates_glyphs_only()
    {
        var ascii = new Caps(Unicode: false, Color: true).Apply("✓ ✗ ○ ⊘ · │ ─ → …");
        Assert.Equal("+ x o s - | - > ~", ascii);
    }

    [Fact]
    public void Ascii_tier_leaves_user_content_untouched()
    {
        // A CJK test name is not in ttr's glyph vocabulary → passes through unchanged.
        var ascii = new Caps(Unicode: false, Color: true).Apply("✓ 名前Test");
        Assert.Equal("+ 名前Test", ascii);
    }

    [Fact]
    public void Colourless_strips_colour_codes_but_keeps_attributes()
    {
        var input = $"{Ansi.Bold}{Ansi.Red}x{Ansi.Reset}{Ansi.Reverse}sel{Ansi.ReverseOff}";
        var outp = new Caps(Unicode: true, Color: false).Apply(input);
        Assert.DoesNotContain(Ansi.Red, outp);
        Assert.DoesNotContain(Ansi.Green, outp);
        Assert.Contains(Ansi.Bold, outp);      // attribute kept
        Assert.Contains(Ansi.Reverse, outp);   // selection highlight kept
    }

    [Fact]
    public void Ascii_transliteration_preserves_cell_width()
    {
        // Every mapped glyph is 1 cell → 1 cell, so a padded field keeps its width (invariant 5).
        var padded = Ansi.Green + "✓" + Ansi.Reset + " name        ";
        var full = Cells.Width(padded);
        var ascii = new Caps(Unicode: false, Color: true).Apply(padded);
        Assert.Equal(full, Cells.Width(ascii));
    }
}
