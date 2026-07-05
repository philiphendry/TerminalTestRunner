using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

public class ReducerTests
{
    private static AppState Fresh() => AppState.Initial("test");

    [Fact]
    public void Discovery_inserts_the_full_hierarchy_and_counts_leaves()
    {
        var s = Discover(Fresh(),
            Id("N.A", "ClassX", "Test1"),
            Id("N.A", "ClassX", "Test2"),
            Id("N.B", "ClassY", "Test3"));

        Assert.Equal(3, s.Root.TotalLeaves);
        Assert.Equal(3, s.Root.NotRun);
        // Root → Project → Tfm → (2 namespaces)
        var project = Assert.Single(s.Root.Children);
        var tfm = Assert.Single(project.Children);
        Assert.Equal(2, tfm.Children.Count);
    }

    [Fact]
    public void Status_transitions_flow_up_into_rollups()
    {
        var t1 = Id("N", "C", "T1");
        var t2 = Id("N", "C", "T2");
        var s = Discover(Fresh(), t1, t2);

        s = Feed(s, new AppEvent.TestStarted(t1));
        Assert.Equal(1, s.Root.Running);
        Assert.True(s.Running);

        s = Feed(s, new AppEvent.TestFinished(t1, TestOutcome.Passed, TimeSpan.FromMilliseconds(10)));
        Assert.Equal(0, s.Root.Running);
        Assert.Equal(1, s.Root.Passed);

        s = Feed(s, new AppEvent.TestFinished(t2, TestOutcome.Failed, TimeSpan.FromMilliseconds(5)));
        Assert.Equal(1, s.Root.Failed);
        Assert.Equal(TimeSpan.FromMilliseconds(15), s.Root.RollupDuration);
    }

    [Fact]
    public void RunCompleted_sweeps_still_running_leaves_to_notrun()
    {
        var t = Id("N", "C", "T");
        var s = Discover(Fresh(), t);
        s = Feed(s, new AppEvent.TestStarted(t));
        Assert.Equal(1, s.Root.Running);

        s = Feed(s, new AppEvent.RunCompleted());
        Assert.Equal(0, s.Root.Running);      // no phantom spinner
        Assert.Equal(1, s.Root.NotRun);
        Assert.False(s.Running);
    }

    [Fact]
    public void Theory_case_children_can_appear_only_via_run_events()
    {
        // Method never discovered; its rows arrive as run events (the 1→N shape).
        var row1 = Id("N", "C", "Theory", "value: 1");
        var row2 = Id("N", "C", "Theory", "value: 2");
        var s = Fresh();

        s = Feed(s,
            new AppEvent.TestStarted(row1),
            new AppEvent.TestFinished(row1, TestOutcome.Passed, TimeSpan.FromMilliseconds(3)),
            new AppEvent.TestStarted(row2),
            new AppEvent.TestFinished(row2, TestOutcome.Failed, TimeSpan.FromMilliseconds(4)));

        Assert.Equal(2, s.Root.TotalLeaves);
        // Root → Project → Tfm → Namespace → Class → Method
        var method = s.Root.Children[0].Children[0].Children[0].Children[0].Children[0];
        Assert.Equal(TestNodeKind.Method, method.Kind);
        Assert.Equal(2, method.Children.Count);              // two Case leaves
        Assert.All(method.Children, c => Assert.Equal(TestNodeKind.Case, c.Kind));
        Assert.Equal(1, method.Passed);
        Assert.Equal(1, method.Failed);
    }

    [Fact]
    public void Selection_survives_tree_changes()
    {
        var keep = Id("N", "C", "Keeper");
        var s = Discover(Fresh(), keep);
        s = s with { Selection = keep.Id };

        // More tests stream in; the selected id must still resolve to a live node.
        s = Discover(s, Id("N", "C", "Other1"), Id("N", "C", "Other2"));

        Assert.Equal(keep.Id, s.Selection);
        Assert.Contains(TreeFlattener.Flatten(s), r => r.Node.Id.Equals(keep.Id));
    }

