using Ttr.Core;
using Ttr.Core.Persistence;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// Persistence-layer tests (brief M6): state.json round-trip, the fingerprint mismatch / schema / corrupt
/// degrade paths, packed-detail lazy read + carry-forward, run pruning to 3, and the .gitignore write. Uses a
/// throwaway temp state dir (also exercises --state-dir, which is just an arbitrary directory).
/// </summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _stateDir;
    private readonly string _projPath;

    public SessionStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttr-store-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        _projPath = Path.Combine(_root, "Sample.Tests.csproj");
        File.WriteAllText(_projPath, "<Project><!-- v1 --></Project>");
        _stateDir = Path.Combine(_root, ".ttr");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SessionStore Store() => new(_stateDir, SessionStore.Fingerprint(_projPath, [_projPath]));

    private static TestCaseId CaseId(string method)
        => TestCaseId.ForCase("fake", "Sample.Tests.csproj", "net10.0", $"N.C.{method}", null);

    private static RestoredUi Ui(string? selected = null) =>
        new(FailedOnly: true, ShowDurations: true, DetailVisible: true, DetailBottom: true, WordWrap: true,
            Expanded: ["a", "b"], Selected: selected, TreeScroll: 7, DetailScroll: 3);

    private static SessionSnapshot Snapshot(params SessionTest[] tests) => new(tests, Ui("N.C.A"));

    // --- Round-trip -------------------------------------------------------------

    [Fact]
    public void State_round_trips_summaries_and_ui()
    {
        Store().Save(Snapshot(
            new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.FromMilliseconds(12), null, false),
            new SessionTest(CaseId("B"), TestStatus.Failed, TimeSpan.FromMilliseconds(34), null, false)));

        var outcome = Store().Restore();
        Assert.Equal(RestoreStatus.Restored, outcome.Status);
        var session = outcome.Session!;
        Assert.Equal(2, session.Tests.Count);

        var a = session.Tests.Single(t => t.Id.Equals(CaseId("A")));
        Assert.Equal(TestStatus.Passed, a.Status);
        Assert.Equal(12, a.Duration.TotalMilliseconds, 0);

        Assert.True(session.Ui.FailedOnly);
        Assert.True(session.Ui.ShowDurations);
        Assert.True(session.Ui.DetailVisible);
        Assert.True(session.Ui.DetailBottom);
        Assert.True(session.Ui.WordWrap);
        Assert.Equal(7, session.Ui.TreeScroll);
        Assert.Equal(3, session.Ui.DetailScroll);
        Assert.Equal("N.C.A", session.Ui.Selected);
        Assert.Equal(["a", "b"], session.Ui.Expanded);
    }

    [Fact]
    public void Missing_state_is_NoState_not_a_crash()
        => Assert.Equal(RestoreStatus.NoState, Store().Restore().Status);

    [Fact]
    public void Fingerprint_mismatch_degrades_to_targets_changed()
    {
        Store().Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.Zero, null, false)));
        File.WriteAllText(_projPath, "<Project><!-- v2 EDITED --></Project>");   // a project file changed
        Assert.Equal(RestoreStatus.TargetsChanged, Store().Restore().Status);
    }

    [Fact]
    public void Corrupt_state_degrades_not_crashes()
    {
        Store().Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.Zero, null, false)));
        File.WriteAllText(Path.Combine(_stateDir, "state.json"), "{ this is not json ]");
        Assert.Equal(RestoreStatus.Corrupt, Store().Restore().Status);
    }

    [Fact]
    public void Schema_mismatch_degrades()
    {
        Store().Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.Zero, null, false)));
        var path = Path.Combine(_stateDir, "state.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\": 1", "\"schema\": 99"));
        Assert.Equal(RestoreStatus.SchemaMismatch, Store().Restore().Status);
    }

    // --- Packed details ---------------------------------------------------------

    [Fact]
    public void Detail_is_written_and_lazily_read()
    {
        var detail = new TestResultDetail(Message: "assertion failed", StackTrace: "at N.C.A() in Foo.cs:line 9");
        var store = Store();
        store.Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Failed, TimeSpan.Zero, detail, false)));

        var read = store.ReadDetail(CaseId("A"));
        Assert.NotNull(read);
        Assert.Equal("assertion failed", read!.Message);
        Assert.Equal("at N.C.A() in Foo.cs:line 9", read.StackTrace);
        Assert.Null(store.ReadDetail(CaseId("NoSuch")));
    }

    [Fact]
    public void Restored_detail_is_carried_forward_across_a_save()
    {
        var detail = new TestResultDetail(Message: "boom");
        var store = Store();
        store.Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Failed, TimeSpan.Zero, detail, false)));

        // A later save where A's detail was never loaded (Detail null, on-disk flag) must not lose it.
        store.Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Failed, TimeSpan.Zero, null, RestoredDetailOnDisk: true)));
        Assert.Equal("boom", store.ReadDetail(CaseId("A"))!.Message);

        // And state.json still reports hasDetail for A.
        var doc = Store().Restore().Session!;
        Assert.True(doc.Tests.Single(t => t.Id.Equals(CaseId("A"))).HasDetail);
    }

    // --- Housekeeping -----------------------------------------------------------

    [Fact]
    public void Runs_prune_to_three()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
            store.Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.Zero, null, false)));

        var runs = Directory.GetDirectories(Path.Combine(_stateDir, "results"));
        Assert.Equal(SessionStore.RunsToKeep, runs.Length);
    }

    [Fact]
    public void Gitignore_is_written_once()
    {
        Store().Save(Snapshot(new SessionTest(CaseId("A"), TestStatus.Passed, TimeSpan.Zero, null, false)));
        var gitignore = Path.Combine(_stateDir, ".gitignore");
        Assert.True(File.Exists(gitignore));
        Assert.Contains("*", File.ReadAllText(gitignore));
    }
}
