using System.Diagnostics;
using Ttr.Cli;
using Ttr.Core;
using Ttr.Core.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Ttr.Tests;

/// <summary>
/// The POC-8 measured bars on a generated 10k-test session (brief M6), CI-asserted with headroom and reported
/// via the test output: state.json save &lt; 200 ms, restore index &lt; 500 ms, lazy detail &lt; 20 ms,
/// footprint &lt; 20 MB, and save-jitter p95 ≤ 10 ms on a 30/s loop while a full save runs off-thread. Numbers
/// are echoed to the log so the PR can quote the actuals (they vary by host; the assertions are the bars).
/// </summary>
[Collection("integration")]   // serialised with the other timing-sensitive/heavy suites (see WatchIntegrationTests)
public sealed class SessionPerfTests : IDisposable
{
    private const int N = 10_000;
    private readonly string _root;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public SessionPerfTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "ttr-perf-" + Guid.NewGuid().ToString("n"));
        _stateDir = Path.Combine(_root, ".ttr");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SessionStore Store() => new(_stateDir, [new SessionDocument.TargetEntry { Path = "x", Hash = "sha256:0" }]);

    [Fact]
    public void Save_10k_under_200ms()
    {
        var store = Store();
        var snapshot = SessionCrashHarness.Generate(N);
        store.Save(snapshot);   // warm (JIT + dir creation)

        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            store.Save(snapshot);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var median = times[times.Count / 2];
        _out.WriteLine($"save 10k median = {median:0.0} ms (all: {string.Join(", ", times.Select(t => $"{t:0}"))})");
        Assert.True(median < 200, $"save median {median:0.0} ms exceeded the 200 ms bar");
    }

    [Fact]
    public void Restore_index_under_500ms()
    {
        Store().Save(SessionCrashHarness.Generate(N));
        var sw = Stopwatch.StartNew();
        var outcome = Store().Restore();
        sw.Stop();
        _out.WriteLine($"restore 10k = {sw.Elapsed.TotalMilliseconds:0.0} ms ({outcome.Session!.Tests.Count} tests)");
        Assert.Equal(RestoreStatus.Restored, outcome.Status);
        Assert.True(sw.Elapsed.TotalMilliseconds < 500, $"restore {sw.Elapsed.TotalMilliseconds:0.0} ms exceeded the 500 ms bar");
    }

    [Fact]
    public void Lazy_detail_under_20ms()
    {
        Store().Save(SessionCrashHarness.Generate(N));
        var store = Store();
        store.Restore();
        // A failing test carries detail (every 10th in the generator). Time a single cold fetch (idx parse
        // included) — POC-8 bar is < 20 ms and "do not preload".
        var id = TestCaseId.ForCase("fake", "Bench.Tests.csproj", "net10.0", "Ns0.Class0.Test_0", null);
        var sw = Stopwatch.StartNew();
        var detail = store.ReadDetail(id);
        sw.Stop();
        _out.WriteLine($"lazy detail fetch = {sw.Elapsed.TotalMilliseconds:0.00} ms");
        Assert.NotNull(detail);
        Assert.True(sw.Elapsed.TotalMilliseconds < 20, $"lazy fetch {sw.Elapsed.TotalMilliseconds:0.00} ms exceeded the 20 ms bar");
    }

    [Fact]
    public void Footprint_under_20MB()
    {
        Store().Save(SessionCrashHarness.Generate(N));
        long bytes = Directory.EnumerateFiles(_stateDir, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var mb = bytes / (1024.0 * 1024.0);
        _out.WriteLine($"footprint = {mb:0.00} MB");
        Assert.True(mb < 20, $"footprint {mb:0.00} MB exceeded the 20 MB budget");
    }

    [Fact]
    public async Task Save_does_not_jitter_a_30fps_loop()
    {
        // Build a 10k tree so the reducer-thread snapshot capture cost is included in the loop; run a full
        // save off-thread (as the orchestrator does) and measure the foreground 30/s tick disturbance.
        var tree = BuildTree(N);
        var snapshot = SessionCrashHarness.Generate(N);
        var store = Store();
        store.Save(snapshot);   // warm

        using var cts = new CancellationTokenSource();
        var saver = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested) store.Save(snapshot);
        });

        const int frameMs = 33;   // ~30 fps
        var disturbances = new List<double>();
        var sw = Stopwatch.StartNew();
        var nextTick = sw.Elapsed.TotalMilliseconds + frameMs;
        for (var i = 0; i < 90; i++)   // ~3 s of ticks
        {
            SessionSnapshot.Capture(tree);   // the only on-thread save cost (the render/reduce loop keeps working)
            while (sw.Elapsed.TotalMilliseconds < nextTick) Thread.SpinWait(50);
            var actual = sw.Elapsed.TotalMilliseconds;
            disturbances.Add(Math.Max(0, actual - nextTick));
            nextTick += frameMs;
        }
        cts.Cancel();
        try { await saver; } catch { /* best-effort */ }

        disturbances.Sort();
        var p95 = disturbances[(int)(disturbances.Count * 0.95)];
        _out.WriteLine($"30fps loop jitter p95 = {p95:0.00} ms during continuous 10k saves");
        Assert.True(p95 <= 10, $"jitter p95 {p95:0.00} ms exceeded the 10 ms bar");
    }

    private static AppState BuildTree(int count)
    {
        var ids = new TestIdentity[count];
        for (var i = 0; i < count; i++)
        {
            var id = TestCaseId.ForCase("fake", "Bench.Tests.csproj", "net10.0", $"Ns{i % 40}.Class{i % 200}.Test_{i}", null);
            ids[i] = new TestIdentity(id, "Bench.Tests.csproj", "net10.0", $"Ns{i % 40}", $"Class{i % 200}", $"Test_{i}");
        }
        return Reducer.Reduce(AppState.Initial("bench"), new AppEvent.TestsDiscovered(ids));
    }
}
