using System.Collections.Immutable;

namespace Ttr.Core;

/// <summary>
/// The single pure reducer (CLAUDE.md invariant 1). <see cref="Reduce"/> maps
/// (state, event) → new state. Tree nodes are updated in place (they are only ever touched
/// here, on the single consumer), while all view state — selection, scroll, expansion,
/// lifecycle — is returned as a fresh immutable <see cref="AppState"/> snapshot. No terminals,
/// no processes, no locks: fully unit-testable.
/// </summary>
public static class Reducer
{
    /// <summary>Structural branches expanded automatically as they are discovered.</summary>
    private static readonly HashSet<TestNodeKind> AutoExpand =
    [
        TestNodeKind.Solution, TestNodeKind.Project, TestNodeKind.Tfm,
        TestNodeKind.Namespace, TestNodeKind.Class,
    ];

    public static AppState Reduce(AppState s, AppEvent e)
    {
        var next = e switch
        {
            AppEvent.TestsDiscovered d => Discovered(s, d),
            AppEvent.TestStarted t => Started(s, t),
            AppEvent.TestFinished t => Finished(s, t),
            AppEvent.RunCompleted => RunCompleted(s),
            AppEvent.Resized r => Resized(s, r),
            AppEvent.KeyPressed k => Key(s, k.Key),
            AppEvent.FatalError f => Fatal(s, f),
            _ => s,
        };
        return ReferenceEquals(next, s) ? s : next with { Revision = s.Revision + 1 };
    }

    // --- Discovery / run events -------------------------------------------------

    private static AppState Discovered(AppState s, AppEvent.TestsDiscovered d)
    {
        var expanded = s.Expanded.ToBuilder();
        var structural = false;
        foreach (var id in d.Tests)
            EnsureLeaf(s.Root, id, expanded, ref structural);

        var selection = s.Selection ?? s.Root.Id;
        var next = s with
        {
            Expanded = expanded.ToImmutable(),
            Selection = selection,
            TreeVersion = structural ? s.TreeVersion + 1 : s.TreeVersion,
        };
        return structural ? RebuildRows(next) : next;
    }

    private static AppState Started(AppState s, AppEvent.TestStarted t)
    {
        var structural = false;
        var expanded = s.Expanded.ToBuilder();
        var leaf = EnsureLeaf(s.Root, t.Test, expanded, ref structural);
        ApplyStatus(leaf, TestStatus.Running);
        var next = s with
        {
            Running = true,
            Expanded = expanded.ToImmutable(),
            TreeVersion = structural ? s.TreeVersion + 1 : s.TreeVersion,
        };
        return structural ? RebuildRows(next) : next;
    }

    private static AppState Finished(AppState s, AppEvent.TestFinished t)
    {
        var structural = false;
        var expanded = s.Expanded.ToBuilder();
        var leaf = EnsureLeaf(s.Root, t.Test, expanded, ref structural);
        ApplyStatus(leaf, t.Outcome switch
        {
            TestOutcome.Passed => TestStatus.Passed,
            TestOutcome.Failed => TestStatus.Failed,
            _ => TestStatus.Skipped,
        });
        SetDuration(leaf, t.Duration);
        var next = s with
        {
            Expanded = expanded.ToImmutable(),
            TreeVersion = structural ? s.TreeVersion + 1 : s.TreeVersion,
        };
        return structural ? RebuildRows(next) : next;
    }

    /// <summary>Recompute the published row snapshot from the current tree + expansion (reducer thread only).</summary>
    private static AppState RebuildRows(AppState s) => s with { Rows = TreeFlattener.Flatten(s.Root, s.Expanded) };

    private static AppState RunCompleted(AppState s)
    {
        // Sweep any still-Running leaf to NotRun so an aborted/interrupted run leaves no phantom
        // spinners (CLAUDE.md VSTest rule — enforced uniformly here for every adapter).
        foreach (var leaf in Leaves(s.Root))
            if (leaf.Status == TestStatus.Running)
                ApplyStatus(leaf, TestStatus.NotRun);
        return s with { Running = false };
    }

    // --- Resize -----------------------------------------------------------------

    private static AppState Resized(AppState s, AppEvent.Resized r)
    {
        // Chrome = 1 header line + 1 footer line. Never negative.
        var viewport = Math.Max(0, r.Height - 2);
        var idx = CurrentIndex(s);
        var scroll = ClampScroll(s.ScrollOffset, idx, viewport, s.Rows.Count);
        return s with { Viewport = viewport, ScrollOffset = scroll };
    }

    // --- Keyboard ---------------------------------------------------------------

