using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>
/// Scripts the <c>--fake --continue</c> demo (brief M1): discovery populates the tree, a scripted
/// <see cref="AppEvent.SessionRestored"/> attaches restored pass/fail results (a couple already Stale) and
/// pre-applies the UI (detail pane open, durations on, the failure selected), the failure's detail arrives via
/// a lazy <see cref="AppEvent.DetailLoaded"/>, then after a pause a simulated rebuild (re-discovery cycle)
/// flips the remaining fresh results to Stale — exactly the states the M1 snapshots pin. No disk is touched.
/// </summary>
internal static class FakeContinueDemo
{
    public static async Task RunAsync(ChannelWriter<AppEvent> events, Func<AppState> state, CancellationToken ct)
    {
        events.TryWrite(FakeContinueScript.ProjectRegistered);
        events.TryWrite(new AppEvent.TestsDiscovered(FakeContinueScript.Initial));
        events.TryWrite(new AppEvent.SessionRestored(
            FakeContinueScript.RestoredResults, FakeContinueScript.RestoredUiState, FakeContinueScript.RelativeTime));
        // The failure's detail loads on demand (it was selected by the restore) — simulate the lazy fetch.
        events.TryWrite(new AppEvent.DetailLoaded(FakeContinueScript.Subtract.Id, FakeContinueScript.SubtractDetail));

        await Pause(2500, ct);

        // A simulated rebuild: re-discover the same set, which marks every kept result Stale (the watch-rebuild
        // path) — the whole subtree dims until a rerun would replace it.
        events.TryWrite(new AppEvent.RediscoveryStarted([FakeContinueScript.Project]));
        events.TryWrite(new AppEvent.TestsDiscovered(FakeContinueScript.Initial));
        events.TryWrite(new AppEvent.RediscoveryCompleted([FakeContinueScript.Project]));

        // Idle — the demo just holds the restored+stale view until the user quits.
        while (!ct.IsCancellationRequested)
            await Pause(1000, ct);
    }

    private static Task Pause(int ms, CancellationToken ct) => Task.Delay(ms, ct);
}
