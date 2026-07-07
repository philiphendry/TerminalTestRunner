using Ttr.Core;
using Ttr.Ui;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// The branch rollup glyph (plan §11.2). A branch mirrors its leaves: it shows the static hourglass while its
/// descendants are only Queued (awaiting execution), exactly as an individual queued leaf does, and switches
/// to the animated spinner only once a descendant is actually Running. The spinner is thus reserved for real
/// in-flight work, so a parent never implies activity while its subtree is merely pending.
/// </summary>
public class BranchGlyphTests
{
    private static TestNode? Find(TestNode n, TestCaseId id)
        => n.Id.Equals(id) ? n : n.Children.Select(c => Find(c, id)).FirstOrDefault(x => x is not null);

    private static TestNode Class(AppState s) => Find(s.Root, BranchId(TestNodeKind.Class, Project, Tfm, "N", "C"))!;

    [Fact]
    public void Branch_shows_hourglass_while_queued_and_spins_only_when_a_child_runs()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discover(AppState.Initial("t"), a, b);

        // Rerun-all marks both leaves Queued and launches; A then runs and finishes while B stays Queued —
        // the common between-tests gap where NO descendant is momentarily Running.
        s = Feed(s, new AppEvent.KeyPressed(Char('R')));
        s = Feed(s, new AppEvent.TestStarted(a), new AppEvent.TestFinished(a, TestOutcome.Passed, TimeSpan.Zero));

        var cls = Class(s);
        Assert.Equal(0, cls.Running);   // nothing running at this instant
        Assert.Equal(1, cls.Queued);    // B is still queued
        Assert.Equal(1, cls.Passed);    // A already ran

        // The branch shows the hourglass — the same glyph its queued leaf shows — not the spinner.
        Assert.Equal(Glyphs.Queued, Glyphs.ForBranch(cls, 0).Glyph);
        Assert.Equal(Glyphs.Queued, Glyphs.ForNode(cls, 0).Glyph);
        Assert.Equal(Glyphs.Queued, Glyphs.ForLeaf(Find(s.Root, b.Id)!.Status, 0).Glyph);
    }

    [Fact]
    public void Branch_spins_once_a_descendant_is_running()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discover(AppState.Initial("t"), a, b);
        s = Feed(s, new AppEvent.KeyPressed(Char('R')));
        // B starts running while A stays queued — a running descendant flips the branch to the spinner.
        s = Feed(s, new AppEvent.TestStarted(b));

        var cls = Class(s);
        Assert.True(cls.AnyRunning);
        Assert.Equal(Glyphs.SpinnerFrame(0), Glyphs.ForBranch(cls, 0).Glyph);
    }
}
