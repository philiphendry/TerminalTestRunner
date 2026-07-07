using Ttr.Core;
using Ttr.Ui;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// The branch rollup glyph (plan §11.2). A branch with only Queued (none currently Running) descendants has
/// genuine pending work while a run is live, so it must show the animated spinner — the spinner tick advances
/// only during a run, so a static hourglass here would look frozen between one test finishing and the next
/// starting (regression: parents left with a "stopped spinner" mid-run). The static hourglass is reserved for
/// the queued-but-no-run-active case; the per-leaf Running/Queued distinction is unaffected.
/// </summary>
public class BranchGlyphTests
{
    private static TestNode? Find(TestNode n, TestCaseId id)
        => n.Id.Equals(id) ? n : n.Children.Select(c => Find(c, id)).FirstOrDefault(x => x is not null);

    [Fact]
    public void Queued_branch_spins_while_a_run_is_active_but_shows_the_hourglass_when_idle()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discover(AppState.Initial("t"), a, b);

        // Rerun-all marks both leaves Queued and launches (Running = true); then A runs and finishes while B
        // is still Queued — the common between-tests gap where NO leaf is momentarily Running.
        s = Feed(s, new AppEvent.KeyPressed(Char('R')));
        s = Feed(s, new AppEvent.TestStarted(a), new AppEvent.TestFinished(a, TestOutcome.Passed, TimeSpan.Zero));

        var cls = Find(s.Root, BranchId(TestNodeKind.Class, Project, Tfm, "N", "C"))!;
        Assert.True(s.Running);
        Assert.Equal(0, cls.Running);      // nothing running at this instant
        Assert.Equal(1, cls.Queued);       // B is still queued
        Assert.Equal(1, cls.Passed);       // A already ran

        // During the active run the branch shows the animated spinner, not a frozen hourglass.
        Assert.Equal(Glyphs.SpinnerFrame(0), Glyphs.ForBranch(cls, 0, runActive: true).Glyph);
        Assert.Equal(Glyphs.SpinnerFrame(0), Glyphs.ForNode(cls, 0, runActive: true).Glyph);

        // With no run active (an idle pending state), the static hourglass shows instead of a stalled spinner.
        Assert.Equal(Glyphs.Queued, Glyphs.ForBranch(cls, 0, runActive: false).Glyph);

        // The per-leaf distinction is untouched: the individual not-yet-started leaf keeps the hourglass.
        Assert.Equal(Glyphs.Queued, Glyphs.ForLeaf(Find(s.Root, b.Id)!.Status, 0).Glyph);
    }

    [Fact]
    public void A_genuinely_running_branch_always_spins()
    {
        var a = Id("N", "C", "A");
        var s = Discover(AppState.Initial("t"), a);
        s = Feed(s, new AppEvent.TestStarted(a));   // A is Running

        var cls = Find(s.Root, BranchId(TestNodeKind.Class, Project, Tfm, "N", "C"))!;
        Assert.True(cls.AnyRunning);
        // AnyRunning short-circuits to the spinner regardless of the runActive flag.
        Assert.Equal(Glyphs.SpinnerFrame(0), Glyphs.ForBranch(cls, 0, runActive: true).Glyph);
        Assert.Equal(Glyphs.SpinnerFrame(0), Glyphs.ForBranch(cls, 0, runActive: false).Glyph);
    }
}
