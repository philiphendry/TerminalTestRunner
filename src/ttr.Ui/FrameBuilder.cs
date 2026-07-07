using System.Text;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>Per-frame render inputs that live outside <see cref="AppState"/> (instrumentation + animation).</summary>
public readonly record struct RenderInfo(double Fps, double LatencyP95Ms, int SpinnerTick, double RunWallClockMs = 0);

/// <summary>
/// Pure projection of an <see cref="AppState"/> into a cursor-positioned ANSI frame (plan §11.2).
/// Only the viewport slice of each pane is materialised (CLAUDE.md invariant 3) and every rendered
/// line is cell-aware truncated so no line ever exceeds its pane width (invariant 5). The render
/// thread reads only the published <see cref="AppState.Rows"/> snapshot + atomic node fields — it
/// never enumerates the mutable child lists (the Phase 1 hazard).
/// </summary>
public static class FrameBuilder
{
    public const int MinWidth = Layout.MinWidth;
    public const int MinHeight = Layout.MinHeight;

    public static string Build(AppState state, RenderInfo info, int width, int height, Caps? caps = null)
    {
        caps ??= Caps.Full;
        var frame =
            Layout.TooSmall(width, height) ? TooSmall(width, height)
            : state.Modal is { } modal ? ModalRenderer.Render(state, modal, width, height)
            : state.HelpVisible ? HelpRenderer.Render(width, height)
            : Main(state, info, width, height, caps);
        // Single degrade chokepoint (brief M2): ASCII glyph transliteration and/or NO_COLOR strip. Identity for
        // the full-capability default, so every pre-Phase-7 frame stays byte-identical.
        return caps.Apply(frame);
    }

    private static string Main(AppState s, RenderInfo info, int width, int height, Caps caps)
    {
        var sb = new StringBuilder(width * height + 256);
        var hasToast = s.Toast is not null;
        var bodyTop = 2;
        var bodyBottom = hasToast ? height - 2 : height - 1;
        var bodyRows = bodyBottom - bodyTop + 1;

        // Header
        Put(sb, 1, Ansi.Bold + Header(s, info, width) + Ansi.Reset);

        // Body: tree only, tree|detail (right), or tree/detail (beneath).
        if (!s.DetailVisible)
        {
            var tree = BuildTreeLines(s, info, width, bodyRows, caps);
            for (var i = 0; i < bodyRows; i++) Put(sb, bodyTop + i, tree[i]);
        }
        else if (s.DetailOrientation == DetailOrientation.Right)
        {
            var treeWidth = Layout.TreeWidth(s, width);
            var detailWidth = width - treeWidth - 1;
            var tree = BuildTreeLines(s, info, treeWidth, bodyRows, caps);
            var detail = BuildDetailLines(s, detailWidth, bodyRows);
            for (var i = 0; i < bodyRows; i++)
                Put(sb, bodyTop + i, tree[i] + Ansi.Dim + "│" + Ansi.Reset + detail[i]);
        }
        else // Beneath
        {
            var treeRows = Layout.TreeViewportRows(s, width, height);
            var detailRows = bodyRows - treeRows - 1;
            var tree = BuildTreeLines(s, info, width, treeRows, caps);
            for (var i = 0; i < treeRows; i++) Put(sb, bodyTop + i, tree[i]);
            Put(sb, bodyTop + treeRows, Ansi.Dim + new string('─', width) + Ansi.Reset);
            var detail = BuildDetailLines(s, width, detailRows);
            for (var i = 0; i < detailRows; i++) Put(sb, bodyTop + treeRows + 1 + i, detail[i]);
        }

        // Toast (its own line, never overlapping the footer hints — brief M6).
        if (hasToast)
            Put(sb, height - 1, Ansi.Yellow + Cells.FitPad("• " + s.Toast, width) + Ansi.Reset);

        // Footer
        Put(sb, height, Ansi.Dim + Footer(s, width) + Ansi.Reset);
        return sb.ToString();
    }

    // --- Header / footer --------------------------------------------------------

    private static string Header(AppState s, RenderInfo info, int width)
    {
        var flags = string.Concat(
            s.FailedOnly ? "f" : "", s.ShowDurations ? "t" : "",
            s.DetailVisible ? "s" : "", s.DetailVisible && s.WordWrap ? "w" : "");
        var wall = s.ShowDurations || s.Running
            ? $" · {DetailComposer.FormatDuration(TimeSpan.FromMilliseconds(info.RunWallClockMs))}"
            : "";
        var watch = s.Watch != WatchKind.Off ? $" · watch: {WatchSegment(s)}" : "";
        // The restore notice appears only after a --continue restore (null otherwise → non-restore headers
        // stay byte-identical). It is dropped from the header once a run replaces every restored result.
        var restore = s.RestoreNotice is { } rn ? $" · {rn}" : "";
        var left = $"ttr · {s.ScenarioName} · {s.TotalTests} tests · " +
                   $"{s.Passed}✓ {s.Failed}✗ {s.Skipped}⊘" +
                   (s.Running ? $" · running {s.RunningCount}" : "") +
                   wall +
                   watch +
                   restore +
                   (flags.Length > 0 ? $" · [{flags}]" : "");
        var right = $"fps {info.Fps:0} · p95 {info.LatencyP95Ms:0}ms";

        if (Cells.Width(left) + 1 + Cells.Width(right) <= width)
        {
            var gap = width - Cells.Width(left) - Cells.Width(right);
            return left + new string(' ', gap) + right;
        }
        return Cells.FitPad(left, width);
    }

