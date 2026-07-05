using System.Collections.Concurrent;
using System.Threading.Channels;
using Ttr.Core;
using Ttr.Core.Persistence;
using Ttr.Ui;

namespace Ttr.Cli;

/// <summary>
/// Launches side effects in reaction to reducer-produced state (plan invariant 1): rerun requests,
/// off-thread syntax highlighting for the 'o' modal, toast auto-expiry, and (Phase 6) session saves after
/// every run and lazy loads of restored result details. Invoked on the reducer thread after each state
/// change; it only kicks off cancellable tasks that feed events back onto the one channel — it never blocks
/// the reducer or mutates state. The save SNAPSHOT is taken here on the reducer thread (cheap, safe), and the
/// serialise+write happens on a background task, so the reducer/render loop never stalls on disk I/O (brief M3).
/// </summary>
public sealed class Orchestrator
{
    private readonly ITestSessionAdapter? _adapter;
    private readonly TestTarget _target;
    private readonly ChannelWriter<AppEvent> _events;
    private readonly CancellationToken _ct;
    private readonly SessionStore? _store;
    private readonly DiagnosticLog? _diag;

    private long _lastRunGeneration;
    private long _lastToastId;
    private string? _lastModalPath;
    private TestCaseId? _lastDetailSelection;
    private readonly ConcurrentDictionary<TestCaseId, byte> _detailRequested = new();

    public Orchestrator(
        ITestSessionAdapter? adapter, ChannelWriter<AppEvent> events, CancellationToken ct,
        TestTarget? target = null, SessionStore? store = null, DiagnosticLog? diag = null)
    {
        _adapter = adapter;
        _target = target ?? new TestTarget("");
        _events = events;
        _ct = ct;
        _store = store;
        _diag = diag;
    }

    public void OnReduced(AppState prev, AppState next)
    {
        if (next.RunGeneration != _lastRunGeneration)
        {
            _lastRunGeneration = next.RunGeneration;
            if (_adapter is not null) Launch(RunEffect(next.RunSubset));   // real read-only mode has no adapter
        }

        // Save after every completed run (including watch auto-runs, brief M3): a run just ended when Running
        // fell. The snapshot is captured now (reducer thread); the write is backgrounded.
        if (_store is not null && prev.Running && !next.Running)
        {
            var snapshot = SessionSnapshot.Capture(next);
            Launch(SaveEffect(snapshot));
        }

        // Lazy-load a restored result's detail the moment it is selected (brief M5) — so the detail pane and
        // 'o' have content by the time the user opens them, without ever preloading all details.
        if (_store is not null) MaybeFetchDetail(next);

        if (next.Modal is { } m)
        {
            if (m.FilePath != _lastModalPath)
            {
                _lastModalPath = m.FilePath;
                if (m.Current.Exists) Launch(HighlightEffect(m.FilePath));
            }
        }
        else
        {
            _lastModalPath = null;
        }

        if (next.Toast is not null && next.ToastId != _lastToastId)
        {
            _lastToastId = next.ToastId;
            Launch(ExpireToast(next.ToastId));
        }
    }

    private async Task RunEffect(IReadOnlyList<TestCaseId> subset)
    {
        if (_adapter is null) return;
        try { await _adapter.RunAsync(_target, subset, _events, _ct); }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task SaveEffect(SessionSnapshot snapshot)
    {
        try
        {
            await Task.Run(() => _store!.Save(snapshot), _ct).ConfigureAwait(false);
            _diag?.Session("saved session state after run");
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception) { /* persistence is best-effort; never disturb the session */ }
    }

    /// <summary>If the selected leaf is a restored result whose detail lives on disk (and hasn't been requested
    /// yet), fetch it off-thread and feed it back as <see cref="AppEvent.DetailLoaded"/>.</summary>
    private void MaybeFetchDetail(AppState s)
    {
        if (s.Selection is not { } sel) return;
        if (sel.Equals(_lastDetailSelection)) return;   // only on a selection change (not every run event)
        _lastDetailSelection = sel;
        TestNode? node = null;
        foreach (var row in s.Rows)
            if (row.Node.Id.Equals(sel)) { node = row.Node; break; }
        if (node is null || !node.HasRestoredDetail || node.Detail is not null) return;
        if (!_detailRequested.TryAdd(node.Id, 0)) return;
        Launch(DetailEffect(node.Id));
    }

    private async Task DetailEffect(TestCaseId id)
    {
        try
        {
            var detail = await Task.Run(() => _store!.ReadDetail(id), _ct).ConfigureAwait(false);
            if (detail is not null) _events.TryWrite(new AppEvent.DetailLoaded(id, detail));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* best-effort; leave the node without detail */ }
    }

    private async Task HighlightEffect(string path)
    {
        try
        {
            var language = SyntaxHighlighter.LanguageForExtension(path);
            if (language is null) return; // unrecognised extension → stays plain
            var text = await File.ReadAllTextAsync(path, _ct).ConfigureAwait(false);
            var lines = SyntaxHighlighter.Highlight(text, language);
            if (lines is not null)
                _events.TryWrite(new AppEvent.HighlightReady(path, lines));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task ExpireToast(long id)
    {
        try
        {
            await Task.Delay(3000, _ct).ConfigureAwait(false);
            _events.TryWrite(new AppEvent.ToastExpired(id));
        }
        catch (OperationCanceledException) { }
    }

    private void Launch(Task task) =>
        _ = task.ContinueWith(static _ => { }, TaskScheduler.Default);
}
