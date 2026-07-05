using System.Threading.Channels;

namespace Ttr.Core;

/// <summary>
/// The single event consumer (CLAUDE.md invariant 1). Drains the one <see cref="AppEvent"/>
/// channel, applies the pure <see cref="Reducer"/>, and publishes the latest snapshot to a
/// volatile field the render thread reads without locking. Exits when a reducer produces a
/// <see cref="AppState.ShouldQuit"/> state or the channel completes.
/// </summary>
public sealed class ReducerLoop
{
    private volatile AppState _current;
    private readonly Action<AppState, AppState>? _onReduced;

    /// <summary>
    /// <paramref name="onReduced"/> (the orchestrator) is invoked after each state change, on the
    /// reducer thread, to LAUNCH side effects in reaction to state (plan invariant 1). It must not
    /// block or mutate state — only fire off cancellable tasks that feed events back onto the channel.
    /// </summary>
    public ReducerLoop(AppState initial, Action<AppState, AppState>? onReduced = null)
    {
        _current = initial;
        _onReduced = onReduced;
    }

    /// <summary>The latest published snapshot. Read from any thread.</summary>
    public AppState Current => _current;

    public async Task<AppState> RunAsync(ChannelReader<AppEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var e in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var prev = _current;
                var next = Reducer.Reduce(prev, e);
                _current = next;
                if (!ReferenceEquals(prev, next)) _onReduced?.Invoke(prev, next);
                if (next.ShouldQuit) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — return whatever we last published.
        }
        return _current;
    }
}