    [Fact]
    public void Toggle_e_expands_and_collapses_single_node()
    {
        var s = Discover(Fresh(), Id("N", "C", "T1"));
        var classId = BranchId(TestNodeKind.Class, Project, Tfm, "N", "C");
        Assert.Contains(classId, s.Expanded);        // auto-expanded on discovery

        s = s with { Selection = classId };
        s = Feed(s, new AppEvent.KeyPressed(Char('e')));
        Assert.DoesNotContain(classId, s.Expanded);  // collapsed

        s = Feed(s, new AppEvent.KeyPressed(Char('e')));
        Assert.Contains(classId, s.Expanded);         // expanded again
    }

    [Fact]
    public void Shift_E_toggles_node_and_all_descendants()
    {
        var s = Discover(Fresh(),
            Id("N", "C1", "T1"), Id("N", "C2", "T2"));
        var projectId = BranchId(TestNodeKind.Project, Project);
        var c1 = BranchId(TestNodeKind.Class, Project, Tfm, "N", "C1");
        var c2 = BranchId(TestNodeKind.Class, Project, Tfm, "N", "C2");

        s = s with { Selection = projectId };
        s = Feed(s, new AppEvent.KeyPressed(Key('E', ConsoleKey.E, shift: true)));

        // Recursive collapse: the project and every descendant branch are collapsed.
        Assert.DoesNotContain(projectId, s.Expanded);
        Assert.DoesNotContain(c1, s.Expanded);
        Assert.DoesNotContain(c2, s.Expanded);

        // Recursive expand puts them all back.
        s = Feed(s, new AppEvent.KeyPressed(Key('E', ConsoleKey.E, shift: true)));
        Assert.Contains(projectId, s.Expanded);
        Assert.Contains(c1, s.Expanded);
        Assert.Contains(c2, s.Expanded);
    }

    [Fact]
    public void Arrow_navigation_moves_selection_and_clamps()
    {
        var s = Discover(Fresh(),
            Id("N", "C", "T1"), Id("N", "C", "T2"), Id("N", "C", "T3"));
        s = Feed(s, new AppEvent.Resized(80, 24));

        var rows = TreeFlattener.Flatten(s);
        var first = rows[0].Node.Id;
        s = s with { Selection = first, ScrollOffset = 0 };

        s = Feed(s, new AppEvent.KeyPressed(Key('\0', ConsoleKey.DownArrow)));
        Assert.Equal(rows[1].Node.Id, s.Selection);

        // Up past the top clamps at the first row.
        s = Feed(s,
            new AppEvent.KeyPressed(Key('\0', ConsoleKey.UpArrow)),
            new AppEvent.KeyPressed(Key('\0', ConsoleKey.UpArrow)));
        Assert.Equal(first, s.Selection);

        // End jumps to the last row.
        s = Feed(s, new AppEvent.KeyPressed(Key('\0', ConsoleKey.End)));
        Assert.Equal(rows[^1].Node.Id, s.Selection);
    }

    [Fact]
    public void Scroll_keeps_selection_in_a_small_viewport()
    {
        var ids = Enumerable.Range(0, 50).Select(i => Id("N", "C", $"T{i:D2}")).ToArray();
        var s = Discover(Fresh(), ids);
        s = Feed(s, new AppEvent.Resized(80, 12)); // viewport = 10 rows

        s = Feed(s, new AppEvent.KeyPressed(Key('\0', ConsoleKey.End)));
        var rows = TreeFlattener.Flatten(s);
        var selIndex = rows.FindIndex(r => r.Node.Id.Equals(s.Selection!.Value));

        Assert.InRange(selIndex, s.ScrollOffset, s.ScrollOffset + s.Viewport - 1);
    }

    [Fact]
    public void Q_quits_zero_and_CtrlC_quits_130()
    {
        var q = Feed(Fresh(), new AppEvent.KeyPressed(Char('q')));
        Assert.True(q.ShouldQuit);
        Assert.Equal(0, q.ExitCode);

        var ctrlC = Feed(Fresh(), new AppEvent.KeyPressed(Key('\x03', ConsoleKey.C, control: true)));
        Assert.True(ctrlC.ShouldQuit);
        Assert.Equal(130, ctrlC.ExitCode);
    }

    [Fact]
    public void Help_key_opens_overlay_and_any_key_closes()
    {
        var s = Feed(Fresh(), new AppEvent.KeyPressed(Char('?')));
        Assert.True(s.HelpVisible);

        s = Feed(s, new AppEvent.KeyPressed(Char('j')));   // any key closes
        Assert.False(s.HelpVisible);
    }

