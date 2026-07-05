namespace Ttr.Core.Persistence;

/// <summary>
/// An immutable, decoupled copy of everything a save needs, taken on the reducer thread (brief M3) so the
/// background writer never walks the live mutable tree (the render-thread hazard, CLAUDE.md invariant 1).
/// <see cref="Capture"/> collects only leaves with a terminal result (plus restored-detail carry-forward),
/// and the current UI state.
/// </summary>
/// <summary>One test's persisted summary + its detail source. <see cref="Detail"/> is the live in-memory
/// detail (a run produced it this session); <see cref="RestoredDetailOnDisk"/> marks a restored result whose
/// detail was never opened, so the writer carries it forward from the previous run rather than losing it.</summary>
public sealed record SessionTest(
    TestCaseId Id, TestStatus Status, TimeSpan Duration, TestResultDetail? Detail, bool RestoredDetailOnDisk)
{
    public bool HasDetail => Detail is not null || RestoredDetailOnDisk;
}

public sealed record SessionSnapshot(IReadOnlyList<SessionTest> Tests, RestoredUi Ui)
{
    /// <summary>Snapshot the terminal-status leaves and UI state of <paramref name="s"/> (pure — no IO).</summary>
    public static SessionSnapshot Capture(AppState s)
    {
        var tests = new List<SessionTest>();
        Collect(s.Root, tests);
        return new SessionSnapshot(tests, CaptureUi(s));
    }

    private static void Collect(TestNode node, List<SessionTest> into)
    {
        if (node.Kind == TestNodeKind.Notice) return;
        if (node.IsLeaf)
        {
            // Only durable results are worth persisting; NotRun/Queued/Running carry nothing to restore.
            if (node.Status is TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)
                into.Add(new SessionTest(
                    node.Id, node.Status, node.Duration, node.Detail,
                    RestoredDetailOnDisk: node.Detail is null && node.HasRestoredDetail));
            return;
        }
        foreach (var child in node.Children) Collect(child, into);
    }

    private static RestoredUi CaptureUi(AppState s) => new(
        FailedOnly: s.FailedOnly,
        ShowDurations: s.ShowDurations,
        DetailVisible: s.DetailVisible,
        DetailBottom: s.DetailOrientation == DetailOrientation.Beneath,
        WordWrap: s.WordWrap,
        Expanded: s.Expanded.Select(id => id.Value).ToList(),
        Selected: s.Selection?.Value,
        TreeScroll: s.ScrollOffset,
        DetailScroll: s.DetailScroll);
}
