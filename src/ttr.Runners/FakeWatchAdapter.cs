using System.Threading.Channels;
using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// The run adapter behind <c>--fake --watch</c> (brief M1). It discovers the initial tree and executes any
/// subset the auto-rerun requests — resolving each derived id back through <see cref="FakeWatchScript"/> —
/// so a rerun of the diffed set (including the newly-added <see cref="FakeWatchScript.Divide"/>) runs
/// correctly. The CLI driver scripts the cycle events (change/build/re-discovery diff); the actual run goes
/// through the normal orchestrator → adapter path, so watch mode is exercised end-to-end, not faked around.
/// </summary>
public sealed class FakeWatchAdapter : ITestSessionAdapter
{
    private readonly int _perTestMs;

    public FakeWatchAdapter(int perTestMs = 220) => _perTestMs = perTestMs;

    public Task DiscoverAsync(TestTarget target, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        events.TryWrite(FakeWatchScript.ProjectRegistered);
        events.TryWrite(new AppEvent.TestsDiscovered(FakeWatchScript.Initial));
        return Task.CompletedTask;
    }

    public async Task RunAsync(
        TestTarget target, IReadOnlyList<TestCaseId> subset, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var ids = subset.Count == 0
            ? FakeWatchScript.AfterDiff.Select(i => i.Id).ToList()
            : subset;
        try
        {
            foreach (var id in ids)
            {
                var identity = FakeWatchScript.Resolve(id);
                if (identity is null) continue;
                events.TryWrite(new AppEvent.TestStarted(identity));
                await Task.Delay(_perTestMs, ct).ConfigureAwait(false);
                var (outcome, detail) = FakeWatchScript.Outcome(identity);
                events.TryWrite(new AppEvent.TestFinished(identity, outcome, TimeSpan.FromMilliseconds(_perTestMs), detail));
            }
            events.TryWrite(new AppEvent.RunCompleted());
        }
        catch (OperationCanceledException)
        {
            // Shutdown: leave the reducer's RunCompleted sweep to clear any in-flight leaf.
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