    [Fact]
    public void Revision_bumps_on_change_only()
    {
        var s0 = Discover(Fresh(), Id("N", "C", "T1"));
        var s1 = Reducer.Reduce(s0, new AppEvent.KeyPressed(Key('\0', ConsoleKey.F1))); // unhandled
        Assert.Equal(s0.Revision, s1.Revision);
        Assert.Same(s0, s1);
    }

    // --- Phase 4: 1→N theory promotion (AC4) and exit codes (M4) ----------------

    [Fact]
    public void Nonserialisable_theory_promotes_method_leaf_to_branch_on_run()
    {
        // A non-serialisable theory discovers as ONE plain case (display == FQN → the Method IS the leaf).
        var method = Id("N", "C", "Theory");
        var s = Discover(Fresh(), method);
        Assert.Equal(1, s.Root.TotalLeaves);
        Assert.True(NodeNamed(s, "Theory")!.IsLeaf);

        // The run reveals N rows with distinct displays under the same method (the live 1→N shape).
        var row1 = Id("N", "C", "Theory", "(x: 1)");
        var row2 = Id("N", "C", "Theory", "(x: 2)");
        s = Feed(s,
            new AppEvent.TestStarted(row1),
            new AppEvent.TestFinished(row1, TestOutcome.Passed, TimeSpan.FromMilliseconds(3)),
            new AppEvent.TestStarted(row2),
            new AppEvent.TestFinished(row2, TestOutcome.Failed, TimeSpan.FromMilliseconds(4)),
            new AppEvent.RunCompleted());

        var promoted = NodeNamed(s, "Theory")!;
        Assert.False(promoted.IsLeaf);
        Assert.Equal(2, promoted.Children.Count);
        // The Method leaf no longer counts as its own test — the rows count exactly once.
        Assert.Equal(2, s.Root.TotalLeaves);
        Assert.Equal(1, s.Root.Passed);
        Assert.Equal(1, s.Root.Failed);
        Assert.Equal(0, s.Root.NotRun);
        Assert.Equal(TimeSpan.FromMilliseconds(7), s.Root.RollupDuration);
    }

    [Fact]
    public void Stray_run_event_on_a_promoted_method_never_corrupts_rollups()
    {
        var method = Id("N", "C", "Theory");
        var s = Discover(Fresh(), method);
        s = Feed(s,
            new AppEvent.TestStarted(Id("N", "C", "Theory", "(x: 1)")),
            new AppEvent.TestFinished(Id("N", "C", "Theory", "(x: 1)"), TestOutcome.Passed, TimeSpan.Zero));

        // A late ActiveTests-style event addressing the BASE method (no row) must be a no-op on the branch.
        s = Feed(s, new AppEvent.TestStarted(method));
        Assert.Equal(1, s.Root.TotalLeaves);
        Assert.Equal(0, s.Root.Running);
        Assert.Equal(1, s.Root.Passed);
    }

    [Fact]
    public void Quit_exit_code_reflects_failures_and_ctrlc_wins()
    {
        var t = Id("N", "C", "T");
        var passed = Feed(Discover(Fresh(), t),
            new AppEvent.TestFinished(t, TestOutcome.Passed, TimeSpan.Zero));
        var q0 = Feed(passed, new AppEvent.KeyPressed(Char('q')));
        Assert.True(q0.ShouldQuit);
        Assert.Equal(0, q0.ExitCode);

        var failed = Feed(passed, new AppEvent.TestFinished(t, TestOutcome.Failed, TimeSpan.Zero));
        Assert.Equal(1, Feed(failed, new AppEvent.KeyPressed(Char('q'))).ExitCode);
        // Ctrl+C is 130 even when tests failed.
        Assert.Equal(130, Feed(failed, new AppEvent.KeyPressed(Key('', ConsoleKey.C, control: true))).ExitCode);
    }

    private static TestNode? NodeNamed(AppState s, string name)
    {
        static TestNode? Walk(TestNode n, string name)
        {
            if (n.Name == name) return n;
            foreach (var c in n.Children)
                if (Walk(c, name) is { } found) return found;
            return null;
        }
        return Walk(s.Root, name);
    }
}