    /// <summary>The watch header state (plan §9, brief M1): idle / change detected / building &lt;proj&gt; /
    /// running (n/m) / queued (change during run). building and running derive from the run/build flags;
    /// the coordinator supplies the change-detected / queued / idle activity.</summary>
    private static string WatchSegment(AppState s)
    {
        if (s.Running)
            return s.WatchActivity == WatchActivity.Queued
                ? "queued (change during run)"
                : $"running ({RunDone(s)}/{s.RunTotal})";
        if (s.Busy)
        {
            var proj = BuildingProject(s);
            return proj is null ? "building" : $"building {proj}";
        }
        return s.WatchActivity switch
        {
            WatchActivity.ChangeDetected or WatchActivity.Queued => "change detected",
            _ => "idle",
        };
    }

    private static int RunDone(AppState s) => Math.Max(0, s.RunTotal - s.RunningCount - s.Root.Queued);

    private static string? BuildingProject(AppState s)
    {
        foreach (var child in s.Root.Children)
            if (child.BuildPhase == BuildPhase.Building) return child.Name;
        return null;
    }

    private static string Footer(AppState s, int width)
    {
        var focus = s.DetailVisible ? (s.Focus == PaneFocus.Detail ? " · focus:detail" : " · focus:tree") : "";
        var text = "↑↓ move · →← expand · s detail · b dock · w wrap · f failed · t times · " +
                   "r/R rerun · o open · Tab focus · ? help · q quit" + focus;
        return Cells.FitPad(text, width);
    }

    // --- Tree pane --------------------------------------------------------------

    private static List<string> BuildTreeLines(AppState s, RenderInfo info, int width, int rowCount, Caps caps)
    {
        var lines = new List<string>(rowCount);
        var rows = s.Rows;
        var scroll = Math.Clamp(s.ScrollOffset, 0, Math.Max(0, rows.Count - 1));
        var treeFocused = !s.DetailVisible || s.Focus == PaneFocus.Tree;

        for (var i = 0; i < rowCount; i++)
        {
            var rowIndex = scroll + i;
            if (rowIndex < rows.Count)
            {
                var row = rows[rowIndex];
                var selected = s.Selection is { } sel && sel.Equals(row.Node.Id);
                lines.Add(TreeRow(row, selected, treeFocused, s.ShowDurations, info.SpinnerTick, width, caps));
            }
            else
            {
                lines.Add(new string(' ', width));
            }
        }
        return lines;
    }

    private static string TreeRow(FlatRow row, bool selected, bool treeFocused, bool showDur, int tick, int width, Caps caps)
    {
        var node = row.Node;
        var durCol = showDur && width >= 28 ? 8 : 0;
        var countsCol = width >= 44 ? 16 : width >= 30 ? 9 : 0;
        var indentCells = Math.Min(row.Depth * 2, Math.Max(0, width - 4));
        // Colourless staleness (brief M2): a grey dim can't show, so a stale row is marked with a leading '~'.
        // It costs one cell, taken from the name budget so counts/duration columns stay aligned.
        var stalePrefix = caps.StalePrefix && Glyphs.IsStaleForDisplay(node) ? "~" : "";
        var nameCol = Math.Max(0, width - indentCells - 2 - stalePrefix.Length - countsCol - durCol);

        var (glyph, color) = Glyphs.ForNode(node, tick);
        // Build state labels apply to a project node even before it has children (discovery gated on
        // build). Notice nodes and test leaves carry no counts.
        var counts = node.BuildPhase == BuildPhase.Building ? "building"
            : node.BuildPhase == BuildPhase.Failed ? "build failed"
            : node.IsLeaf ? "" : BranchCounts(node);
        var dur = durCol > 0 ? DetailComposer.FormatDuration(node.IsLeaf ? node.Duration : node.RollupDuration) : "";

        var indent = new string(' ', indentCells);
        var nameCell = Cells.FitPad(node.Name, nameCol);
        var countsCell = countsCol > 0 ? Cells.PadLeft(counts, countsCol) : "";
        var durCell = durCol > 0 ? Cells.PadLeft(dur, durCol) : "";

        if (selected)
        {
            var plain = indent + stalePrefix + glyph + " " + nameCell + countsCell + durCell;
            return (treeFocused ? Ansi.Reverse : Ansi.Bold) + Cells.FitPad(plain, width) + Ansi.Reset;
        }

        var sb = new StringBuilder();
        sb.Append(indent);
        sb.Append(stalePrefix);
        sb.Append(color).Append(glyph).Append(Ansi.Reset);
        sb.Append(' ');
        sb.Append(nameCell);
        if (countsCol > 0) sb.Append(CountsColor(node)).Append(countsCell).Append(Ansi.Reset);
        if (durCol > 0) sb.Append(Ansi.Dim).Append(durCell).Append(Ansi.Reset);
        return sb.ToString();
    }

