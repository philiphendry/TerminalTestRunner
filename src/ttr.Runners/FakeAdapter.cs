using System.Threading.Channels;
using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// The Fake adapter (plan §6.5) — a first-class <see cref="ITestSessionAdapter"/> with zero
/// external dependencies. It streams the same event shapes as the real adapters (staggered
/// discovery batches, then interleaved started/finished, incl. theory rows that surface only
/// via run events) so the entire UI is developed and reviewed against it before any real
/// platform code exists.
/// </summary>
public sealed class FakeAdapter : ITestSessionAdapter
{
    private readonly FakeScenario _scenario;

    public FakeAdapter(string scenarioName, int seed)
        => _scenario = ScenarioBuilder.Build(scenarioName, seed);

    /// <summary>The built scenario (exposed for tests and the CLI header).</summary>
    public FakeScenario Scenario => _scenario;

    public async Task DiscoverAsync(TestTarget target, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var p = _scenario.Pacing;
        var batch = new List<TestIdentity>(p.DiscoveryBatchSize);
        foreach (var plan in _scenario.Plans)
        {
            if (!plan.DiscoverUpfront) continue;   // theory rows are revealed only during the run
            batch.Add(plan.Identity);
            if (batch.Count >= p.DiscoveryBatchSize)
            {
                events.TryWrite(new AppEvent.TestsDiscovered(batch));
                batch = new List<TestIdentity>(p.DiscoveryBatchSize);
                await Task.Delay(p.DiscoveryBatchDelayMs, ct).ConfigureAwait(false);
            }
        }
        if (batch.Count > 0)
            events.TryWrite(new AppEvent.TestsDiscovered(batch));
    }

    public async Task RunAsync(
        TestTarget target, IReadOnlyList<TestCaseId> subset, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var p = _scenario.Pacing;
        var selected = subset.Count == 0
            ? _scenario.Plans
            : _scenario.Plans.Where(pl => subset.Contains(pl.Identity.Id)).ToList();

        using var gate = new SemaphoreSlim(Math.Max(1, p.MaxConcurrency));
        var running = new List<Task>(selected.Count);

        try
        {
            foreach (var plan in selected)
            {
                if (p.StartIntervalMs > 0)
                    await Task.Delay(p.StartIntervalMs, ct).ConfigureAwait(false);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                running.Add(RunOne(plan, gate, events, ct));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
            events.TryWrite(new AppEvent.RunCompleted());
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested: stop launching. In-flight tasks observe ct and unwind.
            await Task.WhenAll(running.Where(t => !t.IsCompleted)).ConfigureAwait(false);
        }
    }

    private static async Task RunOne(
        FakePlan plan, SemaphoreSlim gate, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        try
        {
            events.TryWrite(new AppEvent.TestStarted(plan.Identity));
            await Task.Delay(plan.Duration, ct).ConfigureAwait(false);
            events.TryWrite(new AppEvent.TestFinished(plan.Identity, plan.Outcome, plan.Duration));
        }
        catch (OperationCanceledException)
        {
            // Left Running; the reducer's RunCompleted sweep (or shutdown) handles it.
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
