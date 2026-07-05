using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// Fabricated <see cref="TestResultDetail"/> for failing fake tests (brief M1). Two flavours:
/// <list type="bullet">
/// <item><b>Generic</b> — realistic message/exception/stack/stdout whose stack frames reference
/// synthetic (non-existent) source paths, so 'o' reports "no file references". Used by
/// default/big/flaky/slow so every failure has a populated detail pane.</item>
/// <item><b>Files-scenario</b> — stack traces and messages that embed absolute paths into the
/// committed <c>fixtures/fake-sources/</c> files, with multiple refs per failure including a
/// non-existent frame (dim/unresolved) and an <c>obj/…​.g.cs</c> frame (filtered), so the 'o'
/// modal works end-to-end.</item>
/// </list>
/// </summary>
internal static class FakeFailures
{
    /// <summary>Absolute path to a committed fixture source, copied beside the assembly at build.</summary>
    public static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fake-sources", name);

    // --- Generic (paths do not exist → unresolved refs) ----------------------

    public static TestResultDetail Generic(string ns, string cls, string method)
    {
        var nsLeaf = ns.Split('.').Last();
        // Deterministic pseudo line from the names so the same test always renders the same detail.
        var line = 20 + (Math.Abs(HashString(method)) % 80);
        var stack =
            $"   at {ns}.{cls}.{method}() in /work/src/{nsLeaf}/{cls}.cs:line {line}\n" +
            $"   at {ns}.{cls}.<{method}>b__0() in /work/src/{nsLeaf}/{cls}.cs:line {line + 4}\n" +
            "   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** args, Signature sig)";
        return new TestResultDetail(
            Message: "Assert.Equal() Failure: Values differ\nExpected: 42\nActual:   41",
            ExceptionChain: ["Xunit.Sdk.EqualException"],
            StackTrace: stack,
            StandardOutput: $"[info] entering {method}\n[warn] recomputing intermediate result");
    }

    // --- Files scenario (paths point at real committed fixtures) --------------

    /// <summary>Assertion failure: two resolvable user frames + an obj/.g.cs frame (filtered) + a
    /// source-linked framework frame (non-existent → unresolved/dim).</summary>
    public static TestResultDetail AssertWithFrames()
    {
        var stack =
            $"   at Contoso.Sample.Calculations.Calculator.Add(Int32 a, Int32 b) in {Fixture("Calculator.cs")}:line 17\n" +
            $"   at Contoso.Sample.Calculations.StringUtilities.Format(String s) in {Fixture("StringUtilities.cs")}:line 12\n" +
            $"   at Contoso.Sample.Generated.Glue.Invoke() in {Fixture(Path.Combine("obj", "Debug", "net10.0", "Glue.g.cs"))}:line 42\n" +
            "   at Microsoft.Testing.Framework.Runner.Execute() in /_/src/Framework/Runner.cs:line 108";
        return new TestResultDetail(
            Message: "Assert.Equal() Failure: Values differ\nExpected: 42\nActual:   41",
            ExceptionChain: ["Xunit.Sdk.EqualException"],
            StackTrace: stack,
            StandardOutput: "[stdout] computing sum of 20 + 22\n[stdout] returned 41");
    }

    /// <summary>Message-embedded references (config.json + notes.txt) plus a stack frame in the long file.</summary>
    public static TestResultDetail MessageRefs()
    {
        var stack =
            $"   at Contoso.Sample.Networking.PaymentProcessor.Charge(CardDetails card, Decimal amount, String currency) in {Fixture("PaymentProcessor.cs")}:line 111\n" +
            "   at Contoso.Sample.Networking.PaymentProcessorTests.Charge_ValidCard()";
        return new TestResultDetail(
            Message:
                $"Expected file {Fixture("config.json")} to contain key \"network.timeout\"; " +
                $"see {Fixture("notes.txt")} for the configuration schema.",
            ExceptionChain: ["System.Collections.Generic.KeyNotFoundException"],
            StackTrace: stack,
            StandardOutput: "[stdout] loading config.json\n[stdout] key lookup failed");
    }

    /// <summary>Deep frame in the long file + a frame into the unrecognised-extension fixture (plain render).</summary>
    public static TestResultDetail DeepAndUnrecognised()
    {
        var stack =
            $"   at Contoso.Sample.Networking.PaymentProcessor.Settle(DateOnly asOf) in {Fixture("PaymentProcessor.cs")}:line 193\n" +
            $"   at Contoso.Sample.Reporting.TemplateEngine.Render(String template) in {Fixture("layout.tmpl")}:line 3\n" +
            "   at Contoso.Sample.Networking.SettlementTests.Settle_Empty_Throws()";
        return new TestResultDetail(
            Message: "PaymentException: nothing to settle",
            ExceptionChain: ["Contoso.Sample.Networking.PaymentException"],
            StackTrace: stack,
            StandardOutput: "[stdout] settling as of 2026-07-05\n[stdout] ledger empty");
    }

    private static int HashString(string s)
    {
        // Small, stable, culture-independent hash (avoids string.GetHashCode's per-run randomisation).
        var h = 17;
        foreach (var c in s) h = h * 31 + c;
        return h;
    }
}