    private static string BranchCounts(TestNode n)
    {
        var parts = new List<string>(5);
        if (n.Failed > 0) parts.Add($"{n.Failed}✗");
        if (n.Passed > 0) parts.Add($"{n.Passed}✓");
        if (n.Skipped > 0) parts.Add($"{n.Skipped}⊘");
        if (n.Running > 0) parts.Add($"{n.Running}◍");
        if (n.Queued > 0) parts.Add($"{n.Queued}{Glyphs.Queued}");
        if (n.NotRun > 0 && n.Running == 0 && n.Queued == 0) parts.Add($"{n.NotRun}○");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private static string CountsColor(TestNode n) =>
        n.BuildPhase == BuildPhase.Building ? Ansi.Yellow
        : n.BuildPhase == BuildPhase.Failed ? Ansi.Red
        : n.Failed > 0 ? Ansi.Red : n.AnyRunning ? Ansi.Cyan : n.NotRun > 0 ? Ansi.Grey : Ansi.Green;

    // --- Detail pane ------------------------------------------------------------

    private static List<string> BuildDetailLines(AppState s, int width, int rowCount)
    {
        var lines = new List<string>(rowCount);
        var node = SelectedNode(s);
        var focused = s.Focus == PaneFocus.Detail;

        // Title row.
        var title = node is null ? "Detail" : $"Detail · {node.Name}";
        var titleStyle = focused ? Ansi.Reverse : Ansi.Dim;
        lines.Add(titleStyle + Cells.FitPad(title, width) + Ansi.Reset);

        var contentRows = rowCount - 1;
        var display = node is null
            ? new List<(string Text, bool Underline, bool Header)>()
            : WrapLogical(DetailComposer.Compose(node), width, s.WordWrap);

        var scroll = Math.Clamp(s.DetailScroll, 0, Math.Max(0, display.Count - 1));
        for (var i = 0; i < contentRows; i++)
        {
            var idx = scroll + i;
            if (idx < display.Count)
            {
                var (text, underline, header) = display[idx];
                var cell = Cells.FitPad(text, width);
                var style = underline ? Ansi.Underline : header ? Ansi.Bold : "";
                lines.Add(style.Length > 0 ? style + cell + Ansi.Reset : cell);
            }
            else
            {
                lines.Add(new string(' ', width));
            }
        }
        return lines;

        static List<(string, bool, bool)> WrapLogical(
            IReadOnlyList<DetailLine> logical, int w, bool wrap)
        {
            var outLines = new List<(string, bool, bool)>(logical.Count);
            foreach (var l in logical)
            {
                if (wrap)
                    foreach (var chunk in Cells.Wrap(l.Text, w))
                        outLines.Add((chunk, l.Underline, l.Header));
                else
                    outLines.Add((Cells.Fit(l.Text, w), l.Underline, l.Header));
            }
            return outLines;
        }
    }

    internal static TestNode? SelectedNode(AppState s)
    {
        if (s.Selection is not { } sel) return null;
        foreach (var row in s.Rows)
            if (row.Node.Id.Equals(sel)) return row.Node;
        return null;
    }

    // --- Shared -----------------------------------------------------------------

    /// <summary>
    /// Write a line at 1-based <paramref name="row"/>. Clears the line BEFORE writing the content, not
    /// after: a full-width line ends with a glyph in the terminal's last column (pending-wrap state), and
    /// a trailing <c>ClearToEol</c> there erases that glyph on strict terminals (the "build faile" / "8"
    /// clipping). Clearing first, then writing, leaves the last glyph intact — the next line's absolute
    /// cursor move cancels the pending wrap. (ANSI-stripped snapshots are unaffected by the reorder.)
    /// </summary>
    private static void Put(StringBuilder sb, int row, string content)
    {
        sb.Append(Ansi.MoveTo(row, 1)).Append(Ansi.ClearToEol).Append(content);
    }

    private static string TooSmall(int width, int height)
    {
        const string msg = "terminal too small (need ≥40×10)";
        var sb = new StringBuilder();
        var mid = Math.Max(1, height / 2);
        for (var line = 1; line <= Math.Max(1, height); line++)
        {
            sb.Append(Ansi.MoveTo(line, 1)).Append(Ansi.ClearToEol);   // clear first (see Put)
            if (line == mid && width > 0)
            {
                var text = Cells.Fit(msg, width);
                var pad = Math.Max(0, (width - Cells.Width(text)) / 2);
                sb.Append(new string(' ', pad)).Append(text);
            }
        }
        return sb.ToString();
    }
}
