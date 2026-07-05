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

    // Rerun bookkeeping (brief M1/M4): the first RunAsync is the initial run (planned outcomes);
    // later calls are reruns, where a currently-failed test flips to Passed with the scenario's
    // RerunPassProbability. Seeded for determinism; mutated only on the single launch loop.
    private readonly Random _rerunRng;
    private readonly Dictionary<TestCaseId, TestOutcome> _outcomes = new();
    private int _runCount;

    public FakeAdapter(string scenarioName, int seed)
    {
        _scenario = ScenarioBuilder.Build(scenarioName, seed);
        _rerunRng = new Random(seed);
        foreach (var plan in _scenario.Plans)
            _outcomes[plan.Identity.Id] = plan.Outcome;
    }

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
        var isRerun = Interlocked.Increment(ref _runCount) > 1;
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
                running.Add(RunOne(plan, EffectiveOutcome(plan, isRerun), gate, events, ct));
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

    /// <summary>Outcome for this run: planned on the initial run; on a rerun, a failed test flips to
    /// Passed with the scenario's <see cref="FakeScenario.RerunPassProbability"/> (the vanish-on-pass loop).</summary>
    private TestOutcome EffectiveOutcome(FakePlan plan, bool isRerun)
    {
        if (!isRerun) return plan.Outcome;
        var current = _outcomes.GetValueOrDefault(plan.Identity.Id, plan.Outcome);
        if (current == TestOutcome.Failed
            && _scenario.RerunPassProbability > 0
            && _rerunRng.NextDouble() < _scenario.RerunPassProbability)
        {
            current = TestOutcome.Passed;
        }
        _outcomes[plan.Identity.Id] = current;
        return current;
    }

    private static async Task RunOne(
        FakePlan plan, TestOutcome outcome, SemaphoreSlim gate,
        ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        try
        {
            events.TryWrite(new AppEvent.TestStarted(plan.Identity));
            await Task.Delay(plan.Duration, ct).ConfigureAwait(false);
            var detail = outcome == TestOutcome.Failed ? plan.Detail : null;
            events.TryWrite(new AppEvent.TestFinished(plan.Identity, outcome, plan.Duration, detail));
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