    private static AppState Key(AppState s, ConsoleKeyInfo k)
    {
        // Ctrl+C → 130 (checked before char handling; KeyChar for Ctrl+C is unreliable).
        if (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0)
            return s with { ShouldQuit = true, ExitCode = 130 };

        switch (k.Key)
        {
            case ConsoleKey.UpArrow: return Move(s, -1);
            case ConsoleKey.DownArrow: return Move(s, +1);
            case ConsoleKey.PageUp: return Move(s, -Math.Max(1, s.Viewport));
            case ConsoleKey.PageDown: return Move(s, +Math.Max(1, s.Viewport));
            case ConsoleKey.Home: return MoveTo(s, 0);
            case ConsoleKey.End: return MoveTo(s, int.MaxValue);
            case ConsoleKey.RightArrow: return ExpandOrDescend(s);
            case ConsoleKey.LeftArrow: return CollapseOrAscend(s);
        }

        switch (k.KeyChar)
        {
            case 'q': return s with { ShouldQuit = true, ExitCode = 0 };
            case 'k': return Move(s, -1);
            case 'j': return Move(s, +1);
            case 'e': return ToggleExpand(s, recursive: false);
            case 'E': return ToggleExpand(s, recursive: true);   // Shift+E → uppercase KeyChar
            case '?':
                return s with
                {
                    FooterMessage = "Help arrives in Phase 2 — ↑/↓ j/k move · →/← e/E expand · q quit",
                };
        }

        return s;
    }

    private static AppState Move(AppState s, int delta)
    {
        if (s.Rows.Count == 0) return s;
        return MoveToIndex(s, CurrentIndex(s) + delta);
    }

    private static AppState MoveTo(AppState s, int index)
    {
        if (s.Rows.Count == 0) return s;
        return MoveToIndex(s, index);
    }

    private static AppState MoveToIndex(AppState s, int index)
    {
        var rows = s.Rows;
        var idx = Math.Clamp(index, 0, rows.Count - 1);
        var sel = rows[idx].Node.Id;
        var scroll = ClampScroll(s.ScrollOffset, idx, s.Viewport, rows.Count);
        if (sel.Equals(s.Selection) && scroll == s.ScrollOffset) return s;
        return s with { Selection = sel, ScrollOffset = scroll };
    }

    private static AppState ExpandOrDescend(AppState s)
    {
        var (node, _) = Selected(s);
        if (node is null) return s;
        if (!node.IsLeaf && !s.Expanded.Contains(node.Id))
            return SetExpansion(s, s.Expanded.Add(node.Id));
        if (!node.IsLeaf && node.Children.Count > 0)
            return SelectId(s, node.Children[0].Id);   // already expanded → step into first child
        return s;
    }

    private static AppState CollapseOrAscend(AppState s)
    {
        var (node, _) = Selected(s);
        if (node is null) return s;
        if (!node.IsLeaf && s.Expanded.Contains(node.Id))
            return SetExpansion(s, s.Expanded.Remove(node.Id));
        if (node.Parent is not null)
            return SelectId(s, node.Parent.Id);         // leaf / collapsed → jump to parent
        return s;
    }

    private static AppState ToggleExpand(AppState s, bool recursive)
    {
        var (node, _) = Selected(s);
        if (node is null || node.IsLeaf) return s;

        var currentlyExpanded = s.Expanded.Contains(node.Id);
        ImmutableHashSet<TestCaseId> set;
        if (!recursive)
        {
            set = currentlyExpanded ? s.Expanded.Remove(node.Id) : s.Expanded.Add(node.Id);
        }
        else
        {
            var target = !currentlyExpanded;
            var builder = s.Expanded.ToBuilder();
            foreach (var branch in BranchesInclusive(node))
            {
                if (target) builder.Add(branch.Id);
                else builder.Remove(branch.Id);
            }
            set = builder.ToImmutable();
        }
        return SetExpansion(s, set);
    }

    /// <summary>Apply a new expansion set, rebuild rows, bump the version, and keep the selection in view.</summary>
    private static AppState SetExpansion(AppState s, ImmutableHashSet<TestCaseId> expanded)
    {
        var afterState = RebuildRows(s with { Expanded = expanded, TreeVersion = s.TreeVersion + 1 });
        var idx = CurrentIndex(afterState);
        var scroll = ClampScroll(afterState.ScrollOffset, idx, afterState.Viewport, afterState.Rows.Count);
        return afterState with { ScrollOffset = scroll };
    }

    private static AppState SelectId(AppState s, TestCaseId id)
    {
        var idx = IndexOf(s.Rows, id);
        if (idx < 0) return s;
        return MoveToIndex(s, idx);
    }

    private static AppState Fatal(AppState s, AppEvent.FatalError f)
        => s with { FatalMessage = f.Message, ShouldQuit = true, ExitCode = 1 };

    // --- Tree construction & rollups -------------------------------------------

