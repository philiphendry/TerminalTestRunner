using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>Quit exit codes (plan §3, brief M1): 0 clean · 1 failing tests · 3 build failure preventing any
/// run · 130 Ctrl+C. Pure reducer transitions.</summary>
public class ExitCodeTests
{
    private static ConsoleKeyInfo Q => Char('q');
    private static ConsoleKeyInfo CtrlC => Key('\u0003', ConsoleKey.C, control: true);

    private static AppState WithOneLeaf(TestOutcome outcome)
    {
        var id = Id("N", "C", "m");
        var s = Discover(AppState.Initial("t"), id);
        s = Feed(s,
            new AppEvent.TestStarted(id),
            new AppEvent.TestFinished(id, outcome, TimeSpan.FromMilliseconds(1), null));
        return Reducer.Reduce(s, new AppEvent.RunCompleted());
    }

    [Fact]
    public void Clean_all_pass_exits_0() =>
        Assert.Equal(0, Reducer.Reduce(WithOneLeaf(TestOutcome.Passed), Q).ExitCode);

    [Fact]
    public void Failing_tests_exit_1() =>
        Assert.Equal(1, Reducer.Reduce(WithOneLeaf(TestOutcome.Failed), Q).ExitCode);

    [Fact]
    public void CtrlC_exits_130() =>
        Assert.Equal(130, Reducer.Reduce(WithOneLeaf(TestOutcome.Passed), CtrlC).ExitCode);

    [Fact]
    public void Build_failure_preventing_any_run_exits_3()
    {
        // Register a project, fail its build, never run anything: q → 3.
        var s = AppState.Initial("t", runsEnabled: true);
        const string proj = "/x/P.csproj";
        s = Feed(s,
            new AppEvent.ProjectRegistered(proj, "P", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.BuildStarted(proj),
            new AppEvent.BuildFailed(proj, [], "P.csproj(1,1): error CS0000: boom"));
        Assert.Equal(0, s.Passed + s.Failed + s.Skipped);   // nothing ran
        Assert.Equal(3, Reducer.Reduce(s, Q).ExitCode);
    }

    [Fact]
    public void Build_failure_that_still_let_tests_run_is_not_3()
    {
        // A partial build failure where another test still ran (and passed) is 0, not 3 — plan §3 reserves
        // 3 for "prevented ANY run".
        var s = WithOneLeaf(TestOutcome.Passed);
        const string proj = "/x/Broken.csproj";
        s = Feed(s,
            new AppEvent.ProjectRegistered(proj, "Broken", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.BuildFailed(proj, [], "boom"));
        Assert.Equal(0, Reducer.Reduce(s, Q).ExitCode);
    }
}
