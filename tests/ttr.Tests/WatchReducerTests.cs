using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// Pure reducer coverage for the Phase 5 watch events (brief M3/M4, plan §8/§9): the re-discovery diff
/// (add/remove/keep by <see cref="TestCaseId"/>, selection survival, theory-placeholder behaviour), the
/// auto-rerun request (launch, coalesce-behind-run, failed-only narrowing), and the watch-state header
/// signal. No terminals, no processes, no watchers.
/// </summary>
public class WatchReducerTests
{
    private const string Proj = TestKit.Project;   // "P"

    private static AppState Setup(params string[] methods)
    {
        var s = AppState.Initial("watch", runsEnabled: true) with { Watch = WatchKind.Build };
        s = Feed(s, new AppEvent.ProjectRegistered(Proj, "P", ["net10.0"], RunnerKind.VsTest));
        return Discover(s, methods.Select(m => Id("N", "C", m)).ToArray());
    }

    private static TestNode? Find(TestNode node, string name)
    {
        if (node.Name == name) return node;
        foreach (var c in node.Children)
            if (Find(c, name) is { } hit) return hit;
        return null;
    }

    private static AppState Finish(AppState s, string method, TestOutcome outcome) =>
        Reducer.Reduce(s, new AppEvent.TestFinished(Id("N", "C", method), outcome, TimeSpan.FromMilliseconds(1),
            outcome == TestOutcome.Failed ? new TestResultDetail("boom", null, null) : null));

    // --- Re-discovery diff (add / remove / keep) --------------------------------

    [Fact]
    public void Rediscovery_adds_new_removes_gone_and_keeps_survivors_results()
    {
        var s = Setup("Add", "Subtract", "Multiply");
        s = Finish(s, "Add", TestOutcome.Passed);
        s = Finish(s, "Subtract", TestOutcome.Failed);
        Assert.Equal(3, s.TotalTests);

        // Diff: keep Add + Subtract, remove Multiply, add Divide.
        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C", "Add"), Id("N", "C", "Subtract"), Id("N", "C", "Divide"));
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        Assert.Equal(3, s.TotalTests);
        Assert.Null(Find(s.Root, "Multiply"));                       // removed
        Assert.NotNull(Find(s.Root, "Divide"));                      // added
        Assert.Equal(TestStatus.Passed, Find(s.Root, "Add")!.Status);        // kept result preserved
        Assert.Equal(TestStatus.Failed, Find(s.Root, "Subtract")!.Status);   // kept result preserved
        Assert.Equal(TestStatus.NotRun, Find(s.Root, "Divide")!.Status);     // new test is NotRun
    }

    [Fact]
    public void Rediscovery_with_no_changes_keeps_everything()
    {
        var s = Setup("Add", "Subtract");
        s = Finish(s, "Add", TestOutcome.Passed);

        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C", "Add"), Id("N", "C", "Subtract"));
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        Assert.Equal(2, s.TotalTests);
        Assert.Equal(TestStatus.Passed, Find(s.Root, "Add")!.Status);
    }

    [Fact]
    public void Rediscovery_prunes_an_emptied_class_branch()
    {
        // Two classes; the whole of class C2 vanishes on re-discovery.
        var s = AppState.Initial("watch", runsEnabled: true) with { Watch = WatchKind.Build };
        s = Feed(s, new AppEvent.ProjectRegistered(Proj, "P", ["net10.0"], RunnerKind.VsTest));
        s = Discover(s, Id("N", "C1", "A"), Id("N", "C2", "B"));
        Assert.NotNull(Find(s.Root, "C2"));

        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C1", "A"));            // C2.B gone
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        Assert.Null(Find(s.Root, "C2"));                // emptied class pruned
        Assert.NotNull(Find(s.Root, "C1"));
        Assert.Equal(1, s.TotalTests);
    }

    [Fact]
    public void Rediscovery_moves_selection_off_a_removed_node_to_a_survivor()
    {
        var s = Setup("Add", "Subtract", "Multiply");
        var removed = Id("N", "C", "Multiply").Id;
        s = SelectRow(s, removed);
        Assert.Equal(removed, s.Selection);

        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C", "Add"), Id("N", "C", "Subtract"));   // Multiply removed
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        Assert.NotEqual(removed, s.Selection);
        Assert.NotNull(s.Selection);
        Assert.Contains(s.Rows, r => r.Node.Id.Equals(s.Selection));       // selection is a live row
    }

