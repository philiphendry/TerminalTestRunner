using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// The deterministic <c>--fake --continue</c> simulation (brief M1). A small REGISTERED single-TFM project
/// whose tree arrives with RESTORED pass/fail results — a few already <c>Stale</c> — and pre-applied UI state
/// (detail pane open, durations on, the failed test selected), then a simulated rebuild flips the rest of a
/// subtree to <c>Stale</c>. The SAME scripted identities/results feed both the live demo
/// (<c>FakeContinueDemo</c>) and the M1 snapshot suite, so the snapshots are the contract the runtime honours.
/// </summary>
public static class FakeContinueScript
{
    public const string AdapterKind = "fake";
    public const string Project = "Sample.Tests/Sample.Tests.csproj";
    public const string DisplayName = "Sample.Tests";
    public const string Tfm = "net10.0";
    private const string Ns = "Sample";
    private const string Calc = "CalculatorTests";
    private const string Str = "StringTests";

    private static TestIdentity Make(string cls, string method)
    {
        var id = TestCaseId.ForCase(AdapterKind, Project, Tfm, $"{Ns}.{cls}.{method}", null);
        return new TestIdentity(id, Project, Tfm, Ns, cls, method);
    }

    public static readonly TestIdentity Add = Make(Calc, "Add_ReturnsSum");
    public static readonly TestIdentity Subtract = Make(Calc, "Subtract_ReturnsDifference");
    public static readonly TestIdentity Divide = Make(Calc, "Divide_ReturnsQuotient");
    public static readonly TestIdentity Trim = Make(Str, "Trim_RemovesWhitespace");
    public static readonly TestIdentity Upper = Make(Str, "Upper_IsSkipped");

    /// <summary>The tests discovered at startup (the current build's tree).</summary>
    public static readonly IReadOnlyList<TestIdentity> Initial = [Add, Subtract, Divide, Trim, Upper];

    /// <summary>The failure's rich detail — so the restored detail pane + 'o' have real content (fed as a lazy
    /// <see cref="AppEvent.DetailLoaded"/>, since the fake demo has no on-disk store).</summary>
    public static readonly TestResultDetail SubtractDetail = FakeFailures.Generic(Ns, Calc, Subtract.Method);

    /// <summary>The <see cref="AppEvent.ProjectRegistered"/> that establishes the single-TFM project node.</summary>
    public static AppEvent ProjectRegistered =>
        new AppEvent.ProjectRegistered(Project, DisplayName, [Tfm], RunnerKind.VsTest);

    /// <summary>The restored results: Calc mostly fresh (Add pass, Subtract fail w/ detail, Divide pass); the
    /// StringTests subtree already Stale on restore (its assembly changed since — Trim pass, Upper skip).</summary>
    public static IReadOnlyList<RestoredResult> RestoredResults =>
    [
        new(Add.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(4), HasDetail: false, Stale: false),
        new(Subtract.Id, TestStatus.Failed, TimeSpan.FromMilliseconds(9), HasDetail: true, Stale: false),
        new(Divide.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(3), HasDetail: false, Stale: false),
        new(Trim.Id, TestStatus.Passed, TimeSpan.FromMilliseconds(2), HasDetail: false, Stale: true),
        new(Upper.Id, TestStatus.Skipped, TimeSpan.FromMilliseconds(1), HasDetail: false, Stale: true),
    ];

    /// <summary>The pre-applied UI: detail pane open (right), durations on, the failed test selected.</summary>
    public static RestoredUi RestoredUiState => new(
        FailedOnly: false,
        ShowDurations: true,
        DetailVisible: true,
        DetailBottom: false,
        WordWrap: false,
        Expanded:
        [
            TestCaseId.ForBranch(TestNodeKind.Solution, "root").Value,
            TestCaseId.ForBranch(TestNodeKind.Project, Project).Value,
            TestCaseId.ForBranch(TestNodeKind.Namespace, Project, Tfm, Ns).Value,
            TestCaseId.ForBranch(TestNodeKind.Class, Project, Tfm, Ns, Calc).Value,
            TestCaseId.ForBranch(TestNodeKind.Class, Project, Tfm, Ns, Str).Value,
        ],
        Selected: Subtract.Id.Value,
        TreeScroll: 0,
        DetailScroll: 0);

    public const string RelativeTime = "3 min ago";
}
