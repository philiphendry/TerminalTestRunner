using System.Text;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>
/// Draws a centred bordered box over a cleared screen for the help and 'o' overlays (plan §11.2,
/// §11.5). Callers pass inner rows already laid out to exactly <see cref="Inner"/> cells (ANSI
/// allowed); Overlay adds the border, a title bar, and an optional footer hint, and blanks every
/// cell outside the box so nothing bleeds through.
/// </summary>
public static class Overlay
{
    /// <summary>Inner content width for a box of the given outer width.</summary>
    public static int Inner(int boxWidth) => Math.Max(1, boxWidth - 2);

    public static string Render(
        int screenW, int screenH, int boxW, int boxH, string title, string footer, IReadOnlyList<string> innerRows)
    {
        boxW = Math.Clamp(boxW, 6, screenW);
        boxH = Math.Clamp(boxH, 3, screenH);
        var left = Math.Max(0, (screenW - boxW) / 2);
        var top = Math.Max(0, (screenH - boxH) / 2);
        var inner = boxW - 2;
        var pad = new string(' ', left);

        var sb = new StringBuilder(screenW * screenH + 256);
        for (var row = 1; row <= screenH; row++)
        {
            sb.Append(Ansi.MoveTo(row, 1));
            var r = row - 1 - top;
            if (r < 0 || r >= boxH)
            {
                sb.Append(Ansi.ClearToEol);
                continue;
            }

            sb.Append(pad);
            if (r == 0)
                sb.Append(Ansi.Bold).Append('┌').Append(Bar(title, inner, '─')).Append('┐').Append(Ansi.Reset);
            else if (r == boxH - 1)
                sb.Append(Ansi.Dim).Append('└').Append(Bar(footer, inner, '─')).Append('┘').Append(Ansi.Reset);
            else
            {
                var ci = r - 1;
                var cell = ci < innerRows.Count ? innerRows[ci] : new string(' ', inner);
                sb.Append(Ansi.Dim).Append('│').Append(Ansi.Reset).Append(cell)
                  .Append(Ansi.Dim).Append('│').Append(Ansi.Reset);
            }
            sb.Append(Ansi.ClearToEol);
        }
        return sb.ToString();
    }

    /// <summary>A title/footer bar: " text " embedded in a run of <paramref name="fill"/>, exactly <paramref name="width"/> cells.</summary>
    private static string Bar(string text, int width, char fill)
    {
        if (string.IsNullOrEmpty(text)) return new string(fill, width);
        var label = " " + Cells.Fit(text, Math.Max(0, width - 4)) + " ";
        var labelW = Cells.Width(label);
        var lead = 1;
        var trail = Math.Max(0, width - labelW - lead);
        return new string(fill, lead) + label + new string(fill, trail);
    }
}
