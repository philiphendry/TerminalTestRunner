using System.Threading.Channels;
using Ttr.Core;
using Ttr.Ui;

namespace Ttr.Cli;

/// <summary>
/// Launches side effects in reaction to reducer-produced state (plan invariant 1): rerun requests,
/// off-thread syntax highlighting for the 'o' modal, and toast auto-expiry. Invoked on the reducer
/// thread after each state change; it only kicks off cancellable tasks that feed events back onto the
/// one channel — it never blocks the reducer or mutates state.
/// </summary>
public sealed class Orchestrator
{
    private readonly ITestSessionAdapter? _adapter;
    private readonly TestTarget _target;
    private readonly ChannelWriter<AppEvent> _events;
    private readonly CancellationToken _ct;

    private long _lastRunGeneration;
    private long _lastToastId;
    private string? _lastModalPath;

    public Orchestrator(
        ITestSessionAdapter? adapter, ChannelWriter<AppEvent> events, CancellationToken ct,
        TestTarget? target = null)
    {
        _adapter = adapter;
        _target = target ?? new TestTarget("");
        _events = events;
        _ct = ct;
    }

    public void OnReduced(AppState prev, AppState next)
    {
        if (next.RunGeneration != _lastRunGeneration)
        {
            _lastRunGeneration = next.RunGeneration;
            if (_adapter is not null) Launch(RunEffect(next.RunSubset));   // real read-only mode has no adapter
        }

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