    private static TestNode EnsureLeaf(
        TestNode root, TestIdentity id, ImmutableHashSet<TestCaseId>.Builder expanded, ref bool structural)
    {
        var project = EnsureBranch(root, id.Project, TestNodeKind.Project,
            TestCaseId.ForBranch(TestNodeKind.Project, id.Project), id.Project, expanded, ref structural);

        var tfm = EnsureBranch(project, id.Tfm, TestNodeKind.Tfm,
            TestCaseId.ForBranch(TestNodeKind.Tfm, id.Project, id.Tfm), id.Tfm, expanded, ref structural);

        var ns = EnsureBranch(tfm, id.Namespace, TestNodeKind.Namespace,
            TestCaseId.ForBranch(TestNodeKind.Namespace, id.Project, id.Tfm, id.Namespace),
            id.Namespace, expanded, ref structural);

        var cls = EnsureBranch(ns, id.ClassName, TestNodeKind.Class,
            TestCaseId.ForBranch(TestNodeKind.Class, id.Project, id.Tfm, id.Namespace, id.ClassName),
            id.ClassName, expanded, ref structural);

        if (!id.IsCase)
        {
            // Plain test: the Method node IS the leaf, keyed on the derived id.
            return EnsureLeafNode(cls, id.Method, TestNodeKind.Method, id.Id, id.Method, ref structural);
        }

        // Theory: Method is a branch; each row is a Case leaf (the 1→N shape, incl. mid-run inserts).
        var method = EnsureBranch(cls, id.Method, TestNodeKind.Method,
            TestCaseId.ForBranch(TestNodeKind.Method, id.Project, id.Tfm, id.Namespace, id.ClassName, id.Method),
            id.Method, expanded, ref structural);
        return EnsureLeafNode(method, id.CaseDisplay!, TestNodeKind.Case, id.Id, id.CaseDisplay!, ref structural);
    }

    private static TestNode EnsureBranch(
        TestNode parent, string key, TestNodeKind kind, TestCaseId id, string name,
        ImmutableHashSet<TestCaseId>.Builder expanded, ref bool structural)
    {
        var existing = parent.FindChild(key);
        if (existing is not null) return existing;

        var node = new TestNode { Id = id, Kind = kind, Name = name, Parent = parent };
        parent.AddChild(key, node);
        structural = true;
        if (AutoExpand.Contains(kind)) expanded.Add(id);
        return node;
    }

    private static TestNode EnsureLeafNode(
        TestNode parent, string key, TestNodeKind kind, TestCaseId id, string name, ref bool structural)
    {
        var existing = parent.FindChild(key);
        if (existing is not null) return existing;

        var node = new TestNode { Id = id, Kind = kind, Name = name, Parent = parent };
        parent.AddChild(key, node);
        structural = true;
        AddLeafToRollups(node);
        return node;
    }

    /// <summary>Register a freshly created leaf (initial status NotRun) into every ancestor's counters.</summary>
    private static void AddLeafToRollups(TestNode leaf)
    {
        for (TestNode? n = leaf; n is not null; n = n.Parent)
        {
            n.Counts[(int)TestStatus.NotRun]++;
            n.TotalLeaves++;
        }
    }

    /// <summary>Transition a leaf, propagating the count delta up the ancestor chain.</summary>
    private static void ApplyStatus(TestNode leaf, TestStatus next)
    {
        var prev = leaf.Status;
        if (prev == next) return;
        for (TestNode? n = leaf; n is not null; n = n.Parent)
        {
            n.Counts[(int)prev]--;
            n.Counts[(int)next]++;
        }
        leaf.Status = next;
    }

    private static void SetDuration(TestNode leaf, TimeSpan duration)
    {
        var delta = duration - leaf.Duration;
        for (TestNode? n = leaf; n is not null; n = n.Parent)
            n.RollupDuration += delta;
        leaf.Duration = duration;
    }

    // --- Selection helpers ------------------------------------------------------

    private static (TestNode? Node, int Index) Selected(AppState s)
    {
        var rows = s.Rows;
        var idx = CurrentIndex(s);
        return idx >= 0 && idx < rows.Count ? (rows[idx].Node, idx) : (null, -1);
    }

    private static int CurrentIndex(AppState s)
    {
        if (s.Selection is { } sel)
        {
            var i = IndexOf(s.Rows, sel);
            if (i >= 0) return i;
        }
        return 0;
    }

    private static int IndexOf(IReadOnlyList<FlatRow> rows, TestCaseId id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Node.Id.Equals(id)) return i;
        return -1;
    }

    private static int ClampScroll(int scroll, int selectedIndex, int viewport, int count)
    {
        if (viewport <= 0) return 0;
        if (selectedIndex < scroll) scroll = selectedIndex;
        else if (selectedIndex >= scroll + viewport) scroll = selectedIndex - viewport + 1;
        var max = Math.Max(0, count - viewport);
        return Math.Clamp(scroll, 0, max);
    }

    // --- Traversal --------------------------------------------------------------

    private static IEnumerable<TestNode> Leaves(TestNode node)
    {
        if (node.IsLeaf)
        {
            yield return node;
            yield break;
        }
        foreach (var child in node.Children)
            foreach (var leaf in Leaves(child))
                yield return leaf;
    }

    private static IEnumerable<TestNode> BranchesInclusive(TestNode node)
    {
        if (node.IsLeaf) yield break;
        yield return node;
        foreach (var child in node.Children)
            foreach (var b in BranchesInclusive(child))
                yield return b;
    }
}
