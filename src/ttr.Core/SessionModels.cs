namespace Ttr.Core;

/// <summary>
/// The persisted UI state (plan §10 / Appendix A <c>ui</c> block): the toggle flags, the expanded
/// branch id-set, the selected id, and the two scroll offsets. Ids are the derived
/// <see cref="TestCaseId.Value"/> strings (CLAUDE.md invariant 7) so they survive across sessions.
/// Reused verbatim by the persistence layer (round-trips through <c>state.json</c>) and by the reducer
/// (applied after the tree populates, brief M4).
/// </summary>
public sealed record RestoredUi(
    bool FailedOnly,
    bool ShowDurations,
    bool DetailVisible,
    bool DetailBottom,
    bool WordWrap,
    IReadOnlyList<string> Expanded,
    string? Selected,
    int TreeScroll,
    int DetailScroll)
{
    public static readonly RestoredUi Default =
        new(false, false, false, false, false, [], null, 0, 0);
}

/// <summary>
/// One restored test result the reducer attaches to a discovered leaf by derived <see cref="Id"/> (brief M4):
/// the summary status/duration, whether rich detail exists on disk (for lazy loading, brief M5), and whether
/// the result is <see cref="Stale"/> (its project's assembly is newer than the result — computed by the CLI
/// against the on-disk assembly, kept out of the pure reducer). Restored ids with no matching discovered leaf
/// are dropped and counted in the restore notice.
/// </summary>
public sealed record RestoredResult(
    TestCaseId Id,
    TestStatus Status,
    TimeSpan Duration,
    bool HasDetail,
    bool Stale);
