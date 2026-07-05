using System.Globalization;

namespace Ttr.Core;

/// <summary>
/// The single <c>--log</c> diagnostics umbrella (plan §3, brief M1). One file, one line per event,
/// each <c>&lt;iso-utc&gt; [CATEGORY] message</c> — timestamped and category-prefixed so a grep by
/// category untangles the interleaving of the concurrent producers. Categories:
/// <list type="bullet">
///   <item><c>SESSION</c> — lifecycle: startup, target resolution, restore/save/prune of state.</item>
///   <item><c>BUILD</c> — every out-of-process <c>dotnet build</c> invocation + its outcome.</item>
///   <item><c>ADAPTER</c> — test-platform traffic (VSTest diag pointer, MTP handshake/discovery/run).</item>
///   <item><c>WATCH</c> — per-cycle stage timing (folds in the <c>TTR_WATCH_LOG</c> alias).</item>
/// </list>
/// Writes are serialised through an internal lock and are entirely best-effort — a logging failure
/// (disk full, read-only path) is swallowed and never disturbs the session. This is IO plumbing, not
/// MVU state, so the internal lock is outside CLAUDE.md's no-locks-on-state rule (same carve-out as
/// <c>SessionStore</c>). Absent <c>--log</c> the sink is simply never constructed and every
/// <c>log?.X(...)</c> call site is a no-op with zero overhead.
/// </summary>
public sealed class DiagnosticLog
{
    public const string CategorySession = "SESSION";
    public const string CategoryBuild = "BUILD";
    public const string CategoryAdapter = "ADAPTER";
    public const string CategoryWatch = "WATCH";

    private readonly string _path;
    private readonly object _lock = new();

    public DiagnosticLog(string path) => _path = path;

    /// <summary>The resolved log file path (surfaced so adapters can place sibling native-diagnostic files).</summary>
    public string Path => _path;

    public void Write(string category, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O} [{category}] {message}");
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + "\n"); }
            catch (IOException) { /* best-effort: a logging failure never disturbs the session */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Session(string message) => Write(CategorySession, message);
    public void Build(string message) => Write(CategoryBuild, message);
    public void Adapter(string message) => Write(CategoryAdapter, message);
    public void Watch(string message) => Write(CategoryWatch, message);
}
