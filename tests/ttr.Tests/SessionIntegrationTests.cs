using Ttr.Core;
using Ttr.Core.Persistence;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// End-to-end persistence (brief M6): a full session (discover → run → toggle UI → quit-save) round-trips
/// through the real <see cref="SessionStore"/>, then a "new process" discovers the same tree, restores, and —
/// via the CLI's staleness rule — shows the changed project's results as Stale; a lazy detail fetch + a watch
/// rebuild are then exercised on the restored tree. No external test hosts (those are covered elsewhere); this
/// pins the store↔reducer contract.
/// </summary>
public sealed class SessionIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _stateDir;

    public SessionIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttr-int-" + Guid.NewGuid().ToString("n"));
        _stateDir = Path.Combine(_root, ".ttr");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private SessionStore Store() => new(_stateDir, [new SessionDocument.TargetEntry { Path = "x", Hash = "sha256:0" }]);

    [Fact]
    public void Session_round_trips_results_ui_and_goes_stale()
    {
        var pass = Id("N", "C", "Passes");
        var fail = Id("N", "C", "Fails");
        var detail = new TestResultDetail(Message: "nope", StackTrace: "at N.C.Fails() in /repo/C.cs:line 3");

        // --- Session 1: discover, run, toggle UI, select the failure, then save (as on quit). ---
        var s = Discover(AppState.Initial("Sln"), pass, fail);
        s = Feed(s,
            new AppEvent.TestStarted(pass), new AppEvent.TestFinished(pass, TestOutcome.Passed, TimeSpan.FromMilliseconds(4)),
            new AppEvent.TestStarted(fail), new AppEvent.TestFinished(fail, TestOutcome.Failed, TimeSpan.FromMilliseconds(9), detail),
            new AppEvent.RunCompleted());
        s = Press(s, Char('t'), Char('s'));   // durations + detail pane on
        s = SelectRow(s, fail.Id);
        var selectedBefore = s.Selection;

        Store().Save(SessionSnapshot.Capture(s));

        // --- Session 2 (fresh process): one store, discover the same tree, restore. ---
        var store2 = Store();
        var outcome = store2.Restore();
        Assert.Equal(RestoreStatus.Restored, outcome.Status);
        var session = outcome.Session!;

        var s2 = Discover(AppState.Initial("Sln"), pass, fail);
        // The CLI computes staleness (assembly newer than the result). Here: the failing test's project changed.
        var results = session.Tests.Select(t =>
            new RestoredResult(t.Id, t.Status, t.Duration, t.HasDetail, Stale: t.Id.Equals(fail.Id))).ToList();
        s2 = Feed(s2, new AppEvent.SessionRestored(results, session.Ui, "2 min ago"));

        // Results restored.
        Assert.Equal(1, s2.Passed);
        Assert.Equal(1, s2.Failed);
        // UI restored.
        Assert.True(s2.ShowDurations);
        Assert.True(s2.DetailVisible);
        Assert.Equal(selectedBefore, s2.Selection);
        Assert.Contains("restored 2 results", s2.RestoreNotice);

        // The changed test is Stale (dim), the unchanged one is not.
        Assert.True(FindLeaf(s2, fail.Id).IsStale);
        Assert.False(FindLeaf(s2, pass.Id).IsStale);

        // --- Lazy detail on the restored failure: the store serves it, the reducer attaches + parses refs. ---
        Assert.True(FindLeaf(s2, fail.Id).HasRestoredDetail);
        var loaded = store2.ReadDetail(fail.Id);
        Assert.NotNull(loaded);
        s2 = Feed(s2, new AppEvent.DetailLoaded(fail.Id, loaded!));
        Assert.Equal("nope", FindLeaf(s2, fail.Id).Detail!.Message);
        Assert.False(FindLeaf(s2, fail.Id).HasRestoredDetail);

        // --- A watch rebuild marks the kept results Stale until a rerun would replace them. ---
        s2 = Feed(s2,
            new AppEvent.RediscoveryStarted([Project]),
            new AppEvent.TestsDiscovered([pass, fail]),
            new AppEvent.RediscoveryCompleted([Project]));
        Assert.True(FindLeaf(s2, pass.Id).IsStale);
        Assert.True(FindLeaf(s2, fail.Id).IsStale);

        // A real rerun clears it.
        s2 = Feed(s2, new AppEvent.TestStarted(pass), new AppEvent.TestFinished(pass, TestOutcome.Passed, TimeSpan.FromMilliseconds(3)));
        Assert.False(FindLeaf(s2, pass.Id).IsStale);
    }

    private static TestNode FindLeaf(AppState s, TestCaseId id)
    {
        TestNode? Walk(TestNode n) => n.Id.Equals(id) ? n : n.Children.Select(Walk).FirstOrDefault(x => x is not null);
        return Walk(s.Root)!;
    }
}
