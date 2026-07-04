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

    public static string SpinnerFrame(int tick) => Spinner[((tick % Spinner.Length) + Spinner.Length) % Spinner.Length];

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