    [Fact]
    public void Rediscovery_keeps_a_selection_that_survived()
    {
        var s = Setup("Add", "Subtract", "Multiply");
        var kept = Id("N", "C", "Add").Id;
        s = SelectRow(s, kept);

        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C", "Add"), Id("N", "C", "Subtract"));
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        Assert.Equal(kept, s.Selection);
    }

    [Fact]
    public void Rediscovery_keeps_a_run_materialised_theory_when_the_method_is_rediscovered_as_a_plain_leaf()
    {
        // Model the VSTest non-serialisable theory: discovered as one Method leaf, then a run promotes it to
        // a branch with a Case child. A re-discovery re-supplies only the Method (display == FQN) — its
        // already-materialised rows must survive (they'll re-run in the auto-rerun), not be swept.
        var s = Setup("Theory");
        var row = Id("N", "C", "Theory", "row1");
        s = Reducer.Reduce(s, new AppEvent.TestStarted(row));
        s = Reducer.Reduce(s, new AppEvent.TestFinished(row, TestOutcome.Passed, TimeSpan.FromMilliseconds(1)));
        var theory = Find(s.Root, "Theory")!;
        Assert.False(theory.IsLeaf);
        Assert.Single(theory.Children);

        s = Feed(s, new AppEvent.RediscoveryStarted([Proj]));
        s = Discover(s, Id("N", "C", "Theory"));   // plain Method leaf, as VSTest re-discovers it
        s = Feed(s, new AppEvent.RediscoveryCompleted([Proj]));

        theory = Find(s.Root, "Theory")!;
        Assert.False(theory.IsLeaf);               // still a branch
        Assert.Single(theory.Children);            // the row survived
    }

    // --- Auto-rerun request -----------------------------------------------------

    [Fact]
    public void Watch_rerun_request_launches_the_affected_subset()
    {
        var s = Setup("Add", "Subtract");
        var gen = s.RunGeneration;

        s = Feed(s, new AppEvent.WatchRerunRequested([Proj]));

        Assert.True(s.Running);
        Assert.Equal(gen + 1, s.RunGeneration);
        Assert.Equal(2, s.RunSubset.Count);
        Assert.Equal(2, s.Root.Queued);            // affected leaves marked Queued
    }

    [Fact]
    public void Watch_rerun_request_during_a_run_coalesces_into_one_followup()
    {
        var s = Setup("Add", "Subtract");
        s = Feed(s, new AppEvent.WatchRerunRequested([Proj]));   // launches
        var gen = s.RunGeneration;

        s = Feed(s, new AppEvent.WatchRerunRequested([Proj]));   // during the run → coalesce, no new launch

        Assert.Equal(gen, s.RunGeneration);
        Assert.False(s.QueuedRerun.IsEmpty);
    }

    [Fact]
    public void Watch_rerun_request_under_failed_filter_reruns_only_failures()
    {
        var s = Setup("Add", "Subtract");
        s = Finish(s, "Add", TestOutcome.Passed);
        s = Finish(s, "Subtract", TestOutcome.Failed);
        s = Reducer.Reduce(s, new AppEvent.RunCompleted());
        s = Press(s, Char('f'));                    // failed-only

        s = Feed(s, new AppEvent.WatchRerunRequested([Proj]));

        Assert.Single(s.RunSubset);
        Assert.Equal(Id("N", "C", "Subtract").Id, s.RunSubset[0]);
    }

    [Fact]
    public void Watch_rerun_request_is_ignored_when_runs_are_disabled()
    {
        var s = AppState.Initial("watch", runsEnabled: false) with { Watch = WatchKind.Build };
        s = Feed(s, new AppEvent.ProjectRegistered(Proj, "P", ["net10.0"], RunnerKind.VsTest));
        s = Discover(s, Id("N", "C", "Add"));
        var gen = s.RunGeneration;

        s = Feed(s, new AppEvent.WatchRerunRequested([Proj]));

        Assert.False(s.Running);
        Assert.Equal(gen, s.RunGeneration);
    }

    // --- Watch-state header signal ---------------------------------------------

    [Fact]
    public void Watch_state_changed_updates_activity_in_watch_mode()
    {
        var s = AppState.Initial("watch") with { Watch = WatchKind.Build };
        s = Reducer.Reduce(s, new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected));
        Assert.Equal(WatchActivity.ChangeDetected, s.WatchActivity);
    }

    [Fact]
    public void Watch_state_changed_is_a_noop_when_not_watching()
    {
        var s = AppState.Initial("plain");   // Watch.Off
        var next = Reducer.Reduce(s, new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected));
        Assert.Same(s, next);
        Assert.Equal(WatchActivity.Idle, next.WatchActivity);
    }
}
