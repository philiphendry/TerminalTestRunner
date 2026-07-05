using Ttr.Build;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// The reverse-dependents closure (brief M2, replicating POC-6's three correctness cases). The closure walk
/// is pure and tested here over the same reverse-edge shape as <c>fixtures/watch</c> — the diamond
/// (A←{B,C}←D←E←F), the two independent test roots (TestsCore→A, TestsApp→F), and the unrelated chain
/// (X←Y, TestsX→X). The REAL <see cref="ProjectGraphService"/> loading this graph from MSBuild is verified
/// end-to-end by the watch integration/tmux runs (editing LibA cycles TestsCore + TestsApp, never TestsX).
/// </summary>
public class ProjectGraphTests
{
    // reverse[p] = projects that directly reference p (the dependents edges).
    private static Dictionary<string, List<string>> WatchFixtureReverse() => new(StringComparer.Ordinal)
    {
        ["A"] = ["B", "C", "TestsCore"],
        ["B"] = ["D"],
        ["C"] = ["D"],
        ["D"] = ["E"],
        ["E"] = ["F"],
        ["F"] = ["TestsApp"],
        ["X"] = ["Y", "TestsX"],
        ["Y"] = [],
        ["TestsCore"] = [],
        ["TestsApp"] = [],
        ["TestsX"] = [],
    };

    [Fact]
    public void Chained_and_diamond_change_implicates_both_test_roots()
    {
        var closure = ProjectGraphService.WalkClosure(WatchFixtureReverse(), ["A"]);
        // A → B,C → D → E → F; plus the direct TestsCore→A edge.
        Assert.Contains("TestsCore", closure);   // direct
        Assert.Contains("TestsApp", closure);    // transitive through the diamond + chain
        Assert.Contains("D", closure);           // diamond join reached once
        Assert.Contains("A", closure);           // inclusive of the changed project
    }

    [Fact]
    public void Unrelated_project_is_untouched()
    {
        var closure = ProjectGraphService.WalkClosure(WatchFixtureReverse(), ["A"]);
        Assert.DoesNotContain("TestsX", closure);
        Assert.DoesNotContain("X", closure);
        Assert.DoesNotContain("Y", closure);
    }

    [Fact]
    public void Test_project_only_change_implicates_only_itself()
    {
        var closure = ProjectGraphService.WalkClosure(WatchFixtureReverse(), ["TestsApp"]);
        Assert.Equal(["TestsApp"], closure);   // nothing references a test project
    }

    [Fact]
    public void Independent_chain_change_stays_within_its_own_closure()
    {
        var closure = ProjectGraphService.WalkClosure(WatchFixtureReverse(), ["X"]);
        Assert.Contains("TestsX", closure);
        Assert.Contains("Y", closure);
        Assert.DoesNotContain("TestsCore", closure);
        Assert.DoesNotContain("TestsApp", closure);
    }

    [Fact]
    public void A_leaf_dependency_edit_reaches_every_transitive_dependent_once()
    {
        var closure = ProjectGraphService.WalkClosure(WatchFixtureReverse(), ["A"]);
        // The diamond must not double-visit D (BFS dedups): assert the full expected set.
        var expected = new HashSet<string> { "A", "B", "C", "D", "E", "F", "TestsCore", "TestsApp" };
        Assert.Equal(expected, closure.ToHashSet());
    }
}
