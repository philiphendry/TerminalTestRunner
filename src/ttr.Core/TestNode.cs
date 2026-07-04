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

    /// <summary>Per-status leaf counts of this subtree (index by <see cref="TestStatus"/>).</summary>
    public int[] Counts { get; } = new int[7];

    /// <summary>Total leaves in this subtree.</summary>
    public int TotalLeaves { get; set; }

    /// <summary>Summed leaf duration across this subtree.</summary>
    public TimeSpan RollupDuration { get; set; }

    public TestNode? FindChild(string key) => _childrenByKey.GetValueOrDefault(key);

    public TestNode AddChild(string key, TestNode child)
    {
        _childrenByKey[key] = child;
        Children.Add(child);
        return child;
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
