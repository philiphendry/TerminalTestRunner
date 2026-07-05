using System.Diagnostics;
using System.Globalization;
using Ttr.Core;

namespace Ttr.Cli.Watch;

/// <summary>
/// Per-cycle stage timing (brief M7 / AC5). Each stage boundary — debounce fired, parallel builds done,
/// re-discovery done, diff done, rerun complete — is stamped to two sinks:
/// <list type="bullet">
///   <item>the dedicated <c>TTR_WATCH_LOG</c> file (raw <c>&lt;cycle&gt; &lt;stage&gt; &lt;iso-utc&gt; ticks=</c>
///     lines that <c>scripts/watch-latency.sh</c> parses into the T0→T5 table — a documented alias kept
///     stable for the harness);</item>
///   <item>the <c>--log</c> umbrella under the <c>WATCH</c> category (M1), so all diagnostics live in one file.</item>
/// </list>
/// Both are best-effort and no-op with zero overhead when their sink is absent, so normal watch runs are
/// unaffected.
/// </summary>
public sealed class WatchLog
{
    public const string Debounce = "T1_DEBOUNCE";
    public const string BuildsDone = "T2_BUILDS";
    public const string DiscoverDone = "T3_DISCOVER";
    public const string DiffDone = "T4_DIFF";
    public const string RerunDone = "T5_RERUN";

    private readonly string? _path;
    private readonly DiagnosticLog? _diag;
    private readonly object _lock = new();
    private int _cycle;

    public WatchLog(string? path, DiagnosticLog? diag = null)
    {
        _path = path;
        _diag = diag;
    }

    public bool Enabled => _path is not null || _diag is not null;

    /// <summary>Start a new cycle; returns its id so the stage marks can be correlated.</summary>
    public int BeginCycle() => Interlocked.Increment(ref _cycle);

    public void Mark(int cycle, string stage)
    {
        _diag?.Watch(string.Create(CultureInfo.InvariantCulture, $"cycle={cycle} {stage} ticks={Stopwatch.GetTimestamp()}"));
        if (_path is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{cycle} {stage} {DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()}");
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + "\n"); }
            catch (IOException) { /* logging is best-effort */ }
        }
    }
}
