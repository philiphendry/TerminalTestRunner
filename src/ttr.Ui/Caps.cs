using System.Text;
using System.Text.RegularExpressions;

namespace Ttr.Ui;

/// <summary>
/// Terminal rendering capabilities + the degrade transform (plan §11.2, brief M2). Two independent axes:
/// <list type="bullet">
///   <item><see cref="Unicode"/> — Unicode glyphs (braille spinner, ✓/✗/○/⊘, box drawing) when the locale is
///     UTF-8; otherwise the ASCII fallback set (<c>|/-\</c> spinner, <c>+ x o s</c> statuses, ASCII box).</item>
///   <item><see cref="Color"/> — ANSI colour (SGR 3x/9x). <c>NO_COLOR</c> turns it off; the stale overlay then
///     falls back from a grey dim to a <c>~</c> prefix (brief M2), since dim needs colour.</item>
/// </list>
/// The renderers always emit the full Unicode + colour frame; <see cref="Apply"/> is the single chokepoint
/// that degrades the finished frame — a whitelist glyph transliteration (every mapped glyph is 1 cell → 1 cell,
/// so the invariant-5 width maths is preserved and only ttr's OWN glyph vocabulary is touched, never user test
/// names) and/or a colour-code strip that keeps the non-colour attributes (bold/dim/reverse/underline) a
/// <c>NO_COLOR</c> terminal still honours. <see cref="Full"/> (the default everywhere) is the identity, so every
/// pre-Phase-7 frame stays byte-identical.
/// </summary>
public sealed partial record Caps(bool Unicode, bool Color)
{
    public static readonly Caps Full = new(true, true);

    /// <summary>True when the stale overlay must use a <c>~</c> prefix instead of a grey dim (colourless mode).</summary>
    public bool StalePrefix => !Color;

    /// <summary>Whether the finished frame needs any degrade transform (false for the full-capability default).</summary>
    public bool NeedsTransform => !Unicode || !Color;

    // Whitelist of ttr's own UI glyphs → 1-cell ASCII equivalents. User content (e.g. CJK test names) has no
    // entry and passes through untouched. Braille spinner frames rotate onto the classic |/-\ ASCII twirl so
    // the spinner still animates (the brief illustrated the ASCII spinner as `*`; a static glyph wouldn't spin).
    private static readonly Dictionary<char, char> AsciiGlyphs = new()
    {
        ['✓'] = '+', ['✗'] = 'x', ['○'] = 'o', ['⊘'] = 's', ['◌'] = '.', ['◍'] = '*',
        ['⚠'] = '!', ['✖'] = 'X',
        ['┌'] = '+', ['┐'] = '+', ['└'] = '+', ['┘'] = '+', ['─'] = '-', ['│'] = '|',
        ['↑'] = '^', ['↓'] = 'v', ['←'] = '<', ['→'] = '>', ['↔'] = '-',
        ['·'] = '-', ['—'] = '-', ['§'] = 'S', ['×'] = 'x', ['•'] = '*', ['…'] = '~',
        ['≥'] = '>', ['≤'] = '<', ['≈'] = '~',
        ['⠋'] = '|', ['⠙'] = '/', ['⠹'] = '-', ['⠸'] = '\\', ['⠼'] = '|',
        ['⠴'] = '/', ['⠦'] = '-', ['⠧'] = '\\', ['⠇'] = '|', ['⠏'] = '/',
    };

    // Colour SGR codes ttr emits (Ansi.Red/Green/Yellow/Blue/Cyan/Grey). Stripping only these keeps
    // reset(0)/bold(1)/dim(2)/underline(4)/reverse(7)/reverse-off(27), which a NO_COLOR terminal still uses.
    [GeneratedRegex("\x1b\\[(?:31|32|33|34|36|90)m")]
    private static partial Regex ColorCode();

    /// <summary>Degrade a finished frame to this capability tier (identity for <see cref="Full"/>).</summary>
    public string Apply(string frame)
    {
        if (!NeedsTransform) return frame;
        if (!Unicode)
        {
            var sb = new StringBuilder(frame.Length);
            foreach (var ch in frame)
                sb.Append(AsciiGlyphs.TryGetValue(ch, out var ascii) ? ascii : ch);
            frame = sb.ToString();
        }
        if (!Color) frame = ColorCode().Replace(frame, "");
        return frame;
    }
}
