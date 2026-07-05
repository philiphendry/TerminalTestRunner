namespace Ttr.Cli.Watch;

/// <summary>One coalesced batch of change: the union of changed projects since the last flush, plus the
/// subset whose project files changed (so the coordinator can invalidate their evaluation + reload the graph).</summary>
public sealed record WatchBatch(
    IReadOnlyCollection<string> Projects,
    IReadOnlyCollection<string> ProjectFileChanges);

/// <summary>
/// Trailing-edge debounce with coalescing (plan §9): every <see cref="Notify"/> resets a timer; when it
/// finally elapses (no change for <c>delayMs</c>) the accumulated set flushes as ONE batch. This is what
/// turns an editor's temp-write+rename storm, an in-place multi-write save, or a burst of edits across
/// several projects into a single watch cycle (brief M4 / AC3). The accumulate/flush split is deliberate so
/// the coalescing is unit-testable without real timers (drive <see cref="Notify"/> then <see cref="Flush"/>);
/// the 300 ms window itself is exercised by the integration tests against a real <c>FileSystemWatcher</c>.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly int _delayMs;
    private readonly Action<WatchBatch> _onFlush;
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectFiles = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _disposed;

    public Debouncer(int delayMs, Action<WatchBatch> onFlush)
    {
        _delayMs = delayMs;
        _onFlush = onFlush;
    }

    public void Notify(WatchChange change)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending.Add(change.ProjectPath);
            if (change.IsProjectFile) _projectFiles.Add(change.ProjectPath);
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_delayMs, Timeout.Infinite);   // trailing edge: each event pushes the deadline out
        }
    }

    /// <summary>Emit the accumulated batch now (called by the timer, or directly by tests). No-op if empty.</summary>
    public void Flush()
    {
        WatchBatch batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = new WatchBatch(_pending.ToArray(), _projectFiles.ToArray());
            _pending.Clear();
            _projectFiles.Clear();
        }
        _onFlush(batch);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
