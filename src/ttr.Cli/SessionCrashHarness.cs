using Ttr.Core;
using Ttr.Core.Persistence;

namespace Ttr.Cli;

/// <summary>
/// A test-only, env-gated crash-safety harness (brief M3/M6, POC-8's kill -9 port). When
/// <c>TTR_CRASH_SAVE_DIR</c> is set, ttr does nothing but generate a large session and <see cref="SessionStore.Save"/>
/// it in a tight loop forever, printing <c>ready</c> once, so a parent test can SIGKILL it mid-write and assert
/// the atomic temp-file + rename guarantee held (state.json is always the old or new file, never torn). References
/// no <c>Microsoft.Build</c> types, so it is safe to invoke before <c>MSBuildLocator</c> in Program.cs.
/// </summary>
internal static class SessionCrashHarness
{
    /// <summary>Returns true (and never returns from within, until killed) when the harness env var is set.</summary>
    public static bool TryRun()
    {
        var dir = Environment.GetEnvironmentVariable("TTR_CRASH_SAVE_DIR");
        if (string.IsNullOrEmpty(dir)) return false;

        var count = int.TryParse(Environment.GetEnvironmentVariable("TTR_CRASH_SAVE_COUNT"), out var c) ? c : 10_000;
        var fingerprint = new List<SessionDocument.TargetEntry>
        {
            new() { Path = "harness", Hash = "sha256:0" },
        };
        var store = new SessionStore(dir, fingerprint);
        var snapshot = Generate(count);

        // One completed save first, so a subsequent kill always lands while OVERWRITING an existing state.json —
        // the true torn-file test (temp-file + rename must leave the old or new file, never a partial one).
        store.Save(snapshot);
        Console.WriteLine("ready");
        Console.Out.Flush();
        while (true) store.Save(snapshot);
    }

    /// <summary>Generate a deterministic <paramref name="count"/>-test session with ~10% failures carrying detail
    /// (roughly POC-8's 10k shape) so the save/footprint numbers are representative.</summary>
    public static SessionSnapshot Generate(int count)
    {
        const string project = "Bench.Tests.csproj";
        const string tfm = "net10.0";
        var tests = new List<SessionTest>(count);
        var expanded = new List<string> { TestCaseId.ForBranch(TestNodeKind.Solution, "root").Value };
        for (var i = 0; i < count; i++)
        {
            var fqn = $"Ns{i % 40}.Class{i % 200}.Test_{i}";
            var id = TestCaseId.ForCase("fake", project, tfm, fqn, null);
            var failed = i % 10 == 0;
            var detail = failed
                ? new TestResultDetail(
                    Message: $"Assert.Equal() Failure at case {i}: Expected 42, Actual {i}",
                    ExceptionChain: ["Xunit.Sdk.EqualException"],
                    StackTrace: $"   at {fqn}() in /repo/src/Bench/Class{i % 200}.cs:line {i % 500 + 1}")
                : null;
            tests.Add(new SessionTest(id, failed ? TestStatus.Failed : TestStatus.Passed,
                TimeSpan.FromMilliseconds(i % 250), detail, RestoredDetailOnDisk: false));
        }
        var ui = new RestoredUi(false, false, false, false, false, expanded,
            tests.Count > 0 ? tests[0].Id.Value : null, 0, 0);
        return new SessionSnapshot(tests, ui);
    }
}
