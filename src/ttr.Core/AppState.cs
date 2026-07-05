using System.Collections.Immutable;

namespace Ttr.Core;

/// <summary>
/// An immutable snapshot of everything the UI needs to render a frame. Produced only by the
/// reducer (plan §5). The tree <see cref="Root"/> is a reference to mutable nodes updated in
/// place by the reducer; every other field is value/immutable. The render thread reads the
/// latest snapshot without locking.
/// </summary>
public sealed record AppState
{
    public required TestNode Root { get; init; }

    /// <summary>
    /// The flattened, expansion-honouring row list — computed by the reducer (the single writer)
    /// and published here. The render thread reads ONLY this snapshot (plus atomic scalar node
    /// fields), never enumerating the mutable child lists, so there is no read/write race and no
    /// lock (CLAUDE.md invariant 1). Rebuilt only when structure or expansion changes.
    /// </summary>
    public IReadOnlyList<FlatRow> Rows { get; init; } = [];

    /// <summary>Ids of branch nodes that are expanded.</summary>
    public ImmutableHashSet<TestCaseId> Expanded { get; init; } = ImmutableHashSet<TestCaseId>.Empty;

    /// <summary>Currently selected node id (keyed on the stable id so it survives tree changes).</summary>
    public TestCaseId? Selection { get; init; }

    /// <summary>First visible row index into the flattened list.</summary>
    public int ScrollOffset { get; init; }

    /// <summary>Number of tree rows the viewport can show (derived via <see cref="Layout"/> on resize/toggle).</summary>
    public int Viewport { get; init; } = 20;

    /// <summary>Last-known terminal size (from <see cref="AppEvent.Resized"/>); drives layout math.</summary>
    public int Width { get; init; } = 80;
    public int Height { get; init; } = 24;

    public string ScenarioName { get; init; } = "";

    /// <summary>
    /// Whether test execution is available this session (plan §14 Phase 3 = read-only). Fake scenarios
    /// set this true; real targets (and the <c>backend</c> fake, which simulates a real read-only run)
    /// set it false, so <c>r</c>/<c>R</c> politely toast "runs arrive in Phase 4" instead of launching.
    /// </summary>
    public bool RunsEnabled { get; init; } = true;

    /// <summary>True while a run is in progress (drives spinner animation / header state).</summary>
    public bool Running { get; init; }

    /// <summary>True while any project is building (plan §7). Drives the build-spinner animation the same
    /// way <see cref="Running"/> drives the run spinner — the render loop ticks while either is set.</summary>
    public bool Busy { get; init; }

    /// <summary>Wall-clock elapsed of the current/last run (shown separately from summed durations, plan §11.2).</summary>
    public TimeSpan RunWallClock { get; init; }

    // --- Detail pane (M2) -------------------------------------------------------
    public bool DetailVisible { get; init; }
    public DetailOrientation DetailOrientation { get; init; } = DetailOrientation.Right;
    public bool WordWrap { get; init; }
    public PaneFocus Focus { get; init; } = PaneFocus.Tree;
    /// <summary>Independent scroll offset (display lines) of the detail pane.</summary>
    public int DetailScroll { get; init; }

    // --- Filter & durations (M3) ------------------------------------------------
    public bool FailedOnly { get; init; }
    public bool ShowDurations { get; init; }

    // --- Rerun / effects (M4) ---------------------------------------------------
    /// <summary>Bumped when the orchestrator should launch a run; it dispatches each new generation once.</summary>
    public long RunGeneration { get; init; }
    /// <summary>Subset for the pending run launch (empty = all).</summary>
    public IReadOnlyList<TestCaseId> RunSubset { get; init; } = [];
    /// <summary>Rerun requests accumulated while a run is active; coalesced into one follow-up (plan §9).</summary>
    public ImmutableHashSet<TestCaseId> QueuedRerun { get; init; } = ImmutableHashSet<TestCaseId>.Empty;
    /// <summary>Set when a queued rerun is "run everything" (R during an active run).</summary>
    public bool QueuedRerunAll { get; init; }

    // --- Watch (Phase 5, plan §9) ----------------------------------------------
    /// <summary>Off, or which source drives watch mode. Off ⇒ no header segment ⇒ non-watch snapshots
    /// stay byte-identical to earlier phases (brief M1).</summary>
    public WatchKind Watch { get; init; } = WatchKind.Off;

    /// <summary>The watch pipeline's coarse activity (change-detected / queued / idle); building and
    /// running are derived from <see cref="Busy"/>/<see cref="Running"/> for the header.</summary>
    public WatchActivity WatchActivity { get; init; } = WatchActivity.Idle;

    /// <summary>Number of tests in the current run (set when a run launches) — the <c>m</c> in the watch
    /// header's <c>running (n/m)</c>. Zero outside a run.</summary>
    public int RunTotal { get; init; }

    // --- Overlays & toast (M5/M6) ----------------------------------------------
    public ModalState? Modal { get; init; }
    public bool HelpVisible { get; init; }
    /// <summary>
    /// A restored-session header notice ("restored N results from &lt;relative time&gt;", Phase 6 brief M1),
    /// shown only after a <c>--continue</c> restore — null otherwise, so every non-restore snapshot stays
    /// byte-identical. Persists (unlike a toast) so the "these are old results" cue stays visible while
    /// browsing; degrade cases (targets/format changed) surface as a transient toast instead.
    /// </summary>
    public string? RestoreNotice { get; init; }

    /// <summary>Transient one-line toast; auto-cleared ~3s after it appears (M6).</summary>
    public string? Toast { get; init; }
    /// <summary>Monotonic id so a stale <see cref="AppEvent.ToastExpired"/> can't clear a newer toast.</summary>
    public long ToastId { get; init; }

    // --- Lifecycle ---
    public bool ShouldQuit { get; init; }
    public int ExitCode { get; init; }
    public string? FatalMessage { get; init; }

    // --- Change tracking ---
    /// <summary>Bumped on every produced snapshot — the render loop's dirty signal.</summary>
    public long Revision { get; init; }

    /// <summary>Bumped only when tree structure or expansion changes — the flatten-cache key.</summary>
    public int TreeVersion { get; init; }

    // --- Convenience rollups (read straight off the root) ---
    public int TotalTests => Root.TotalLeaves;
    public int Passed => Root.Passed;
    public int Failed => Root.Failed;
    public int Skipped => Root.Skipped;
    public int RunningCount => Root.Running;
    public int NotRunCount => Root.NotRun;

    public static AppState Initial(string scenarioName, bool runsEnabled = true, string rootName = "(fake)")
    {
        var root = new TestNode
        {
            Id = TestCaseId.ForBranch(TestNodeKind.Solution, "root"),
            Kind = TestNodeKind.Solution,
            Name = rootName,
        };
        var expanded = ImmutableHashSet.Create(root.Id);
        return new AppState
        {
            Root = root,
            ScenarioName = scenarioName,
            RunsEnabled = runsEnabled,
            Expanded = expanded,
            Rows = TreeFlattener.Flatten(root, expanded),
        };
    }
}
