using System.Collections.Immutable;
using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// Pure-reducer tests for the Phase 6 session events (brief M4/M5): restored results attach/drop by derived id,
/// staleness marking + its clearing on a real run, the restored-UI application (filters/expansion/selection/
/// scroll), the rebuild-goes-stale path, and lazy detail attach. No terminals, no disk.
/// </summary>
public class SessionReducerTests
{
    private static AppState Discovered(params TestIdentity[] ids)
        => Discover(AppState.Initial("t"), ids);

    private static TestNode? Find(TestNode node, TestCaseId id)
    {
        if (node.Id.Equals(id)) return node;
        foreach (var c in node.Children)
            if (Find(c, id) is { } hit) return hit;
        return null;
    }

    private static TestNode Leaf(AppState s, TestIdentity ident) => Find(s.Root, ident.Id)!;

    private static RestoredUi Ui(string? selected = null, IEnumerable<string>? expanded = null,
        bool failedOnly = false, bool times = false, bool details = false, bool bottom = false, bool wrap = false,
        int treeScroll = 0, int detailScroll = 0)
        => new(failedOnly, times, details, bottom, wrap, (expanded ?? []).ToList(), selected, treeScroll, detailScroll);

    // --- Attach / drop ----------------------------------------------------------

    [Fact]
    public void Restore_attaches_results_by_id_and_counts_dropped()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var gone = Id("N", "C", "Gone");
        var s = Discovered(a, b);

        var results = new[]
        {
            new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(5), false, false),
            new RestoredResult(b.Id, TestStatus.Failed, TimeSpan.FromMilliseconds(9), true, false),
            new RestoredResult(gone.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(1), false, false),
        };
        s = Feed(s, new AppEvent.SessionRestored(results, Ui(), "5 min ago"));

        Assert.Equal(TestStatus.Passed, Leaf(s, a).Status);
        Assert.Equal(TestStatus.Failed, Leaf(s, b).Status);
        Assert.Equal(TimeSpan.FromMilliseconds(9), Leaf(s, b).Duration);
        Assert.Equal(1, s.Passed);
        Assert.Equal(1, s.Failed);
        Assert.Contains("restored 2 results", s.RestoreNotice);
        Assert.Contains("1 gone", s.RestoreNotice);
    }

    [Fact]
    public void Restore_marks_stale_without_losing_the_outcome()
    {
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Failed, TimeSpan.FromMilliseconds(9), false, Stale: true)], Ui(), "1 h ago"));

        var leaf = Leaf(s, a);
        Assert.Equal(TestStatus.Failed, leaf.Status);   // outcome preserved
        Assert.True(leaf.IsStale);                        // rendered dim
        Assert.Equal(1, s.Root.StaleLeaves);              // rolled up
    }

    [Fact]
    public void A_real_run_clears_staleness()
    {
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(5), false, Stale: true)], Ui(), "1 h ago"));
        Assert.True(Leaf(s, a).IsStale);

        s = Feed(s, new AppEvent.TestStarted(a));
        Assert.False(Leaf(s, a).IsStale);
        s = Feed(s, new AppEvent.TestFinished(a, TestOutcome.Passed, TimeSpan.FromMilliseconds(4)));
        Assert.False(Leaf(s, a).IsStale);
        Assert.Equal(0, s.Root.StaleLeaves);
    }

    [Fact]
    public void A_rerun_that_passes_again_still_clears_staleness()
    {
        // prev==next status (Passed→Passed) must NOT short-circuit the stale clear.
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(5), false, Stale: true)], Ui(), "1 h ago"));
        s = Feed(s, new AppEvent.TestFinished(a, TestOutcome.Passed, TimeSpan.FromMilliseconds(4)));
        Assert.False(Leaf(s, a).IsStale);
    }

    // --- Rebuild → stale (watch path, composes with restore) --------------------

    [Fact]
    public void Rediscovery_marks_kept_results_stale_but_not_new_ones()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discovered(a);
        s = Feed(s, new AppEvent.TestStarted(a), new AppEvent.TestFinished(a, TestOutcome.Passed, TimeSpan.FromMilliseconds(3)));

        var proj = ProjectId(Project);
        s = Feed(s,
            new AppEvent.RediscoveryStarted([Project]),
            new AppEvent.TestsDiscovered([a, b]),   // a kept, b new
            new AppEvent.RediscoveryCompleted([Project]));

        Assert.True(Leaf(s, a).IsStale);    // kept result now stale (old binary)
        Assert.False(Leaf(s, b).IsStale);   // freshly-added test is NotRun, not stale
        Assert.Equal(TestStatus.NotRun, Leaf(s, b).Status);
    }

    // --- UI application ---------------------------------------------------------

    [Fact]
    public void Restore_applies_filters_pane_and_selection()
    {
        var a = Id("N", "C", "A");
        var b = Id("N", "C", "B");
        var s = Discovered(a, b);
        // Realistic restore: the saved expansion keeps the selected leaf's ancestors open (it was visible).
        var expanded = s.Expanded.Select(id => id.Value).ToArray();
        var ui = Ui(selected: b.Id.Value, expanded: expanded, times: true, details: true, bottom: true, wrap: true, failedOnly: false);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.Zero, false, false),
             new RestoredResult(b.Id, TestStatus.Failed, TimeSpan.Zero, false, false)], ui, "now"));

        Assert.True(s.ShowDurations);
        Assert.True(s.DetailVisible);
        Assert.True(s.WordWrap);
        Assert.Equal(DetailOrientation.Beneath, s.DetailOrientation);
        Assert.Equal(b.Id, s.Selection);
    }

    [Fact]
    public void Restore_selection_falls_back_to_nearest_survivor_when_gone()
    {
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        var expanded = s.Expanded.Select(id => id.Value).ToArray();
        var ui = Ui(selected: Id("N", "C", "Missing").Id.Value, expanded: expanded);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.Zero, false, false)], ui, "now"));

        Assert.NotNull(s.Selection);                     // never left dangling
        Assert.NotEqual(Id("N", "C", "Missing").Id, s.Selection);   // fell back to a real surviving row
    }

    [Fact]
    public void Restore_applies_expansion_set()
    {
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        // Collapse everything except the root by supplying only the root in the expansion set.
        var rootExpanded = new[] { TestCaseId.ForBranch(TestNodeKind.Solution, "root").Value };
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Passed, TimeSpan.Zero, false, false)], Ui(expanded: rootExpanded), "now"));

        // Only the root + its direct child (project) are visible; the deeper namespace/class is collapsed away.
        Assert.True(s.Rows.Count <= 2);
    }

    // --- Lazy detail (brief M5) -------------------------------------------------

    [Fact]
    public void DetailLoaded_attaches_detail_and_clears_the_flag()
    {
        var a = Id("N", "C", "A");
        var s = Discovered(a);
        s = Feed(s, new AppEvent.SessionRestored(
            [new RestoredResult(a.Id, TestStatus.Failed, TimeSpan.Zero, HasDetail: true, false)], Ui(), "now"));
        Assert.True(Leaf(s, a).HasRestoredDetail);
        Assert.Null(Leaf(s, a).Detail);

        var detail = new TestResultDetail(Message: "boom", StackTrace: "at X");
        s = Feed(s, new AppEvent.DetailLoaded(a.Id, detail));
        Assert.False(Leaf(s, a).HasRestoredDetail);
        Assert.Equal("boom", Leaf(s, a).Detail!.Message);
    }
}
