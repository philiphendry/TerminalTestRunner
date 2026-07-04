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

    public ReducerLoop(AppState initial) => _current = initial;

    /// <summary>The latest published snapshot. Read from any thread.</summary>
    public AppState Current => _current;

    public async Task<AppState> RunAsync(ChannelReader<AppEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var e in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _current = Reducer.Reduce(_current, e);
                if (_current.ShouldQuit) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — return whatever we last published.
        }
        return _current;
    }
}
