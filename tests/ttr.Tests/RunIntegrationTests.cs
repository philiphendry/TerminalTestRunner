using System.Diagnostics;
using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// Real end-to-end EXECUTION against the fixture matrix (brief M2/M3, AC1–AC4): drive the actual VSTest /
/// MTP adapters at built fixture assemblies, stream discovery + run events through the reducer, and assert
/// the resulting tree (pass/fail/skip totals, theory rows, cancellation → no phantom spinners, MTP host
/// reuse across runs). Like the discovery integration tests, these Skip when a fixture assembly (or
/// vstest.console) is absent so the pure suite stays green where the matrix isn't built.
/// </summary>
[Collection("integration")]
public class RunIntegrationTests
{
    private static string? RepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\')))
            if (File.Exists(Path.Combine(dir, "ttr.sln"))) return dir;
        return null;
    }

    private static string? FixtureAssembly(string project)
    {
        var root = RepoRoot();
        if (root is null) return null;
        var dll = Path.Combine(root, "fixtures", "matrix", project, "bin", "Debug", "net10.0", $"{project}.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static string FixtureProject(string project) =>
        Path.Combine(RepoRoot()!, "fixtures", "matrix", project, $"{project}.csproj");

    /// <summary>Apply an event-producing action's stream to a state through the reducer.</summary>
    private static async Task<AppState> Drain(AppState s, Func<ChannelWriter<AppEvent>, Task> produce)
    {
        var channel = Channel.CreateUnbounded<AppEvent>();
        await produce(channel.Writer);
        channel.Writer.Complete();
        await foreach (var e in channel.Reader.ReadAllAsync())
            s = Reducer.Reduce(s, e);
        return s;
    }

    private static TestNode? Find(TestNode node, Func<TestNode, bool> match)
    {
        if (match(node)) return node;
        foreach (var c in node.Children)
            if (Find(c, match) is { } found) return found;
        return null;
    }

    // --- VSTest execution (AC1, AC4) -------------------------------------------

    [Fact]
    public async Task XunitV2_vstest_runs_all_with_correct_totals()
    {
        var dll = FixtureAssembly("XunitV2.Tests");
        var console = SdkTools.FindVsTestConsole();
        if (dll is null || console is null) return;   // skip: fixture / vstest.console not built

        var proj = FixtureProject("XunitV2.Tests");
        await using var d = new VsTestDiscoverer(console!);
        var s = AppState.Initial("run", runsEnabled: true);

        s = await Drain(s, async writer =>
        {
            writer.TryWrite(new AppEvent.ProjectRegistered(proj, "XunitV2.Tests", ["net10.0"], RunnerKind.VsTest));
            await d.DiscoverAsync([new VsTestSource(proj, "net10.0", dll!)], writer, null, default);
        });
        Assert.Equal(8, s.TotalTests);   // Add_MemberData is one placeholder leaf at discovery

        s = await Drain(s, writer => d.RunAsync([], writer, default));

        // 6 pass (Add, Divide, Add_Theory(1,1,2), Add_Theory(2,3,5), Add_MemberData ×2);
        // 2 fail (Failing_Assertion, Add_Theory(10,22,33)); 1 skip (Skipped_Test).
        Assert.Equal(6, s.Passed);
        Assert.Equal(2, s.Failed);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(0, s.RunningCount);   // no phantom spinners after RunCompleted

        // AC4: the non-serialisable theory discovered as ONE placeholder case is PROMOTED to a branch and
        // materialises its N rows as Case children during the run (the live 1→N shape). The Method leaf
        // stops counting as its own test, so the total goes 8 → 9 (one placeholder → two rows), not 10.
        var memberData = Find(s.Root, n => n.Name == "Add_MemberData");
        Assert.NotNull(memberData);
        Assert.False(memberData!.IsLeaf);
        Assert.Equal(2, memberData.Children.Count);
        Assert.Equal(9, s.TotalTests);

        // The failing plain test carries parsed detail + a file reference (the 'o' target).
        var failing = Find(s.Root, n => n.Name == "Failing_Assertion");
        Assert.NotNull(failing);
        Assert.NotNull(failing!.Detail);
        Assert.Contains(failing.FileRefs, r => r.FileName == "CalcTests.cs");
    }

    [Fact]
    public async Task XunitV2_vstest_runs_subset_only()
    {
        var dll = FixtureAssembly("XunitV2.Tests");
        var console = SdkTools.FindVsTestConsole();
        if (dll is null || console is null) return;

        var proj = FixtureProject("XunitV2.Tests");
        await using var d = new VsTestDiscoverer(console!);
        var s = AppState.Initial("run", runsEnabled: true);
        s = await Drain(s, w => d.DiscoverAsync([new VsTestSource(proj, "net10.0", dll!)], w, null, default));

        // Subset = just the two plain passing/failing facts (exclude the theory + skip).
        var subset = d.Cache.Keys.Where(id => id.Value.Contains("Add_ReturnsSum") || id.Value.Contains("Failing_Assertion")).ToList();
        Assert.Equal(2, subset.Count);
        s = await Drain(s, w => d.RunAsync(subset, w, default));

        Assert.Equal(1, s.Passed);
        Assert.Equal(1, s.Failed);
        // Everything not in the subset stays NotRun.
        Assert.Equal(s.TotalTests - 2, s.NotRunCount);
    }

    // --- MTP execution (AC1, cancellation + reuse AC3) -------------------------

    [Fact]
    public async Task XunitV3_mtp_runs_and_reuses_host_across_runs()
    {
        var dll = FixtureAssembly("XunitV3.Tests");
        if (dll is null) return;   // skip: fixture not built

        var proj = FixtureProject("XunitV3.Tests");
        var before = ProcessSnapshot();
        var session = new MtpSession(proj, dll!, "net10.0", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var s = AppState.Initial("run", runsEnabled: true);

            Assert.True(await session.StartAsync(Channel.CreateUnbounded<AppEvent>().Writer, cts.Token));
            s = await Drain(s, w => session.DiscoverAsync(w, cts.Token));

            // Theory rows are enumerated at discovery and rendered as distinct Case leaves (never collapsed).
            var even = Find(s.Root, n => n.Name == "Even");
            Assert.NotNull(even);
            Assert.Equal(2, even!.Children.Count);           // Even(n: 2), Even(n: 4)
            var discovered = s.TotalTests;                    // Get, Post, Failing + 2 theory rows = 5
            Assert.Equal(5, discovered);

            // First run: every discovered test reaches a terminal state (Get, Post, Even×2 pass; Failing fails).
            s = await Drain(s, w => session.RunAsync([], w, cts.Token));
            Assert.Equal(4, s.Passed);                        // Get, Post, Even(2), Even(4)
            Assert.Equal(1, s.Failed);                        // Failing
            Assert.Equal(0, s.RunningCount);

            // Second run on the SAME session proves host reuse: it services the follow-up and every test
            // reaches terminal again (exact per-run counts are pinned by the cancellation test below; here
            // we assert reuse works and produces a full, spinner-free result set).
            s = await Drain(s, w => session.RunAsync([], w, cts.Token));
            Assert.Equal(1, s.Failed);
            Assert.Equal(0, s.RunningCount);
            Assert.Equal(discovered, s.Passed + s.Failed);    // all 5 reached a terminal state
        }
        finally { await session.DisposeAsync(); }

        await Task.Delay(1000);
        Assert.True(ProcessSnapshot() <= before, "an MTP test host was left running after the session was disposed");
    }

    [Fact]
    public async Task XunitV3_mtp_cancellation_is_prompt_and_host_survives()
    {
        var dll = FixtureAssembly("XunitV3.Tests");
        if (dll is null) return;

        var proj = FixtureProject("XunitV3.Tests");
        var session = new MtpSession(proj, dll!, "net10.0", null);
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Assert.True(await session.StartAsync(Channel.CreateUnbounded<AppEvent>().Writer, start.Token));
            await session.DiscoverAsync(Channel.CreateUnbounded<AppEvent>().Writer, start.Token);

            // Cancel almost immediately: $/cancelRequest goes out; the call returns promptly and the host
            // must survive to service the run below.
            using var cancel = new CancellationTokenSource();
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
            var sw = Stopwatch.StartNew();
            await session.RunAsync([], Channel.CreateUnbounded<AppEvent>().Writer, cancel.Token);
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"MTP cancellation took {sw.Elapsed.TotalSeconds:F1}s");

            // The host survived: a follow-up run completes with real results.
            var s = AppState.Initial("run", runsEnabled: true);
            using var again = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            s = await Drain(s, w => session.DiscoverAsync(w, again.Token));
            s = await Drain(s, w => session.RunAsync([], w, again.Token));
            Assert.Equal(4, s.Passed);
            Assert.Equal(1, s.Failed);
        }
        finally { await session.DisposeAsync(); }
    }

    private static int ProcessSnapshot()
    {
        try
        {
            return Process.GetProcesses()
                .Count(p => SafeName(p) is "testhost" or "vstest.console" or "dotnet-exec");
        }
        catch { return 0; }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return ""; }
    }
}
