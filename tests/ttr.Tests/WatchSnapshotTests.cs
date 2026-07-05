using Ttr.Core;
using Ttr.Runners;
using Xunit;
using static Ttr.Tests.TestKit;
using static VerifyXunit.Verifier;

namespace Ttr.Tests;

/// <summary>
/// The Phase 5 watch UX contract (brief M1), snapshotted fake-first: each watch header state (idle / change
/// detected / building / running / queued), and the tree before/after a re-discovery diff that adds one test
/// and removes one. Non-watch snapshots are untouched — the header segment only appears when
/// <see cref="AppState.Watch"/> is set, so every earlier-phase snapshot stays byte-identical (AC8).
/// </summary>
public class WatchSnapshotTests
{
    private const string Proj = FakeWatchScript.Project;

    // --- Header states ----------------------------------------------------------

    [Fact]
    public Task Watch_idle() => Verify(Render(WatchRan(), 100, 16));

    [Fact]
    public Task Watch_change_detected() =>
        Verify(Render(Feed(WatchRan(), new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected)), 100, 16));

    [Fact]
    public Task Watch_building() =>
        Verify(Render(Feed(WatchRan(),
            new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected),
            new AppEvent.BuildStarted(Proj)), 100, 16));

    [Fact]
    public Task Watch_running()
    {
        // Auto-rerun launched; one test has finished so the header shows running (n/m).
        var s = Feed(WatchRan(), new AppEvent.WatchRerunRequested([Proj]));
        s = Feed(s, new AppEvent.TestStarted(FakeWatchScript.Add),
            new AppEvent.TestFinished(FakeWatchScript.Add, TestOutcome.Passed, TimeSpan.FromMilliseconds(2)));
        return Verify(Render(s, 100, 16));
    }

    [Fact]
    public Task Watch_queued_change_during_run()
    {
        var s = Feed(WatchRan(),
            new AppEvent.WatchRerunRequested([Proj]),
            new AppEvent.WatchStateChanged(WatchActivity.Queued));
        return Verify(Render(s, 100, 16));
    }

    // --- Re-discovery diff (add + remove), tree before/after --------------------

    [Fact]
    public Task Watch_diff_before() => Verify(Render(WatchRan(), 100, 16));

    [Fact]
    public Task Watch_diff_after()
    {
        // −Multiply, +Divide; Add (pass) and Subtract (fail) results are preserved, Divide is NotRun.
        var s = Feed(WatchRan(),
            new AppEvent.RediscoveryStarted([Proj]),
            new AppEvent.TestsDiscovered(FakeWatchScript.AfterDiff),
            new AppEvent.RediscoveryCompleted([Proj]));
        return Verify(Render(s, 100, 16));
    }
}
