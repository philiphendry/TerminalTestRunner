namespace Ttr.Core;

/// <summary>
/// Lifecycle state of a single test case (plan §5.1). The integer values double as
/// indices into per-node rollup count arrays, so the order here is load-bearing.
/// </summary>
public enum TestStatus
{
    NotRun = 0,
    Queued = 1,
    Running = 2,
    Passed = 3,
    Failed = 4,
    Skipped = 5,

    /// <summary>Restored (--continue) or pre-rebuild result whose assembly has since changed.</summary>
    Stale = 6,
}

/// <summary>Terminal outcome reported by an adapter when a test finishes.</summary>
public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>
/// Node kinds in the test tree (plan §5.1). A Method node is a leaf for a plain test and a
/// branch (with Case children) for a parameterised theory.
/// </summary>
public enum TestNodeKind
{
    Solution,
    Project,
    Tfm,
    Namespace,
    Class,
    Method,
    Case,

    /// <summary>A standalone diagnostic node (phantom project, unparseable solution entry,
    /// smoke-validation failure). Carries a <see cref="NodeNotice"/>; never counted as a test leaf.</summary>
    Notice,
}
