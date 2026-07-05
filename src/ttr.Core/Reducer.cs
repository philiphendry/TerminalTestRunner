using System.Collections.Immutable;
using TtrParser;

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
            AppEvent.ProjectRegistered p => ProjectRegistered(s, p),
            AppEvent.NoticeRaised n => NoticeRaised(s, n),
            AppEvent.BuildStarted b => BuildPhaseChanged(s, b.ProjectPath, BuildPhase.Building),
            AppEvent.BuildSucceeded b => BuildPhaseChanged(s, b.ProjectPath, BuildPhase.Built),
            AppEvent.BuildFailed b => BuildFailed(s, b),
            AppEvent.DiscoveryFailed d => DiscoveryFailed(s, d),
            AppEvent.TestsDiscovered d => Discovered(s, d),
            AppEvent.TestStarted t => Started(s, t),
            AppEvent.TestFinished t => Finished(s, t),
            AppEvent.RunCompleted => RunCompleted(s),
            AppEvent.Resized r => Resized(s, r),
            AppEvent.KeyPressed k => Key(s, k.Key),
            AppEvent.HighlightReady h => HighlightReady(s, h),
            AppEvent.ToastExpired t => ToastExpired(s, t),
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
        AttachDetail(leaf, t.Detail);

        // Under the failed-only filter a pass changes which rows are visible (vanish-on-pass), so the
        // published snapshot must be rebuilt even when the tree structure did not change (brief M3).
        var prevIndex = CurrentIndex(s);
        var next = s with
        {
            Expanded = expanded.ToImmutable(),
            TreeVersion = structural ? s.TreeVersion + 1 : s.TreeVersion,
        };
        if (structural || s.FailedOnly)
            next = NormalizeView(RebuildRows(next), prevIndex);
        return next;
    }

    /// <summary>Recompute the published row snapshot from the current tree + expansion + filter (reducer thread only).</summary>
    private static AppState RebuildRows(AppState s) =>
        s with { Rows = TreeFlattener.Flatten(s.Root, s.Expanded, s.FailedOnly) };

    private static AppState RunCompleted(AppState s)
    {
        // Sweep any still-Running leaf to NotRun so an aborted/interrupted run leaves no phantom
        // spinners (CLAUDE.md VSTest rule — enforced uniformly here for every adapter).
        foreach (var leaf in Leaves(s.Root))
            if (leaf.Status == TestStatus.Running)
                ApplyStatus(leaf, TestStatus.NotRun);

        var next = s with { Running = false };

        // Coalesced follow-up (plan §9): reruns requested while this run was active launch now, once.
        if (s.QueuedRerunAll || !s.QueuedRerun.IsEmpty)
        {
            var subset = s.QueuedRerunAll ? [] : (IReadOnlyList<TestCaseId>)s.QueuedRerun.ToArray();
            next = LaunchRun(next, subset)
                with { QueuedRerun = ImmutableHashSet<TestCaseId>.Empty, QueuedRerunAll = false };
        }

        // Filter view may need a rebuild after the final sweep.
        return s.FailedOnly ? NormalizeView(RebuildRows(next), CurrentIndex(s)) : next;
    }

    // --- Phase 3: project registration, build phase & diagnostics ---------------

    /// <summary>Create/refresh a Project node from evaluation + detection, before build/discovery (brief M1).
    /// A single declared TFM collapses the TFM level; two or more insert TFM child nodes eagerly.</summary>
    private static AppState ProjectRegistered(AppState s, AppEvent.ProjectRegistered p)
    {
        var expanded = s.Expanded.ToBuilder();
        var structural = false;
        var project = EnsureBranch(s.Root, p.ProjectPath, TestNodeKind.Project,
            TestCaseId.ForBranch(TestNodeKind.Project, p.ProjectPath), p.DisplayName, expanded, ref structural);
        project.DeclaredTfms = p.Tfms;
        project.Runner = p.Runner;
        project.Notice = p.Notice;

        // Multi-targeting: materialise the TFM nodes now so the structure is visible before discovery.
        if (p.Tfms.Count > 1)
            foreach (var tfm in p.Tfms)
                EnsureBranch(project, tfm, TestNodeKind.Tfm,
                    TestCaseId.ForBranch(TestNodeKind.Tfm, p.ProjectPath, tfm), tfm, expanded, ref structural);

        var next = s with
        {
            Expanded = expanded.ToImmutable(),
            Selection = s.Selection ?? s.Root.Id,
            TreeVersion = structural ? s.TreeVersion + 1 : s.TreeVersion,
        };
        return structural ? RebuildRows(next) : next;
    }

    /// <summary>Insert a standalone diagnostic node under the solution root (phantom / unparseable entry).</summary>
    private static AppState NoticeRaised(AppState s, AppEvent.NoticeRaised n)
    {
        var existing = s.Root.FindChild(n.Key);
        if (existing is not null)
        {
            existing.Notice = n.Notice;
            return s with { };
        }
        var node = new TestNode
        {
            Id = TestCaseId.ForBranch(TestNodeKind.Notice, n.Key),
            Kind = TestNodeKind.Notice,
            Name = n.Name,
            Parent = s.Root,
            Notice = n.Notice,
        };
        s.Root.AddChild(n.Key, node);
        var next = s with { Selection = s.Selection ?? s.Root.Id, TreeVersion = s.TreeVersion + 1 };
        return RebuildRows(next);
    }

    private static AppState BuildPhaseChanged(AppState s, string projectPath, BuildPhase phase)
    {
        var project = s.Root.FindChild(projectPath);
        if (project is null || project.BuildPhase == phase) return s;
        project.BuildPhase = phase;
        return WithBusy(s);
    }

    private static AppState BuildFailed(AppState s, AppEvent.BuildFailed b)
    {
        var project = s.Root.FindChild(b.ProjectPath);
        if (project is null) return s;
        project.BuildPhase = BuildPhase.Failed;
        project.BuildDiagnostics = b.Diagnostics;
        project.BuildOutput = b.RawOutput;
        // Build diagnostics carry file paths (canonical `path(line,col): error CODE:`), so the 'o' modal
        // works on build errors too (plan §7): derive the node's ordered refs from the raw output.
        project.FileRefs = string.IsNullOrEmpty(b.RawOutput)
            ? []
            : FileReferenceParser.Parse(b.RawOutput, null, projectDir: ProjectDirOf(b.ProjectPath));
        return WithBusy(s);
    }

    /// <summary>Runner smoke-validation failure → a warning/error <see cref="TestNodeKind.Notice"/> child
    /// under the project (or its TFM node), never a silently empty subtree (plan §6.2).</summary>
    private static AppState DiscoveryFailed(AppState s, AppEvent.DiscoveryFailed d)
    {
        var project = s.Root.FindChild(d.ProjectPath);
        if (project is null) return s;
        var parent = d.Tfm is { } tfm && project.NeedsTfmLevel
            ? project.FindChild(tfm) ?? project
            : project;

        var key = "notice:" + (d.Tfm ?? "");
        var existing = parent.FindChild(key);
        if (existing is not null)
        {
            existing.Notice = d.Notice;
            return WithBusy(s);
        }
        var node = new TestNode
        {
            Id = TestCaseId.ForBranch(TestNodeKind.Notice, d.ProjectPath, d.Tfm ?? "", "smoke"),
            Kind = TestNodeKind.Notice,
            Name = d.Notice.Summary,
            Parent = parent,
            Notice = d.Notice,
        };
        parent.AddChild(key, node);
        var next = s with { TreeVersion = s.TreeVersion + 1 };
        return RebuildRows(WithBusy(next));
    }

    /// <summary>Recompute the busy flag (any project mid-build) so the render loop animates build spinners.</summary>
    private static AppState WithBusy(AppState s)
    {
        var busy = false;
        foreach (var child in s.Root.Children)
            if (child.BuildPhase == BuildPhase.Building) { busy = true; break; }
        return s with { Busy = busy };
    }

    private static string ProjectDirOf(string projectPath)
    {
        try { return Path.GetDirectoryName(projectPath) ?? ""; }
        catch (ArgumentException) { return ""; }
    }

    // --- Resize -----------------------------------------------------------------

    private static AppState Resized(AppState s, AppEvent.Resized r)
    {
        if (r.Width == s.Width && r.Height == s.Height) return s;
        return Relayout(s with { Width = r.Width, Height = r.Height });
    }

    /// <summary>Recompute the tree viewport from the current size + pane flags and re-clamp scroll.</summary>
    private static AppState Relayout(AppState s)
    {
        var viewport = Layout.TreeViewportRows(s, s.Width, s.Height);
        var idx = CurrentIndex(s);
        var scroll = ClampScroll(s.ScrollOffset, idx, viewport, s.Rows.Count);
        return s with { Viewport = viewport, ScrollOffset = scroll };
    }

    // --- Keyboard ---------------------------------------------------------------

    private static AppState Key(AppState s, ConsoleKeyInfo k)
    {
        // Ctrl+C → 130 everywhere (checked first; KeyChar for Ctrl+C is unreliable).
        if (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0)
            return s with { ShouldQuit = true, ExitCode = 130 };

        // The 'o' modal captures ALL input while open (plan §11.3).
        if (s.Modal is not null) return ModalKey(s, k);

        // The help overlay closes on any key.
        if (s.HelpVisible) return s with { HelpVisible = false };

        return MainKey(s, k);
    }

    private static AppState MainKey(AppState s, ConsoleKeyInfo k)
    {
        var detailFocused = s.DetailVisible && s.Focus == PaneFocus.Detail;

        switch (k.Key)
        {
            case ConsoleKey.UpArrow: return detailFocused ? DetailScroll(s, -1) : Move(s, -1);
            case ConsoleKey.DownArrow: return detailFocused ? DetailScroll(s, +1) : Move(s, +1);
            case ConsoleKey.PageUp:
                return detailFocused ? DetailScroll(s, -DetailPage(s)) : Move(s, -Math.Max(1, s.Viewport));
            case ConsoleKey.PageDown:
                return detailFocused ? DetailScroll(s, +DetailPage(s)) : Move(s, +Math.Max(1, s.Viewport));
            case ConsoleKey.Home: return detailFocused ? DetailScrollTo(s, 0) : MoveTo(s, 0);
            case ConsoleKey.End: return detailFocused ? DetailScrollTo(s, int.MaxValue) : MoveTo(s, int.MaxValue);
            case ConsoleKey.RightArrow: return detailFocused ? s : ExpandOrDescend(s);
            case ConsoleKey.LeftArrow: return detailFocused ? s : CollapseOrAscend(s);
            case ConsoleKey.Tab: return s.DetailVisible ? ToggleFocus(s) : s;
            case ConsoleKey.Escape: return s;
        }

        switch (k.KeyChar)
        {
            case 'q': return s with { ShouldQuit = true, ExitCode = 0 };
            case 'k': return detailFocused ? DetailScroll(s, -1) : Move(s, -1);
            case 'j': return detailFocused ? DetailScroll(s, +1) : Move(s, +1);
            case 'e': return ToggleExpand(s, recursive: false);
            case 'E': return ToggleExpand(s, recursive: true);   // Shift+E → uppercase KeyChar
            case 's': return ToggleDetail(s);
            case 'b': return ToggleOrientation(s);
            case 'w': return s.DetailVisible ? s with { WordWrap = !s.WordWrap } : s;
            case 'f': return ToggleFailedOnly(s);
            case 't': return s with { ShowDurations = !s.ShowDurations };
            case 'r': return Rerun(s, rooted: false);
            case 'R': return Rerun(s, rooted: true);
            case 'o': return OpenModal(s);
            case '?': return s with { HelpVisible = true };
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
        var prevIndex = CurrentIndex(s);
        var afterState = RebuildRows(s with { Expanded = expanded, TreeVersion = s.TreeVersion + 1 });
        return NormalizeView(afterState, prevIndex);
    }

    private static AppState SelectId(AppState s, TestCaseId id)
    {
        var idx = IndexOf(s.Rows, id);
        if (idx < 0) return s;
        return MoveToIndex(s, idx);
    }

    private static AppState Fatal(AppState s, AppEvent.FatalError f)
        => s with { FatalMessage = f.Message, ShouldQuit = true, ExitCode = 1 };

    // --- View toggles (M2/M3) ---------------------------------------------------

    private static AppState ToggleDetail(AppState s)
    {
        var open = !s.DetailVisible;
        return Relayout(s with { DetailVisible = open, Focus = open ? s.Focus : PaneFocus.Tree, DetailScroll = 0 });
    }

    private static AppState ToggleOrientation(AppState s) =>
        Relayout(s with
        {
            DetailOrientation = s.DetailOrientation == DetailOrientation.Right
                ? DetailOrientation.Beneath
                : DetailOrientation.Right,
        });

    private static AppState ToggleFocus(AppState s) =>
        s with { Focus = s.Focus == PaneFocus.Tree ? PaneFocus.Detail : PaneFocus.Tree };

    private static AppState ToggleFailedOnly(AppState s)
    {
        var prevIndex = CurrentIndex(s);
        var ns = s with { FailedOnly = !s.FailedOnly, DetailScroll = 0 };
        return NormalizeView(RebuildRows(ns), prevIndex);
    }

    // --- Detail-pane scroll (M2) ------------------------------------------------

    private static int DetailPage(AppState s) => Math.Max(1, Layout.DetailViewportRows(s, s.Width, s.Height));

    private static int DetailContentCount(AppState s)
    {
        var (node, _) = Selected(s);
        return node is null ? 0 : DetailComposer.Compose(node).Count;
    }

    private static AppState DetailScroll(AppState s, int delta) => DetailScrollTo(s, s.DetailScroll + delta);

    private static AppState DetailScrollTo(AppState s, int index)
    {
        // Clamp against the logical line count (exact when wrap is off; conservative when on). The
        // renderer clamps the display slice cell-accurately too.
        var max = Math.Max(0, DetailContentCount(s) - DetailPage(s));
        var scroll = Math.Clamp(index, 0, max);
        return scroll == s.DetailScroll ? s : s with { DetailScroll = scroll };
    }

    // --- Rerun (M4) -------------------------------------------------------------

    private static AppState Rerun(AppState s, bool rooted)
    {
        // Phase 3 is read-only for real targets (and the 'backend' fake, which simulates one): runs
        // arrive in Phase 4 (brief AC7). Fake scenarios keep RunsEnabled and run normally.
        if (!s.RunsEnabled) return Toast(s, "runs arrive in Phase 4");

        var root = rooted ? s.Root : Selected(s).Node ?? s.Root;
        // Under 'f' the rerun set is the filter's visible set: currently-failed leaves plus any
        // already in-flight (Queued/Running) so a second r/R mid-run coalesces the same set.
        var targets = Leaves(root)
            .Where(l => !s.FailedOnly || l.Status is TestStatus.Failed or TestStatus.Queued or TestStatus.Running)
            .ToList();
        if (targets.Count == 0) return Toast(s, "nothing to rerun");

        // A rooted rerun with no filter reruns everything → empty subset (adapter runs all).
        var runAll = rooted && !s.FailedOnly;
        IReadOnlyList<TestCaseId> subset = runAll ? [] : targets.Select(l => l.Id).ToList();

        if (s.Running)
        {
            // Coalesce into one consolidated follow-up run (plan §9).
            var queued = runAll
                ? s with { QueuedRerunAll = true }
                : s with { QueuedRerun = s.QueuedRerun.Union(subset) };
            return Toast(queued, "rerun queued");
        }

        foreach (var leaf in targets) ApplyStatus(leaf, TestStatus.Queued);
        return LaunchRun(s, subset);
    }

    /// <summary>Request a run launch (the orchestrator dispatches each new generation once).</summary>
    private static AppState LaunchRun(AppState s, IReadOnlyList<TestCaseId> subset) =>
        s with { RunGeneration = s.RunGeneration + 1, RunSubset = subset, Running = true };

    // --- 'o' modal (M5) ---------------------------------------------------------

    private static AppState OpenModal(AppState s)
    {
        var (node, _) = Selected(s);
        if (node is null || !node.HasResolvedRefs) return Toast(s, "no file references");
        return s with { Modal = BuildModal(s, node.FileRefs, FirstResolvedIndex(node.FileRefs)) };
    }

    private static AppState ModalKey(AppState s, ConsoleKeyInfo k)
    {
        var m = s.Modal!;
        switch (k.Key)
        {
            case ConsoleKey.Escape: return s with { Modal = null };
            case ConsoleKey.UpArrow: return ModalScrollTo(s, m.Scroll - 1);
            case ConsoleKey.DownArrow: return ModalScrollTo(s, m.Scroll + 1);
            case ConsoleKey.PageUp: return ModalScrollTo(s, m.Scroll - ModalPage(s));
            case ConsoleKey.PageDown: return ModalScrollTo(s, m.Scroll + ModalPage(s));
            case ConsoleKey.Home: return ModalScrollTo(s, 0);
            case ConsoleKey.End: return ModalScrollTo(s, int.MaxValue);
        }

        switch (k.KeyChar)
        {
            case 'c': return s with { Modal = null };
            case 'n': return s with { Modal = BuildModal(s, m.Refs, Step(m.Refs, m.Index, +1)) };
            case 'p': return s with { Modal = BuildModal(s, m.Refs, Step(m.Refs, m.Index, -1)) };
            case 'w': return s with { Modal = m with { Wrap = !m.Wrap } };
            case 'k': return ModalScrollTo(s, m.Scroll - 1);
            case 'j': return ModalScrollTo(s, m.Scroll + 1);
        }
        return s;
    }

    private static int ModalPage(AppState s) => Math.Max(1, Layout.ModalContentRows(s.Height));

    private static AppState ModalScrollTo(AppState s, int index)
    {
        var m = s.Modal!;
        var max = Math.Max(0, m.Lines.Count - ModalPage(s));
        var scroll = Math.Clamp(index, 0, max);
        return scroll == m.Scroll ? s : s with { Modal = m with { Scroll = scroll } };
    }

    /// <summary>Build the modal for a given reference index: read the file (plain first paint) and
    /// scroll to the referenced line. Highlighting is applied later off-thread (plan §11.5).</summary>
    private static ModalState BuildModal(AppState s, IReadOnlyList<FileRef> refs, int index)
    {
        var r = refs[index];
        var lines = r.Exists
            ? ReadFileLines(r.Path)
            : new[] { $"(unresolved reference — file not found)", "", r.Path };
        var page = Layout.ModalContentRows(s.Height);
        var target = r.Line is { } ln ? Math.Max(0, ln - 1 - page / 3) : 0;
        var scroll = Math.Clamp(target, 0, Math.Max(0, lines.Count - page));
        return new ModalState
        {
            Refs = refs,
            Index = index,
            Scroll = scroll,
            Wrap = s.Modal?.Wrap ?? false,   // preserve wrap across n/p
            FilePath = r.Path,
            TargetLine = r.Line,
            Lines = lines,
            Highlighted = null,              // orchestrator fills this in for existing files
        };
    }

    private static int FirstResolvedIndex(IReadOnlyList<FileRef> refs)
    {
        for (var i = 0; i < refs.Count; i++)
            if (refs[i].Exists) return i;
        return 0;
    }

    private static int Step(IReadOnlyList<FileRef> refs, int index, int dir)
    {
        if (refs.Count == 0) return 0;
        return ((index + dir) % refs.Count + refs.Count) % refs.Count;
    }

    private static IReadOnlyList<string> ReadFileLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException) { return ["(could not read file)"]; }
        catch (UnauthorizedAccessException) { return ["(could not read file)"]; }
    }

    // --- Highlight & toast events (M5/M6) --------------------------------------

    private static AppState HighlightReady(AppState s, AppEvent.HighlightReady h) =>
        s.Modal is { Highlighted: null } m && m.FilePath == h.FilePath
            ? s with { Modal = m with { Highlighted = h.Lines } }
            : s;

    private static AppState ToastExpired(AppState s, AppEvent.ToastExpired t) =>
        s.Toast is not null && s.ToastId == t.Id ? Relayout(s with { Toast = null }) : s;

    /// <summary>Set a transient toast (auto-cleared ~3s later by the orchestrator via <see cref="AppEvent.ToastExpired"/>).</summary>
    private static AppState Toast(AppState s, string text) =>
        Relayout(s with { Toast = text, ToastId = s.ToastId + 1 });

    // --- Selection normalisation ------------------------------------------------

    /// <summary>After a row set changes, keep the selection valid: survive by id, else move to the
    /// nearest surviving row (plan §11.4), and re-clamp scroll.</summary>
    private static AppState NormalizeView(AppState s, int prevIndex)
    {
        if (s.Rows.Count == 0)
            return s with { Selection = null, ScrollOffset = 0 };

        var idx = s.Selection is { } sel ? IndexOf(s.Rows, sel) : -1;
        if (idx < 0)
        {
            idx = Math.Clamp(prevIndex, 0, s.Rows.Count - 1);
            s = s with { Selection = s.Rows[idx].Node.Id };
        }
        var scroll = ClampScroll(s.ScrollOffset, idx, s.Viewport, s.Rows.Count);
        return s with { ScrollOffset = scroll };
    }

    // --- Tree construction & rollups -------------------------------------------

    private static TestNode EnsureLeaf(
        TestNode root, TestIdentity id, ImmutableHashSet<TestCaseId>.Builder expanded, ref bool structural)
    {
        var project = EnsureBranch(root, id.Project, TestNodeKind.Project,
            TestCaseId.ForBranch(TestNodeKind.Project, id.Project), id.Project, expanded, ref structural);

        // A registered single-TFM project collapses the TFM level (namespaces hang off the project);
        // multi-targeting projects and unregistered legacy fake scenarios keep the TFM node (brief M1).
        var container = project.NeedsTfmLevel
            ? EnsureBranch(project, id.Tfm, TestNodeKind.Tfm,
                TestCaseId.ForBranch(TestNodeKind.Tfm, id.Project, id.Tfm), id.Tfm, expanded, ref structural)
            : project;

        var ns = EnsureBranch(container, id.Namespace, TestNodeKind.Namespace,
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

    /// <summary>
    /// Store the finished leaf's detail and derive its ordered file references via TtrParser (brief M1).
    /// A pass/skip clears any stale failure detail (so vanish-on-pass and the detail pane stay honest).
    /// The parser's existence check touches the filesystem — a deliberate, bounded reduce-time read
    /// documented in docs/phase-2-notes.md. Relative paths resolve against CWD until real adapters
    /// (Phase 3) supply per-project directories; the Fake adapter embeds absolute paths.
    /// </summary>
    private static void AttachDetail(TestNode leaf, TestResultDetail? detail)
    {
        leaf.Detail = detail;
        leaf.FileRefs = detail is null || detail.IsEmpty
            ? []
            : TtrParser.FileReferenceParser.Parse(detail.Message, detail.StackTrace, projectDir: "");
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
        if (node.Kind == TestNodeKind.Notice) yield break;   // diagnostics are not test leaves
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
