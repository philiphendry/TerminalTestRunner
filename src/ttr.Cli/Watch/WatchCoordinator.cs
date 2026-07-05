using System.Threading.Channels;
using Ttr.Build;
using Ttr.Core;

namespace Ttr.Cli.Watch;

/// <summary>
/// Drives watch cycles (plan §9). A source (mode A or B) feeds raw changes into a <see cref="Debouncer"/>;
/// each debounced batch becomes one cycle: map changed projects → dependents closure (<see cref="ProjectGraphService"/>)
/// → affected TEST projects → parallel rebuild + re-discovery + diff (<see cref="RealBackend.RediscoverAsync"/>)
/// → auto-rerun the affected set (<see cref="AppEvent.WatchRerunRequested"/>).
///
/// Concurrency policy (plan §9 / M0.2): changes arriving during an active run are QUEUED and coalesced — the
/// run finishes, then one consolidated cycle executes. The rerun itself reuses the Phase 4 run queue (the
/// reducer coalesces <see cref="AppEvent.WatchRerunRequested"/> into <see cref="AppState.QueuedRerun"/> when a
/// run is active); this coordinator adds only the change-batch coalescing, never a second run queue.
/// </summary>
public sealed class WatchCoordinator : IAsyncDisposable
{
    private readonly IWatchSource _source;
    private readonly RealBackend _backend;
    private readonly ProjectGraphService _graph;
    private readonly HashSet<string> _testProjects;
    private readonly bool _forceBuild;
    private readonly ChannelWriter<AppEvent> _events;
    private readonly Func<AppState> _state;
    private readonly WatchLog _log;
    private readonly Debouncer _debouncer;
    private readonly Channel<WatchBatch> _batches;
    private Task? _loop;

    public WatchCoordinator(
        IWatchSource source, RealBackend backend, ProjectGraphService graph,
        IEnumerable<string> testProjectPaths, bool forceBuild,
        ChannelWriter<AppEvent> events, Func<AppState> state, WatchLog? log = null, int debounceMs = 300)
    {
        _source = source;
        _backend = backend;
        _graph = graph;
        _testProjects = new HashSet<string>(testProjectPaths.Select(Path.GetFullPath), StringComparer.Ordinal);
        _forceBuild = forceBuild;
        _events = events;
        _state = state;
        _log = log ?? new WatchLog(null);
        _batches = Channel.CreateUnbounded<WatchBatch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _debouncer = new Debouncer(debounceMs, b => _batches.Writer.TryWrite(b));
    }

    /// <summary>Start the source and spin up the cycle loop. The graph load (~1 s — it evaluates the project
    /// set) runs on the loop task so it never blocks the render start; a change arriving in that first second
    /// falls back to a self-only closure (degraded but safe — see <see cref="ProjectGraphService"/>).</summary>
    public void Start(CancellationToken ct)
    {
        _source.Start(c => _debouncer.Notify(c));
        _loop = Task.Run(async () => { _graph.Reload(); await RunLoopAsync(ct).ConfigureAwait(false); }, ct);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var first in _batches.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var changed = new HashSet<string>(first.Projects, StringComparer.Ordinal);
                var projFiles = new HashSet<string>(first.ProjectFileChanges, StringComparer.Ordinal);
                DrainInto(changed, projFiles);   // coalesce batches already queued behind this one
                await RunCycleAsync(changed, projFiles, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task RunCycleAsync(HashSet<string> changed, HashSet<string> projectFiles, CancellationToken ct)
    {
        // Queue behind an active run and coalesce into ONE follow-up cycle (M0.2 / AC6).
        if (_state().Running)
        {
            _events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Queued));
            while (_state().Running && !ct.IsCancellationRequested)
                await Task.Delay(30, ct).ConfigureAwait(false);
            DrainInto(changed, projectFiles);   // fold in whatever landed during the run
        }
        if (ct.IsCancellationRequested) return;

        // A project-file change re-evaluates that project's detection + reloads the graph — changed project
        // ONLY (plan §9: full-solution re-evaluation costs ~1 s even warm).
        if (projectFiles.Count > 0)
        {
            _graph.Reload();
            await _backend.ReevaluateAsync(projectFiles, _events, ct).ConfigureAwait(false);
        }

        // Changed → transitive dependents → the affected TEST projects (libraries build via each test
        // project's own `dotnet build`, plan §7).
        var affected = _graph.DependentsClosure(changed).Where(_testProjects.Contains).ToList();
        if (affected.Count == 0)
        {
            _events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Idle));
            return;
        }

        _events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.ChangeDetected));

        var cycle = _log.BeginCycle();
        _log.Mark(cycle, WatchLog.Debounce);   // T1: debounce fired, cycle starting (T0 is the save, known to the harness)

        // Parallel rebuild → (on success) re-discover + diff. A build failure surfaces the Phase 3 error
        // node and drops that project before discovery; RediscoverAsync returns the projects that got through.
        IReadOnlyList<string> rediscovered;
        try
        {
            // Skip restore on the common source-edit cycle (startup restored the target set); a project-file
            // change re-enables it, since a new package may need restoring.
            var noRestore = projectFiles.Count == 0;
            rediscovered = await _backend.RediscoverAsync(affected, _events, ct, _forceBuild, noRestore, _log, cycle).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        _log.Mark(cycle, WatchLog.DiffDone);   // T4: re-discovery diff applied (RediscoveryCompleted emitted)

        if (rediscovered.Count > 0)
        {
            _events.TryWrite(new AppEvent.WatchRerunRequested(rediscovered));
            // In measurement mode, wait for the auto-rerun to finish so T5 is a clean per-cycle number.
            if (_log.Enabled)
            {
                await WaitUntilAsync(s => s.Running, ct).ConfigureAwait(false);
                await WaitUntilAsync(s => !s.Running, ct).ConfigureAwait(false);
                _log.Mark(cycle, WatchLog.RerunDone);
            }
        }

        _events.TryWrite(new AppEvent.WatchStateChanged(WatchActivity.Idle));
    }

    private async Task WaitUntilAsync(Func<AppState, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 1500 && !ct.IsCancellationRequested; i++)   // ~30s cap so a stuck run can't wedge the loop
        {
            if (predicate(_state())) return;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private void DrainInto(HashSet<string> changed, HashSet<string> projectFiles)
    {
        while (_batches.Reader.TryRead(out var more))
        {
            changed.UnionWith(more.Projects);
            projectFiles.UnionWith(more.ProjectFileChanges);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Shutdown ordering (AC9): stop the source FIRST so no new change can enter, then the debouncer,
        // then complete the batch channel and drain the loop — a watcher event can never fire into a
        // disposed pipeline.
        await _source.DisposeAsync().ConfigureAwait(false);
        _debouncer.Dispose();
        _batches.Writer.TryComplete();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* best-effort shutdown */ }
        }
    }
}
