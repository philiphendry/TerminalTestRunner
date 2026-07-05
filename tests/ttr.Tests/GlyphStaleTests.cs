using Ttr.Core;
using Ttr.Ui;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// The Stale colour overlay (plan §11.2) — snapshots are ANSI-stripped, so the dimming is pinned here: a
/// stale leaf dims to grey (keeping its ✓/✗ shape), a branch dims only when EVERY resulted leaf under it is
/// stale, and a fresh failure among stale siblings keeps the branch red.
/// </summary>
public class GlyphStaleTests
{
    private static TestNode? Find(TestNode n, TestCaseId id)
        => n.Id.Equals(id) ? n : n.Children.Select(c => Find(c, id)).FirstOrDefault(x => x is not null);

    private static string Color(AppState s, TestCaseId id) => Glyphs.ForNode(Find(s.Root, id)!, 0).Color;

    [Fact]
    public void Stale_leaf_dims_but_fresh_failure_keeps_branch_red()
    {
        var pass = Id("N", "C", "Pass");
        var fail = Id("N", "C", "Fail");
        var stale = Id("N", "C", "StalePass");
        var s = Discover(AppState.Initial("t"), pass, fail, stale);
        s = Feed(s, new AppEvent.SessionRestored(
        [
            new RestoredResult(pass.Id, TestStatus.Passed, TimeSpan.Zero, false, Stale: false),
            new RestoredResult(fail.Id, TestStatus.Failed, TimeSpan.Zero, false, Stale: false),
            new RestoredResult(stale.Id, TestStatus.Passed, TimeSpan.Zero, false, Stale: true),
        ], new RestoredUi(false, false, false, false, false, s.Expanded.Select(i => i.Value).ToList(), null, 0, 0), "now"));

        Assert.Equal(Ansi.Green, Color(s, pass.Id));   // fresh pass
        Assert.Equal(Ansi.Grey, Color(s, stale.Id));   // stale pass → dim
        Assert.Equal(Ansi.Red, Color(s, fail.Id));     // fresh fail
        // The class branch has a fresh failure among a stale sibling → stays red, not dimmed.
        Assert.Equal(Ansi.Red, Color(s, BranchId(TestNodeKind.Class, Project, Tfm, "N", "C")));
    }

    [Fact]
    public void Branch_dims_when_all_resulted_leaves_are_stale()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discover(AppState.Initial("t"), a, b);
        s = Feed(s, new AppEvent.SessionRestored(
        [
            new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.Zero, false, Stale: true),
            new RestoredResult(b.Id, TestStatus.Passed, TimeSpan.Zero, false, Stale: true),
        ], new RestoredUi(false, false, false, false, false, s.Expanded.Select(i => i.Value).ToList(), null, 0, 0), "now"));

        Assert.Equal(Ansi.Grey, Color(s, BranchId(TestNodeKind.Class, Project, Tfm, "N", "C")));
    }
}
