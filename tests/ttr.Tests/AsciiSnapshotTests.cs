using System.Linq;
using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;
using static VerifyXunit.Verifier;

namespace Ttr.Tests;

/// <summary>
/// The ASCII fallback snapshot lane (brief M2, AC3): the same fake scenarios the Unicode
/// <see cref="SnapshotTests"/> cover, rendered in the ASCII glyph tier, so every main screen is proven to
/// degrade legibly with no braille/box/arrow glyphs surviving. The verified baselines are the Unicode frames
/// transliterated by <see cref="Ttr.Ui.Caps"/>; the existing (Unicode) snapshots are untouched. A guard test
/// additionally asserts NO character from ttr's mapped glyph vocabulary leaks into an ASCII frame.
/// </summary>
public class AsciiSnapshotTests
{
    private static ConsoleKeyInfo Down => Key('\0', ConsoleKey.DownArrow);
    private static AppState AtFailingLeaf() => Press(Play("files", 7), Down, Down, Down, Down, Down);

    [Fact]
    public Task Ascii_main_tree() =>
        Verify(RenderAscii(Play("files", 7), 100, 30));

    [Fact]
    public Task Ascii_detail_right() =>
        Verify(RenderAscii(Press(AtFailingLeaf(), Char('s')), 100, 30));

    [Fact]
    public Task Ascii_modal_open() =>
        Verify(RenderAscii(Press(AtFailingLeaf(), Char('o')), 100, 30));

    [Fact]
    public Task Ascii_help_overlay() =>
        Verify(RenderAscii(Press(Play("files", 7), Char('?')), 100, 30));

    // ttr's Unicode glyph vocabulary — none of these may survive an ASCII-tier render (they would mojibake on
    // a non-UTF-8 terminal). User content is never mapped, but the fake scenarios use only ASCII test names.
    private const string UnicodeGlyphs = "✓✗○⊘◌◍⚠✖┌┐└┘─│↑↓←→↔·—§≥≤≈×•…⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    [Fact]
    public void Ascii_frames_contain_no_unicode_glyphs()
    {
        foreach (var frame in new[]
        {
            RenderAscii(Play("files", 7), 100, 30),
            RenderAscii(Press(AtFailingLeaf(), Char('s')), 100, 30),
            RenderAscii(Press(AtFailingLeaf(), Char('o')), 100, 30),
            RenderAscii(Press(Play("files", 7), Char('?')), 100, 30),
        })
        {
            var leaked = frame.Where(c => UnicodeGlyphs.Contains(c)).Distinct().ToArray();
            Assert.True(leaked.Length == 0, $"ASCII frame leaked Unicode glyph(s): {string.Join(" ", leaked)}");
        }
    }

    [Fact]
    public void Full_mode_keeps_unicode_glyphs()
    {
        // Regression guard: the default (full) tier is unchanged — the ✓/✗ glyphs are still Unicode.
        var frame = Render(Play("files", 7), 100, 30);
        Assert.Contains('✗', frame);
        Assert.Contains('✓', frame);
    }
}
