using System.Diagnostics;
using System.Globalization;

namespace Ttr.Cli.Watch;

/// <summary>
/// Per-cycle stage timing for the latency harness (brief M7 / AC5). When <c>--log</c> is set, the watch
/// pipeline stamps each stage boundary — debounce fired, parallel builds done, re-discovery done, diff done,
/// rerun complete — as one line <c>&lt;cycle&gt; &lt;stage&gt; &lt;iso-utc&gt;</c> that <c>scripts/watch-latency.sh</c>
/// parses into the T0→T5 segment table. No-op (and zero overhead) when logging is off, so normal watch runs
/// are unaffected.
/// </summary>
public sealed class WatchLog
{
    public const string Debounce = "T1_DEBOUNCE";
    public const string BuildsDone = "T2_BUILDS";
    public const string DiscoverDone = "T3_DISCOVER";
    public const string DiffDone = "T4_DIFF";
    public const string RerunDone = "T5_RERUN";

    private readonly string? _path;
    private readonly object _lock = new();
    private int _cycle;

    public WatchLog(string? path) => _path = path;

    public bool Enabled => _path is not null;

    /// <summary>Start a new cycle; returns its id so the stage marks can be correlated.</summary>
    public int BeginCycle() => Interlocked.Increment(ref _cycle);

    public void Mark(int cycle, string stage)
    {
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
