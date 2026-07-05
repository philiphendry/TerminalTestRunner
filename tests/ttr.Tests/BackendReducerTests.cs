using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>Reducer coverage for the Phase 3 backend events (brief M1): TFM-level collapse, build
/// phase, standalone + smoke notices, and the read-only rerun guard. Pure — no terminal, no processes.</summary>
public class BackendReducerTests
{
    private const string Proj = "matrix/Api.Tests/Api.Tests.csproj";

    private static AppState Fresh() => AppState.Initial("backend", runsEnabled: false);

    private static TestIdentity ProjId(string tfm, string method) =>
        new(TestCaseId.ForCase("vstest", Proj, tfm, $"N.C.{method}", null), Proj, tfm, "N", "C", method);

    [Fact]
    public void Registered_single_tfm_project_collapses_the_tfm_level()
    {
        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.TestsDiscovered([ProjId("net10.0", "T1")]));

        var project = Assert.Single(s.Root.Children);
        Assert.Equal("Api.Tests", project.Name);
        // No TFM node: the namespace hangs directly off the project.
        var ns = Assert.Single(project.Children);
        Assert.Equal(TestNodeKind.Namespace, ns.Kind);
    }

    [Fact]
    public void Registered_multi_tfm_project_inserts_tfm_nodes()
    {
        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0", "net8.0"], RunnerKind.Mtp),
            new AppEvent.TestsDiscovered([ProjId("net10.0", "T1"), ProjId("net8.0", "T1")]));

        var project = Assert.Single(s.Root.Children);
        Assert.Equal(2, project.Children.Count);
        Assert.All(project.Children, c => Assert.Equal(TestNodeKind.Tfm, c.Kind));
        Assert.Equal(2, project.TotalLeaves);
    }

    [Fact]
    public void Unregistered_project_keeps_the_tfm_node()
    {
        // Legacy fake path: no ProjectRegistered → the TFM node is always inserted (Phase 1/2 shape).
        var s = Discover(Fresh(), Id("N", "C", "T1"));
        var project = Assert.Single(s.Root.Children);
        var tfm = Assert.Single(project.Children);
        Assert.Equal(TestNodeKind.Tfm, tfm.Kind);
    }

    [Fact]
    public void Build_failed_sets_phase_diagnostics_and_derives_file_refs()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "fake-sources", "Calculator.cs");
        var raw = $"{file}(17,20): error CS1002: ; expected\n    1 Error(s)";
        var diag = new BuildDiagnostic(file, 17, 20, "CS1002", "; expected");

        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.BuildStarted(Proj),
            new AppEvent.BuildFailed(Proj, [diag], raw));

        var project = Assert.Single(s.Root.Children);
        Assert.Equal(BuildPhase.Failed, project.BuildPhase);
        Assert.Single(project.BuildDiagnostics);
        Assert.Equal(raw, project.BuildOutput);
        Assert.False(s.Busy);                       // no project left building
        Assert.True(project.HasResolvedRefs);       // 'o' works on build errors (the fixture file exists)
    }

    [Fact]
    public void Build_started_marks_busy_until_it_completes()
    {
        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.BuildStarted(Proj));
        Assert.True(s.Busy);
        s = Feed(s, new AppEvent.BuildSucceeded(Proj));
        Assert.False(s.Busy);
    }

    [Fact]
    public void Discovery_failure_adds_a_notice_child_never_an_empty_subtree()
    {
        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0"], RunnerKind.Mtp),
            new AppEvent.DiscoveryFailed(Proj, null,
                new NodeNotice(NoticeSeverity.Warning, "test project discovered zero tests")));

        var project = Assert.Single(s.Root.Children);
        var notice = Assert.Single(project.Children);
        Assert.Equal(TestNodeKind.Notice, notice.Kind);
        Assert.Equal(NoticeSeverity.Warning, notice.Notice!.Severity);
        Assert.Equal(0, project.TotalLeaves);       // the notice is not a test leaf
    }

    [Fact]
    public void Raised_notice_is_a_root_child_and_not_counted_as_a_test()
    {
        var s = Feed(Fresh(), new AppEvent.NoticeRaised("phantom:X", "X.Tests",
            new NodeNotice(NoticeSeverity.Warning, "project file not found")));

        var notice = Assert.Single(s.Root.Children);
        Assert.Equal(TestNodeKind.Notice, notice.Kind);
        Assert.Equal(0, s.Root.TotalLeaves);
    }

    [Fact]
    public void Rerun_is_disabled_and_toasts_when_runs_are_not_supported()
    {
        var s = Feed(Fresh(),
            new AppEvent.ProjectRegistered(Proj, "Api.Tests", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.TestsDiscovered([ProjId("net10.0", "T1")]));

        var after = Press(s, Char('R'));
        Assert.False(after.Running);
        Assert.Equal(s.RunGeneration, after.RunGeneration);   // no run launched
        Assert.Equal("runs arrive in Phase 4", after.Toast);
    }
}
