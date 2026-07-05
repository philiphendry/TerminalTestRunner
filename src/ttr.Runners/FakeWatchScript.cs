using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// The deterministic <c>--fake --watch</c> simulation (brief M1). A tiny REGISTERED single-TFM project whose
/// watch cycles exercise every new UI state fake-first: the build spinner, a re-discovery diff that ADDS one
/// test (<see cref="Divide"/>) and REMOVES one (<see cref="Multiply"/>), an auto-rerun of the affected
/// subtree, and a cycle that arrives mid-run (the queued state). The SAME scripted identities feed both the
/// live demo (<see cref="FakeWatchAdapter"/> + the CLI driver) and the snapshot suite, so the snapshots are
/// the contract the runtime honours.
/// </summary>
public static class FakeWatchScript
{
    public const string AdapterKind = "fake";
    public const string Project = "watch/Watch.Sample.Tests/Watch.Sample.Tests.csproj";
    public const string DisplayName = "Watch.Sample.Tests";
    public const string Tfm = "net10.0";
    private const string Ns = "Watch.Sample";
    private const string Cls = "CalculatorTests";

    private static TestIdentity Make(string method)
    {
        var id = TestCaseId.ForCase(AdapterKind, Project, Tfm, $"{Ns}.{Cls}.{method}", null);
        return new TestIdentity(id, Project, Tfm, Ns, Cls, method);
    }

    public static readonly TestIdentity Add = Make("Add_ReturnsSum");
    public static readonly TestIdentity Subtract = Make("Subtract_ReturnsDifference");
    public static readonly TestIdentity Multiply = Make("Multiply_ReturnsProduct");   // removed by the diff
    public static readonly TestIdentity Divide = Make("Divide_ReturnsQuotient");      // added by the diff

    /// <summary>The tests discovered at startup.</summary>
    public static readonly IReadOnlyList<TestIdentity> Initial = [Add, Subtract, Multiply];

    /// <summary>The tests after the re-discovery diff (−Multiply, +Divide).</summary>
    public static readonly IReadOnlyList<TestIdentity> AfterDiff = [Add, Subtract, Divide];

    private static readonly IReadOnlyList<TestIdentity> All = [Add, Subtract, Multiply, Divide];

    /// <summary>The <see cref="AppEvent.ProjectRegistered"/> that establishes the single-TFM project node.</summary>
    public static AppEvent ProjectRegistered =>
        new AppEvent.ProjectRegistered(Project, DisplayName, [Tfm], RunnerKind.VsTest);

    /// <summary>Resolve a derived id back to its identity (for driving runs from a subset).</summary>
    public static TestIdentity? Resolve(TestCaseId id) => All.FirstOrDefault(i => i.Id.Equals(id));

    /// <summary>Deterministic outcome for a test: <see cref="Subtract"/> fails (so the tree shows a red node
    /// and the detail pane has content); everything else passes.</summary>
    public static (TestOutcome Outcome, TestResultDetail? Detail) Outcome(TestIdentity id) =>
        id.Method == Subtract.Method
            ? (TestOutcome.Failed, FakeFailures.Generic(Ns, Cls, id.Method))
            : (TestOutcome.Passed, null);
}
