using System.Collections.Concurrent;

namespace Ttr.Cli.Watch;

/// <summary>
/// Watch mode B (plan §9): the user drives builds; ttr watches only the per-TFM PRIMARY output assemblies
/// (never all of <c>bin/</c> — POC-6 showed that means quiescing over ~170 transitive DLLs for no benefit).
/// A write starts a 500 ms stability window (no further write for 500 ms ⇒ the build has settled); an
/// exclusive-open probe is a belt-and-braces extra on top (Linux has no mandatory locking, so the window is
/// the real signal). A no-op incremental build doesn't rewrite the assembly, so it fires zero cycles with no
/// special-casing (POC-6 / AC4). Only test-project assemblies are watched — those are what re-discovery runs.
/// </summary>
public sealed class AssemblyWatchSource : IWatchSource
{
    private const int QuiescenceMs = 500;

    private readonly IReadOnlyList<(string ProjectPath, string AssemblyPath)> _assemblies;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private Action<WatchChange>? _onChange;
    private bool _disposed;

    public AssemblyWatchSource(IReadOnlyList<(string ProjectPath, string AssemblyPath)> assemblies)
    {
        _assemblies = assemblies;
    }

    public void Start(Action<WatchChange> onChange)
    {
        _onChange = onChange;
        // One watcher per assembly directory, filtered to that assembly's filename.
        foreach (var group in _assemblies.GroupBy(a => Path.GetDirectoryName(a.AssemblyPath), StringComparer.Ordinal))
        {
            var dir = group.Key;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var byName = group.ToDictionary(a => Path.GetFileName(a.AssemblyPath), a => a.ProjectPath, StringComparer.Ordinal);
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            void Handle(object? _, FileSystemEventArgs e)
            {
                if (byName.TryGetValue(Path.GetFileName(e.FullPath), out var project))
                    Touch(e.FullPath, project);
            }
            w.Changed += Handle;
            w.Created += Handle;
            w.Renamed += (_, e) => { if (byName.TryGetValue(Path.GetFileName(e.FullPath), out var p)) Touch(e.FullPath, p); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    /// <summary>(Re)start the per-assembly quiescence timer; it fires 500 ms after the LAST write.</summary>
    private void Touch(string assemblyPath, string projectPath)
    {
        if (_disposed) return;
        var timer = _timers.GetOrAdd(assemblyPath,
            _ => new Timer(_ => OnStable(assemblyPath, projectPath), null, Timeout.Infinite, Timeout.Infinite));
        try { timer.Change(QuiescenceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnStable(string assemblyPath, string projectPath)
    {
        if (_disposed) return;
        // Belt-and-braces: if the file is still being written (exclusive open fails), wait another window.
        if (!CanOpenExclusively(assemblyPath))
        {
            if (_timers.TryGetValue(assemblyPath, out var t))
                try { t.Change(QuiescenceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
            return;
        }
        _onChange?.Invoke(new WatchChange(projectPath, false));
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return false; }                  // still being written / sharing violation
        catch (UnauthorizedAccessException) { return true; }   // permissions, not an in-progress write
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        _watchers.Clear();
        foreach (var t in _timers.Values) t.Dispose();
        _timers.Clear();
        _onChange = null;
        return ValueTask.CompletedTask;
    }
}
