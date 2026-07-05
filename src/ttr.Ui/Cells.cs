using Spectre.Console.Rendering;

namespace Ttr.Ui;

/// <summary>
/// Cell-aware width measurement and truncation (CLAUDE.md invariant 5). Widths are measured in
/// terminal cells — CJK and emoji are 2 cells — via Spectre's public
/// <see cref="Segment.CellCount()"/> (the measuring-library role from plan §11.1). No rendered
/// string may ever exceed its pane width, so every line goes through <see cref="Fit"/> before it
/// is written.
/// </summary>
public static class Cells
{
    private const char Ellipsis = '…';

    /// <summary>Width of <paramref name="text"/> in terminal cells.</summary>
    public static int Width(string text) => new Segment(text).CellCount();

    /// <summary>
    /// Truncates <paramref name="text"/> so it fits within <paramref name="maxCells"/> cells,
    /// appending '…' (itself 1 cell) when it had to cut. Handles double-width runes at the
    /// boundary without ever overshooting (drops the whole rune, pads if that leaves a gap).
    /// </summary>
    public static string Truncate(string text, int maxCells)
    {
        if (maxCells <= 0) return string.Empty;
        if (Width(text) <= maxCells) return text;
        if (maxCells == 1) return Ellipsis.ToString();

        var budget = maxCells - 1; // reserve one cell for the ellipsis
        var used = 0;
        var end = 0;
        var runes = text.EnumerateRunes();
        foreach (var rune in runes)
        {
            var w = new Segment(rune.ToString()).CellCount();
            if (used + w > budget) break;
            used += w;
            end += rune.Utf16SequenceLength;
        }

        var head = text[..end];
        // A double-width rune straddling the boundary can leave `used` one short of `budget`;
        // pad so the ellipsis lands in a stable column and the field width is exact.
        var pad = budget - used;
        return pad > 0 ? head + new string(' ', pad) + Ellipsis : head + Ellipsis;
    }

    /// <summary>Truncate to fit, then right-pad with spaces to exactly <paramref name="width"/> cells.</summary>
    public static string FitPad(string text, int width)
    {
        if (width <= 0) return string.Empty;
        var fitted = Truncate(text, width);
        var pad = width - Width(fitted);
        return pad > 0 ? fitted + new string(' ', pad) : fitted;
    }

    /// <summary>Truncate to fit within <paramref name="width"/> cells (no padding).</summary>
    public static string Fit(string text, int width) => Truncate(text, width);

    /// <summary>Truncate to fit, then LEFT-pad with spaces to exactly <paramref name="width"/> cells (right-aligned).</summary>
    public static string PadLeft(string text, int width)
    {
        if (width <= 0) return string.Empty;
        var fitted = Truncate(text, width);
        var pad = width - Width(fitted);
        return pad > 0 ? new string(' ', pad) + fitted : fitted;
    }

    /// <summary>
    /// Cell-aware hard wrap of <paramref name="text"/> into lines no wider than <paramref name="width"/>
    /// cells (double-width runes never split a cell). An empty string yields one empty line.
    /// </summary>
    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (width <= 0) { lines.Add(string.Empty); return lines; }

        var start = 0;      // rune-index start of the current line, in UTF-16 offset
        var used = 0;
        var lineStart = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = new Segment(rune.ToString()).CellCount();
            if (used + w > width)
            {
                lines.Add(text[lineStart..start]);
                lineStart = start;
                used = 0;
            }
            used += w;
            start += rune.Utf16SequenceLength;
        }
        lines.Add(text[lineStart..]);
        return lines;
    }
}
