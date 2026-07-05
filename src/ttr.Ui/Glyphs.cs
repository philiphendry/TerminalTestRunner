using Ttr.Core;

namespace Ttr.Ui;

/// <summary>Status glyphs, the braille spinner, and their colours (plan §11.2).</summary>
public static class Glyphs
{
    /// <summary>Braille spinner frames (each exactly 1 cell).</summary>
    public static readonly string[] Spinner =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public const string Passed = "✓";
    public const string Failed = "✗";
    public const string NotRun = "○";
    public const string Skipped = "⊘";
    public const string Queued = "◌";

    /// <summary>Warning-notice glyph (1 cell) — phantom / unknown-runner / dead-opt-in / zero-tests.</summary>
    public const string Warning = "⚠";

    /// <summary>Error-notice glyph (1 cell), distinct from the test-failed ✗ — unparseable entry / no handshake.</summary>
    public const string Error = "✖";

    public static string SpinnerFrame(int tick) => Spinner[((tick % Spinner.Length) + Spinner.Length) % Spinner.Length];

    /// <summary>
    /// The glyph + colour for any node, honouring Phase 3 states in priority order (brief M1): a build
    /// spinner while building, a red ✗ on build failure, then a warning/error notice glyph (which
    /// overrides the rollup so the diagnostic is visible), else the normal leaf/branch glyph. An
    /// <see cref="NoticeSeverity.Info"/> note does not override the glyph (it is not an alarm — plan §6.2).
    /// </summary>
    public static (string Glyph, string Color) ForNode(TestNode node, int tick)
    {
        if (node.BuildPhase == BuildPhase.Building) return (SpinnerFrame(tick), Ansi.Yellow);
        if (node.BuildPhase == BuildPhase.Failed) return (Failed, Ansi.Red);
        if (node.Notice is { Severity: NoticeSeverity.Error }) return (Error, Ansi.Red);
        if (node.Notice is { Severity: NoticeSeverity.Warning }) return (Warning, Ansi.Yellow);
        var (glyph, color) = node.IsLeaf ? ForLeaf(node.Status, tick) : ForBranch(node, tick);
        if (color != Ansi.Grey && IsStaleForDisplay(node)) color = Ansi.Grey;
        return (glyph, color);
    }

    /// <summary>
    /// Staleness overlay (plan §10/§11.2): keep the ✓/✗ shape but dim its colour. A leaf dims when its own
    /// result is stale; a branch dims only when EVERY resulted leaf under it is stale and nothing is
    /// (re)running — so a branch with a fresh failure among stale siblings still shows red, not grey.
    /// </summary>
    private static bool IsStaleForDisplay(TestNode node)
    {
        if (node.StaleLeaves == 0) return false;
        if (node.IsLeaf) return true;
        if (node.AnyRunning || node.Queued > 0) return false;
        var resulted = node.Passed + node.Failed + node.Skipped;   // stale leaves keep their underlying status
        return resulted > 0 && node.StaleLeaves == resulted;
    }

    /// <summary>The glyph (1 cell) + colour for a leaf status. Running/Queued animate via <paramref name="tick"/>.</summary>
    public static (string Glyph, string Color) ForLeaf(TestStatus status, int tick) => status switch
    {
        TestStatus.Passed => (Passed, Ansi.Green),
        TestStatus.Failed => (Failed, Ansi.Red),
        TestStatus.Skipped => (Skipped, Ansi.Yellow),
        TestStatus.Running => (SpinnerFrame(tick), Ansi.Cyan),
        TestStatus.Queued => (Queued, Ansi.Cyan),
        TestStatus.Stale => (Passed, Ansi.Grey),
        _ => (NotRun, Ansi.Grey),
    };

    /// <summary>The leading glyph + colour for a branch, rolled up from its subtree.</summary>
    public static (string Glyph, string Color) ForBranch(TestNode node, int tick)
    {
        if (node.AnyRunning) return (SpinnerFrame(tick), Ansi.Cyan);
        return node.BranchStatus switch
        {
            TestStatus.Running => (SpinnerFrame(tick), Ansi.Cyan), // queued-but-not-started
            TestStatus.Failed => (Failed, Ansi.Red),
            TestStatus.Passed => (Passed, Ansi.Green),
            TestStatus.Skipped => (Skipped, Ansi.Yellow),
            _ => (NotRun, Ansi.Grey),
        };
    }
}
