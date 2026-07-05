using Ttr.Core;
using Ttr.Runners;
using TtrParser;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// Regression suite for the file-reference parser (plan §11.5 / POC-9 spec). Built against the
/// committed <c>fixtures/fake-sources/</c> files (copied beside the test assembly). NOTE: when the
/// real POC-9 corpus surfaces, replace TtrParser verbatim and swap this for the corpus-driven suite.
/// </summary>
public class TtrParserTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fake-sources", name);

    [Fact]
    public void Stack_frames_resolve_in_trace_order_top_first()
    {
        var stack =
            $"   at A.B.Add() in {Fixture("Calculator.cs")}:line 17\n" +
            $"   at A.B.Format() in {Fixture("StringUtilities.cs")}:line 12\n";
        var refs = FileReferenceParser.Parse(null, stack, "");

        Assert.Equal(2, refs.Count);
        Assert.EndsWith("Calculator.cs", refs[0].Path);
        Assert.Equal(17, refs[0].Line);
        Assert.True(refs[0].Exists);
        Assert.EndsWith("StringUtilities.cs", refs[1].Path);
        Assert.Equal(12, refs[1].Line);
    }

    [Fact]
    public void Obj_and_generated_frames_are_dropped_entirely()
    {
        var objPath = Fixture(Path.Combine("obj", "Debug", "Glue.g.cs"));
        var stack =
            $"   at Gen.Glue() in {objPath}:line 42\n" +
            $"   at A.B.Add() in {Fixture("Calculator.cs")}:line 17\n";
        var refs = FileReferenceParser.Parse(null, stack, "");

        Assert.Single(refs);
        Assert.EndsWith("Calculator.cs", refs[0].Path);
        Assert.DoesNotContain(refs, r => r.Path.Contains("obj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nonexistent_frames_are_kept_but_marked_unresolved()
    {
        var stack =
            $"   at A.B.Add() in {Fixture("Calculator.cs")}:line 17\n" +
            "   at Fx.Runner.Run() in /_/src/Framework/Runner.cs:line 108\n";
        var refs = FileReferenceParser.Parse(null, stack, "");

        Assert.Equal(2, refs.Count);
        Assert.True(refs[0].Exists);
        Assert.False(refs[1].Exists);   // dim/unresolved in the modal
    }

    [Fact]
    public void Duplicate_path_and_line_deduped()
    {
        var stack =
            $"   at A.B.X() in {Fixture("Calculator.cs")}:line 17\n" +
            $"   at A.B.Y() in {Fixture("Calculator.cs")}:line 17\n";
        Assert.Single(FileReferenceParser.Parse(null, stack, ""));
    }

    [Fact]
    public void Message_bare_paths_with_known_extensions_are_found()
    {
        var msg = $"Expected file {Fixture("config.json")} to contain key; see {Fixture("notes.txt")}.";
        var refs = FileReferenceParser.Parse(msg, null, "");

        Assert.Contains(refs, r => r.Path.EndsWith("config.json", StringComparison.Ordinal) && r.Exists);
        Assert.Contains(refs, r => r.Path.EndsWith("notes.txt", StringComparison.Ordinal) && r.Exists);
    }

    [Fact]
    public void Msbuild_diagnostic_form_is_parsed()
    {
        var msg = $"{Fixture("Calculator.cs")}(17,9): error CS0219: variable is assigned but never used";
        var refs = FileReferenceParser.Parse(msg, null, "");
        Assert.Contains(refs, r => r.Kind == SourceKind.MsBuild && r.Line == 17);
    }

    [Fact]
    public void Relative_paths_resolve_against_project_dir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fake-sources");
        var refs = FileReferenceParser.Parse("see ./Calculator.cs for details", null, dir);
        Assert.Contains(refs, r => r.Exists && r.Path.EndsWith("Calculator.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_scenario_failures_resolve_refs_through_the_reducer()
    {
        // End-to-end M1 wiring: build the 'files' scenario, drive it through the reducer, and confirm
        // the crafted failures produced resolved file references (via TtrParser at reduce time).
        var scenario = ScenarioBuilder.Build("files", 7);
        var state = AppState.Initial("files");
        foreach (var plan in scenario.Plans.Where(p => p.DiscoverUpfront))
            state = Reducer.Reduce(state, new AppEvent.TestsDiscovered([plan.Identity]));
        foreach (var plan in scenario.Plans)
        {
            state = Reducer.Reduce(state, new AppEvent.TestStarted(plan.Identity));
            var detail = plan.Outcome == TestOutcome.Failed ? plan.Detail : null;
            state = Reducer.Reduce(state,
                new AppEvent.TestFinished(plan.Identity, plan.Outcome, plan.Duration, detail));
        }

        var failedLeaves = Leaves(state.Root).Where(n => n.Status == TestStatus.Failed).ToList();
        Assert.NotEmpty(failedLeaves);
        Assert.Contains(failedLeaves, n => n.HasResolvedRefs);
        // At least one failure carries an unresolved (dim) ref and never an obj/.g.cs target.
        Assert.Contains(failedLeaves, n => n.FileRefs.Any(r => !r.Exists));
        Assert.DoesNotContain(failedLeaves,
            n => n.FileRefs.Any(r => r.Path.Replace('\\', '/').Contains("/obj/")));
    }

    private static IEnumerable<TestNode> Leaves(TestNode node)
    {
        if (node.IsLeaf) { yield return node; yield break; }
        foreach (var c in node.Children)
            foreach (var l in Leaves(c))
                yield return l;
    }
}
