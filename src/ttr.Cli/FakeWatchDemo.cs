using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>
/// Scripts the <c>--fake --watch</c> demo (brief M1) on a timer: an initial discover+run, then repeating
/// cycles that show every watch state — change detected → build spinner → re-discovery diff (+Divide/−Multiply,
/// then the reverse) → auto-rerun — plus a cycle that arrives mid-run to surface the queued state. Reruns go
/// through the real orchestrator → <see cref="FakeWatchAdapter"/> path (this driver only emits the cycle
/// scaffolding events), so the demo is watch mode exercised end-to-end, not painted.
/// </summary>
internal static class FakeWatchDemo
{
    public static async Task RunAsync(
        FakeWatchAdapter adapter, TestTarget target, ChannelWriter<AppEvent> events,
        Func<AppState> state, CancellationToken ct)
    {
        await adapter.DiscoverAsync(target, events, ct).ConfigureAwait(false);
        await Pause(600, ct);
        await RerunAndWait(events, state, ct);          // initial run populates the tree

        var toDiff = true;   // toggle the tree between the diffed set and the original each cycle
        while (!ct.IsCancellationRequested)
        {
            await Pause(1500, ct);                        // watch: idle
            await DiffCycle(events, state, toDiff ? FakeWatchScript.AfterDiff : FakeWatchScript.Initial, ct);
            toDiff = !toDiff;

            await Pause(1500, ct);
            await MidRunQueuedCycle(events, state, toDiff ? FakeWatchScript.AfterDiff : FakeWatchScript.Initial, ct);
            toDiff = !toDiff;
        }
    }

    /// <summary>A normal cycle: change → build spinner → re-discovery diff → auto-rerun.</summary>
    private static async Task DiffCycle(
        ChannelWriter<AppEvent> events, Func<AppState> state, IReadOnlyList<TestIdentity> newSet, CancellationToken ct)
    {
        events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected));
        await Pause(500, ct);
        await Build(events, ct);
        Rediscover(events, newSet);
        await RerunAndWait(events, state, ct);
        events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Idle));
    }

    /// <summary>A cycle whose change lands while the previous run is still going — the queued state — then the
    /// one consolidated follow-up cycle executes once the run finishes (AC6).</summary>
    private static async Task MidRunQueuedCycle(
        ChannelWriter<AppEvent> events, Func<AppState> state, IReadOnlyList<TestIdentity> newSet, CancellationToken ct)
    {
        // Kick off a run, then let a change arrive mid-flight.
        events.TryWrite(new AppEvent.WatchRerunRequested([FakeWatchScript.Project]));
        await WaitUntil(state, s => s.Running, ct);
        events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Queued));
        await WaitUntil(state, s => !s.Running, ct);

        // The consolidated follow-up cycle.
        events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected));
        await Build(events, ct);
        Rediscover(events, newSet);
        await RerunAndWait(events, state, ct);
        events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Idle));
    }

    private static async Task Build(ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        events.TryWrite(new AppEvent.BuildStarted(FakeWatchScript.Project));
        await Pause(700, ct);
        events.TryWrite(new AppEvent.BuildSucceeded(FakeWatchScript.Project));
    }

    private static void Rediscover(ChannelWriter<AppEvent> events, IReadOnlyList<TestIdentity> newSet)
    {
        events.TryWrite(new AppEvent.RediscoveryStarted([FakeWatchScript.Project]));
        events.TryWrite(new AppEvent.TestsDiscovered(newSet));
        events.TryWrite(new AppEvent.RediscoveryCompleted([FakeWatchScript.Project]));
    }

    private static async Task RerunAndWait(ChannelWriter<AppEvent> events, Func<AppState> state, CancellationToken ct)
    {
        events.TryWrite(new AppEvent.WatchRerunRequested([FakeWatchScript.Project]));
        await WaitUntil(state, s => s.Running, ct);
        await WaitUntil(state, s => !s.Running, ct);
    }

    private static async Task WaitUntil(Func<AppState> state, Func<AppState, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 500 && !ct.IsCancellationRequested; i++)   // ~10s cap so the demo can never wedge
        {
            if (predicate(state())) return;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private static Task Pause(int ms, CancellationToken ct) => Task.Delay(ms, ct);
}
