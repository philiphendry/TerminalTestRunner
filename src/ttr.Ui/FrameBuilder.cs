using System.Text;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>Per-frame render inputs that live outside <see cref="AppState"/> (instrumentation + animation).</summary>
public readonly record struct RenderInfo(double Fps, double LatencyP95Ms, int SpinnerTick);

/// <summary>
/// Pure projection of an <see cref="AppState"/> into a cursor-positioned ANSI frame (plan §11.2).
/// Only the viewport slice is materialised (CLAUDE.md invariant 3), and every rendered line is
/// cell-aware truncated so no line ever exceeds the pane width (invariant 5). Being pure, it is
/// unit-testable without a terminal.
/// </summary>
public static class FrameBuilder
{
    // The minimum must be strictly exceeded: exactly 40×10 shows the placeholder (brief AC3;
    // plan §11.2 "below roughly 40×10").
    public const int MinWidth = 40;
    public const int MinHeight = 10;

    public static string Build(AppState state, RenderInfo info, int width, int height)
    {
        if (width <= MinWidth || height <= MinHeight)
            return TooSmall(width, height);

        var sb = new StringBuilder(width * height + 64);
        var visibleRows = height - 2; // 1 header + 1 footer

        // Header (row 1)
        sb.Append(Ansi.MoveTo(1, 1));
        sb.Append(Ansi.Bold);
        sb.Append(Header(state, info, width));
        sb.Append(Ansi.Reset);
        sb.Append(Ansi.ClearToEol);

        // Tree (rows 2 .. height-1). Read the reducer-published snapshot — never enumerate the
        // mutable child lists from the render thread (that races the reducer's inserts).
        var rows = state.Rows;
        var scroll = Math.Clamp(state.ScrollOffset, 0, Math.Max(0, rows.Count - 1));
        for (var i = 0; i < visibleRows; i++)
        {
            var line = 2 + i;
            sb.Append(Ansi.MoveTo(line, 1));
            var rowIndex = scroll + i;
            if (rowIndex < rows.Count)
            {
                var row = rows[rowIndex];
                var selected = state.Selection is { } sel && sel.Equals(row.Node.Id);
                sb.Append(TreeRow(row, selected, info.SpinnerTick, width));
            }
            else
            {
                sb.Append(Ansi.ClearToEol);
            }
        }

        // Footer (row height)
        sb.Append(Ansi.MoveTo(height, 1));
        sb.Append(Ansi.Dim);
        sb.Append(Footer(state, width));
        sb.Append(Ansi.Reset);
        sb.Append(Ansi.ClearToEol);

        return sb.ToString();
    }

    private static string Header(AppState s, RenderInfo info, int width)
    {
        var left = $"ttr · {s.ScenarioName} · {s.TotalTests} tests · " +
                   $"{s.Passed}✓ {s.Failed}✗ {s.Skipped}⊘" +
                   (s.Running ? $" · running {s.RunningCount}" : "");
        var right = $"fps {info.Fps:0} · p95 {info.LatencyP95Ms:0}ms";

        if (Cells.Width(left) + 1 + Cells.Width(right) <= width)
        {
            var gap = width - Cells.Width(left) - Cells.Width(right);
            return left + new string(' ', gap) + right;
        }
        return Cells.FitPad(left, width);
    }

    private static string Footer(AppState s, int width)
    {
        var text = s.FooterMessage
                   ?? "↑↓/jk move · →← e/E expand/collapse · q quit · ? help";
        return Cells.FitPad(text, width);
    }

    private static string TreeRow(FlatRow row, bool selected, int tick, int width)
    {
        var node = row.Node;
        var indent = new string(' ', row.Depth * 2);
        var (glyph, color) = node.IsLeaf
            ? Glyphs.ForLeaf(node.Status, tick)
            : Glyphs.ForBranch(node, tick);

        var counts = node.IsLeaf ? "" : BranchCounts(node);
        var countsWidth = counts.Length == 0 ? 0 : Cells.Width(counts) + 1; // +1 leading space
        var prefixWidth = Cells.Width(indent) + 1 /*glyph*/ + 1 /*space*/;
        var labelBudget = Math.Max(0, width - prefixWidth - countsWidth);
        var label = Cells.Fit(node.Name, labelBudget);

        var w = new RowWriter();
        if (selected)
        {
            // Full-width reverse bar; no inner colour resets (they would cancel the reverse).
            w.Text(indent);
            w.Text(glyph);
            w.Text(" ");
            w.Text(label);
            if (counts.Length > 0) { w.Text(" "); w.Text(counts); }
            return Ansi.Reverse + w.Build(width) + Ansi.Reset;
        }

        w.Text(indent);
        w.Colored(glyph, color);
        w.Text(" ");
        w.Text(label);
        if (counts.Length > 0) { w.Text(" "); w.Colored(counts, CountsColor(node)); }
        return w.Build(width) + Ansi.ClearToEol;
    }

    private static string BranchCounts(TestNode n)
    {
        var parts = new List<string>(5);
        if (n.Failed > 0) parts.Add($"{n.Failed}✗");
        if (n.Passed > 0) parts.Add($"{n.Passed}✓");
        if (n.Skipped > 0) parts.Add($"{n.Skipped}⊘");
        if (n.Running > 0) parts.Add($"{n.Running}◍");
        if (n.Queued > 0) parts.Add($"{n.Queued}◌");
        var notRun = n.NotRun;
        if (notRun > 0 && n.Running == 0 && n.Queued == 0) parts.Add($"{notRun}○");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private static string CountsColor(TestNode n) =>
        n.Failed > 0 ? Ansi.Red : n.AnyRunning ? Ansi.Cyan : n.NotRun > 0 ? Ansi.Grey : Ansi.Green;

    private static string TooSmall(int width, int height)
    {
        // Fill the whole screen so no stale content leaks; centre a short message.
        const string msg = "terminal too small (need ≥40×10)";
        var sb = new StringBuilder();
        var mid = Math.Max(1, height / 2);
        for (var line = 1; line <= Math.Max(1, height); line++)
        {
            sb.Append(Ansi.MoveTo(line, 1));
            if (line == mid && width > 0)
            {
                var text = Cells.Fit(msg, width);
                var pad = Math.Max(0, (width - Cells.Width(text)) / 2);
                sb.Append(new string(' ', pad)).Append(text);
            }
            sb.Append(Ansi.ClearToEol);
        }
        return sb.ToString();
    }

    /// <summary>Builds one line while tracking visible cell width separately from ANSI escape bytes.</summary>
    private sealed class RowWriter
    {
        private readonly StringBuilder _sb = new();
        private int _w;

        public void Text(string s)
        {
            _sb.Append(s);
            _w += Cells.Width(s);
        }

        public void Colored(string s, string color)
        {
            _sb.Append(color).Append(s).Append(Ansi.Reset);
            _w += Cells.Width(s);
        }

        public string Build(int targetWidth)
        {
            if (_w < targetWidth) _sb.Append(new string(' ', targetWidth - _w));
            return _sb.ToString();
        }
    }
}
