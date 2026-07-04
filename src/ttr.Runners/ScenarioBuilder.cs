using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// Builds deterministic fake scenarios (plan §6.5). Everything — outcomes, durations, name
/// selection — is derived from the seed, so a given <c>--fake-seed</c> always yields the same
/// run. Timing is wall-clock (Task.Delay) but content is fixed.
/// </summary>
public static class ScenarioBuilder
{
    public const string AdapterKind = "fake";
    private const string Project = "Contoso.Sample.Tests";
    private const string Tfm = "net10.0";

    private static readonly string[] Namespaces =
    [
        "Contoso.Sample.Calculations", "Contoso.Sample.Networking", "Contoso.Sample.Storage",
        "Contoso.Sample.Parsing", "Contoso.Sample.Rendering", "Contoso.Sample.Security",
        "Contoso.Sample.Concurrency", "Contoso.Sample.Serialization", "Contoso.Sample.Caching",
        "Contoso.Sample.Diagnostics",
    ];

    private static readonly string[] ClassSuffixes =
    [
        "Tests", "SpecTests", "IntegrationTests", "EdgeCaseTests", "RegressionTests",
        "BehaviourTests", "BoundaryTests", "SmokeTests",
    ];

    private static readonly string[] Verbs =
    [
        "Returns", "Computes", "Handles", "Rejects", "Accepts", "Parses", "Serializes",
        "Validates", "Throws", "Retries", "Caches", "Normalizes", "Rounds", "Merges",
    ];

    private static readonly string[] Nouns =
    [
        "EmptyInput", "NullArgument", "LargePayload", "UnicodeText", "NegativeValue",
        "ConcurrentAccess", "TimeoutExpiry", "DuplicateKey", "MissingField", "BoundaryValue",
    ];

    // Names that exercise cell-aware truncation (CLAUDE.md invariant 5).
    private const string LongName =
        "Given_A_Very_Long_And_Detailed_Behavioural_Specification_Name_That_Exceeds_One_Hundred_" +
        "And_Twenty_Characters_To_Exercise_Ellipsis_Truncation_In_The_Virtualised_Tree_View";

    private static readonly string[] WideGlyphNames =
    [
        "計算機は二つの数値を正しく加算する",       // Japanese (CJK, 2 cells each)
        "验证输入边界条件的处理逻辑",              // Chinese
        "adds_two_numbers_🚀_and_returns_✅",     // emoji (2 cells)
        "한국어_테스트_케이스_경계값_검증",         // Korean
    ];

    public static FakeScenario Build(string name, int seed)
    {
        var rng = new Random(seed);
        return name switch
        {
            "big" => Grid(name, rng, nsCount: 10, classesPerNs: 20, methodsPerClass: 50,
                pacing: new FakePacing(200, 8, 48, 1), durationScale: 0.08, includeTheory: false),
            "slow" => Slow(rng),
            "flaky" => Grid(name, rng, nsCount: 3, classesPerNs: 4, methodsPerClass: 6,
                pacing: new FakePacing(20, 40, 6, 25), durationScale: 1.0, includeTheory: true,
                failChance: 0.30),
            "files" => throw new NotSupportedException(
                "The 'files' scenario is deferred to Phase 2 (needs the 'o' modal + fixture files)."),
            _ => Grid("default", rng, nsCount: 3, classesPerNs: 5, methodsPerClass: 20,
                pacing: new FakePacing(25, 35, 8, 6), durationScale: 1.0, includeTheory: true),
        };
    }

    /// <summary>Is <paramref name="name"/> a scenario ttr can run this phase?</summary>
    public static bool IsKnown(string name) =>
        name is "default" or "big" or "flaky" or "slow";

    private static FakeScenario Grid(
        string name, Random rng, int nsCount, int classesPerNs, int methodsPerClass,
        FakePacing pacing, double durationScale, bool includeTheory, double failChance = 0.12)
    {
        var target = nsCount * classesPerNs * methodsPerClass;
        var plans = new List<FakePlan>(target + 8);
        var made = 0;

        for (var n = 0; n < nsCount && made < target; n++)
        {
            var ns = Namespaces[n % Namespaces.Length];
            for (var c = 0; c < classesPerNs && made < target; c++)
            {
                var cls = $"{Nouns[c % Nouns.Length]}{ClassSuffixes[c % ClassSuffixes.Length]}";
                for (var m = 0; m < methodsPerClass && made < target; m++)
                {
                    var method = MethodName(rng, n, c, m);
                    plans.Add(MakePlan(ns, cls, method, caseDisplay: null, rng, failChance, durationScale, true));
                    made++;
                }
            }
        }

        if (includeTheory)
            AddMidRunTheory(plans, rng, Namespaces[0], "BoundaryValueTests", failChance, durationScale);

        return new FakeScenario(name, plans, pacing);
    }

