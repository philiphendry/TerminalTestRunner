using System.Text;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>
/// Renders the 'o' file-reference modal (plan §11.5, brief M5): a ~90% centred box titled with the
/// file name + (index/total), a line-number gutter, the referenced line highlighted, and either the
/// off-thread ColorCode spans or a plain first paint. Content is laid out to exactly the inner width
/// (truncate, or wrap when toggled) so it never bleeds — CJK/emoji measured in cells.
/// </summary>
public static class ModalRenderer
{
    private static readonly Dictionary<HlColor, string> Palette = new()
    {
        [HlColor.Default] = "",
        [HlColor.Keyword] = Ansi.Blue,
        [HlColor.Type] = Ansi.Cyan,
        [HlColor.String] = Ansi.Yellow,
        [HlColor.Comment] = Ansi.Grey,
        [HlColor.Number] = Ansi.Green,
        [HlColor.Preprocessor] = Ansi.Grey,
        [HlColor.Identifier] = "",
    };

    public static string Render(AppState s, ModalState m, int width, int height)
    {
        var boxW = Layout.ModalWidth(width);
        var boxH = Layout.ModalHeight(height);
        var inner = Overlay.Inner(boxW);
        var contentRows = Layout.ModalContentRows(height);

        var lineCount = m.Lines.Count;
        var gutter = Math.Max(3, lineCount.ToString().Length) + 1; // digits + trailing space
        var textWidth = Math.Max(1, inner - gutter);

        var rows = new List<string>(contentRows);
        var src = m.Scroll;
        while (rows.Count < contentRows && src < lineCount)
        {
            var lineNo = src + 1;
            var isTarget = m.TargetLine == lineNo;
            var pieces = WrapSourceLine(m, src, textWidth);
            for (var p = 0; p < pieces.Count && rows.Count < contentRows; p++)
                rows.Add(ComposeRow(p == 0 ? lineNo.ToString() : "", gutter, pieces[p], textWidth, isTarget));
            src++;
        }
        while (rows.Count < contentRows) rows.Add(new string(' ', inner));

        var r = m.Current;
        var marker = r.Exists ? "" : "  [unresolved]";
        var title = $"{r.FileName}  ({m.Index + 1}/{m.Refs.Count})" +
                    $"{(r.Line is { } ln ? $"  :line {ln}" : "")}{marker}";
        const string footer = "c/Esc close · n/p file · w wrap · ↑↓ PgUp/PgDn scroll";
        return Overlay.Render(width, height, boxW, boxH, title, footer, rows);
    }

    /// <summary>Break source line <paramref name="index"/> into display pieces: one truncated piece
    /// (wrap off) or as many as needed (wrap on), each ≤ <paramref name="textWidth"/> cells.</summary>
    private static List<Piece> WrapSourceLine(ModalState m, int index, int textWidth)
    {
        var spans = SpansFor(m, index);
        var pieces = new List<Piece>();
        var current = new Piece();
        var used = 0;

        foreach (var span in spans)
        {
            foreach (var rune in span.Text.EnumerateRunes())
            {
                var s = rune.ToString();
                var rw = Cells.Width(s);
                if (used + rw > textWidth)
                {
                    pieces.Add(current);
                    if (!m.Wrap) return pieces;   // truncate to one visual line
                    current = new Piece();
                    used = 0;
                }
                current.Add(s, span.Color);
                used += rw;
            }
        }
        pieces.Add(current);
        return pieces;
    }

    private static IReadOnlyList<HlSpan> SpansFor(ModalState m, int index)
    {
        if (m.Highlighted is { } hl && index < hl.Count) return hl[index];
        var text = index < m.Lines.Count ? m.Lines[index] : "";
        return [new HlSpan(text, HlColor.Default)];
    }

    private static string ComposeRow(string num, int gutter, Piece piece, int textWidth, bool isTarget)
    {
        var numCell = Cells.PadLeft(num, gutter - 1) + " ";
        if (isTarget)
            // Highlight the referenced line: reverse the whole row (plain text, unambiguous).
            return Ansi.Reverse + numCell + Cells.FitPad(piece.Plain, textWidth) + Ansi.Reset;

        return Ansi.Grey + numCell + Ansi.Reset + piece.ToPaddedString(textWidth);
    }

    /// <summary>One display line accumulating colour-coalesced slices to an exact cell width.</summary>
    private sealed class Piece
    {
        private readonly StringBuilder _ansi = new();
        private readonly StringBuilder _plain = new();
        private int _cells;
        private HlColor _cur = HlColor.Default;
        private bool _open;

        public string Plain => _plain.ToString();

        public void Add(string text, HlColor color)
        {
            if (text.Length == 0) return;
            if (color != _cur || (!_open && Palette[color].Length > 0))
            {
                if (_open) { _ansi.Append(Ansi.Reset); _open = false; }
                var code = Palette[color];
                if (code.Length > 0) { _ansi.Append(code); _open = true; }
                _cur = color;
            }
            _ansi.Append(text);
            _plain.Append(text);
            _cells += Cells.Width(text);
        }

        public string ToPaddedString(int width)
        {
            var s = _open ? _ansi + Ansi.Reset : _ansi.ToString();
            var pad = width - _cells;
            return pad > 0 ? s + new string(' ', pad) : s;
        }
    }
}
