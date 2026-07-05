namespace Ttr.Core;

/// <summary>
/// A node in the test tree. Status and rollup counters are mutable and are written ONLY by the
/// reducer running on the single event-consumer (CLAUDE.md invariant 1: "all state mutation lives
/// [in the reducer] — no locks anywhere else"). Rollups are maintained incrementally by
/// delta-propagation up the parent chain, so a status change costs O(depth), never O(tree) — this
/// is what keeps the 10k-node "result storm" cheap.
/// </summary>
public sealed class TestNode
{
    private readonly Dictionary<string, TestNode> _childrenByKey = new(StringComparer.Ordinal);

    public required TestCaseId Id { get; init; }
    public required TestNodeKind Kind { get; init; }

    /// <summary>The display segment for this node (e.g. a namespace suffix, class name, method name).</summary>
    public required string Name { get; init; }

    public TestNode? Parent { get; init; }

    /// <summary>Ordered children (insertion order = discovery order).</summary>
    public List<TestNode> Children { get; } = [];

    public bool IsLeaf => Children.Count == 0;

    /// <summary>Own status — meaningful for leaves; branches derive display state from rollups.</summary>
    public TestStatus Status { get; set; } = TestStatus.NotRun;

    /// <summary>Own duration for leaves.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Rich outcome detail for a finished leaf (message/exception/stack/stdout); null until it finishes.</summary>
    public TestResultDetail? Detail { get; set; }

    /// <summary>
    /// Ordered file references parsed from <see cref="Detail"/> by the reducer (brief M1). Includes
    /// unresolved refs (<c>Exists == false</c>) in order so the 'o' modal can traverse and dim them.
    /// </summary>
    public IReadOnlyList<TtrParser.FileRef> FileRefs { get; set; } = [];

    /// <summary>True when this leaf has at least one file reference that resolved to an existing file.</summary>
    public bool HasResolvedRefs
    {
        get
        {
            foreach (var r in FileRefs)
                if (r.Exists) return true;
            return false;
        }
    }

    // --- Phase 3: project registration, build phase, and diagnostics -----------

    /// <summary>
    /// The project's evaluated target frameworks (Project nodes only), set by
    /// <see cref="AppEvent.ProjectRegistered"/>. <c>null</c> means the project was never registered
    /// (the legacy fake scenarios): the tree then always inserts a TFM node, preserving Phase 1/2
    /// shape. A single-element list collapses the TFM level (namespaces hang directly off the project);
    /// two or more inserts TFM child nodes (brief M1, plan §8/D9).
    /// </summary>
    public IReadOnlyList<string>? DeclaredTfms { get; set; }

    /// <summary>Whether the project needs an explicit TFM level. Meaningful once <see cref="DeclaredTfms"/> is set.</summary>
    public bool NeedsTfmLevel => DeclaredTfms is null || DeclaredTfms.Count > 1;

    /// <summary>Detected test platform for a Project node (plan §6.2); diagnostics only for other kinds.</summary>
    public RunnerKind Runner { get; set; } = RunnerKind.Fake;

    /// <summary>Build lifecycle of a Project node (plan §7); drives the build spinner / failure glyph.</summary>
    public BuildPhase BuildPhase { get; set; } = BuildPhase.None;

    /// <summary>Parsed build diagnostics attached on <see cref="AppEvent.BuildFailed"/>.</summary>
    public IReadOnlyList<BuildDiagnostic> BuildDiagnostics { get; set; } = [];

    /// <summary>Raw build output kept alongside parsed diagnostics as the user-openable fallback (plan §7).</summary>
    public string? BuildOutput { get; set; }

    /// <summary>
    /// A diagnostic attached to this node (brief M1): a classification note on a Project node
    /// (dead MTP opt-in, unknown runner, dual-mode info) or the payload of a standalone
    /// <see cref="TestNodeKind.Notice"/> node (phantom / unparseable / smoke failure).
    /// </summary>
    public NodeNotice? Notice { get; set; }

    /// <summary>
    /// Re-discovery tombstone (brief M3): set on every leaf under a project when a re-discovery cycle
    /// starts, cleared as each surviving test's <see cref="AppEvent.TestsDiscovered"/> re-arrives, then
    /// swept (removed) if still set when the cycle completes. Reducer-only, transient — never persisted.
    /// </summary>
    public bool PendingRemoval { get; set; }

    /// <summary>Per-status leaf counts of this subtree (index by <see cref="TestStatus"/>).</summary>
    public int[] Counts { get; } = new int[7];

    /// <summary>Total leaves in this subtree.</summary>
    public int TotalLeaves { get; set; }

    /// <summary>Summed leaf duration across this subtree.</summary>
    public TimeSpan RollupDuration { get; set; }

    public TestNode? FindChild(string key) => _childrenByKey.GetValueOrDefault(key);

    /// <summary>The structural key this node was added under (its key in <see cref="Parent"/>'s child map).
    /// Recorded so the re-discovery sweep can remove a node without re-deriving its key. Set by
    /// <see cref="AddChild"/>.</summary>
    public string? KeyInParent { get; private set; }

    public TestNode AddChild(string key, TestNode child)
    {
        _childrenByKey[key] = child;
        Children.Add(child);
        child.KeyInParent = key;
        return child;
    }

    /// <summary>Remove a child by its structural key (re-discovery removal, brief M3). Reducer-only.</summary>
    public void RemoveChild(string key)
    {
        if (_childrenByKey.Remove(key, out var child))
            Children.Remove(child);
    }

    // --- Rollup helpers (branch display state derived from leaf counters) ---

    public int Passed => Counts[(int)TestStatus.Passed];
    public int Failed => Counts[(int)TestStatus.Failed];
    public int Skipped => Counts[(int)TestStatus.Skipped];
    public int Running => Counts[(int)TestStatus.Running];
    public int Queued => Counts[(int)TestStatus.Queued];
    public int NotRun => Counts[(int)TestStatus.NotRun];
    public int Stale => Counts[(int)TestStatus.Stale];

    public bool AnyRunning => Running > 0;

    /// <summary>The single glyph a branch shows before its counts (plan §11.2).</summary>
    public TestStatus BranchStatus
    {
        get
        {
            if (Running > 0 || Queued > 0) return TestStatus.Running;
            if (Failed > 0) return TestStatus.Failed;
            if (Passed > 0 && NotRun == 0) return TestStatus.Passed;
            if (Skipped > 0 && Passed == 0 && Failed == 0 && NotRun == 0) return TestStatus.Skipped;
            return TestStatus.NotRun;
        }
    }
}