    private static FakeScenario Slow(Random rng)
    {
        // A handful of genuinely long-running tests (5–15 s) plus a couple of quick ones, all
        // launched near-simultaneously so their spinners run concurrently while nav stays live.
        var plans = new List<FakePlan>();
        var ns = "Contoso.Sample.Concurrency";
        var cls = "LongRunningTests";
        for (var i = 0; i < 6; i++)
        {
            var seconds = 5 + rng.Next(0, 11); // 5..15
            var outcome = rng.NextDouble() < 0.2 ? TestOutcome.Failed : TestOutcome.Passed;
            var id = TestCaseId.ForCase(AdapterKind, Project, Tfm, $"{ns}.{cls}.SlowOperation_{i}", null);
            plans.Add(new FakePlan(
                new TestIdentity(id, Project, Tfm, ns, cls, $"SlowOperation_TakesAbout_{seconds}s_{i}"),
                outcome, TimeSpan.FromSeconds(seconds), DiscoverUpfront: true));
        }
        for (var i = 0; i < 4; i++)
            plans.Add(MakePlan(ns, "QuickTests", $"FastCheck_{i}", null, rng, 0.1, 1.0, true));

        AddMidRunTheory(plans, rng, ns, "ParameterisedTests", 0.15, 1.0);

        // Launch all at once (StartIntervalMs 0) with room for every slow test to run in parallel.
        return new FakeScenario("slow", plans, new FakePacing(10, 30, 32, 0));
    }

    /// <summary>A theory whose Case children are announced ONLY via run events (the 1→N shape).</summary>
    private static void AddMidRunTheory(
        List<FakePlan> plans, Random rng, string ns, string cls, double failChance, double durationScale)
    {
        const string method = "ParsesValue_Theory";
        var rows = new[] { "value: 0", "value: 1", "value: -1", "value: int.MaxValue", "value: \"café\"" };
        foreach (var row in rows)
        {
            var id = TestCaseId.ForCase(AdapterKind, Project, Tfm, $"{ns}.{cls}.{method}", row);
            var outcome = Roll(rng, failChance);
            var ms = 20 + rng.Next(0, 180);
            plans.Add(new FakePlan(
                new TestIdentity(id, Project, Tfm, ns, cls, method, CaseDisplay: row),
                outcome, Scale(ms, durationScale), DiscoverUpfront: false)); // <-- appears mid-run only
        }
    }

    private static FakePlan MakePlan(
        string ns, string cls, string method, string? caseDisplay,
        Random rng, double failChance, double durationScale, bool discover)
    {
        var fqn = $"{ns}.{cls}.{method}";
        var id = TestCaseId.ForCase(AdapterKind, Project, Tfm, fqn, caseDisplay);
        var outcome = Roll(rng, failChance);
        var ms = 5 + rng.Next(0, 220);
        return new FakePlan(
            new TestIdentity(id, Project, Tfm, ns, cls, method, caseDisplay),
            outcome, Scale(ms, durationScale), discover);
    }

    private static string MethodName(Random rng, int n, int c, int m)
    {
        // Sprinkle in the pathological names so every default/big run has them somewhere.
        if (n == 0 && c == 0 && m == 1) return LongName;
        if (n == 0 && c == 1 && m < WideGlyphNames.Length) return WideGlyphNames[m];
        return $"{Verbs[rng.Next(Verbs.Length)]}_{Nouns[rng.Next(Nouns.Length)]}_{m}";
    }

    private static TestOutcome Roll(Random rng, double failChance)
    {
        var r = rng.NextDouble();
        if (r < failChance) return TestOutcome.Failed;
        if (r < failChance + 0.06) return TestOutcome.Skipped;
        return TestOutcome.Passed;
    }

    private static TimeSpan Scale(int ms, double scale) =>
        TimeSpan.FromMilliseconds(Math.Max(1, ms * scale));
}
