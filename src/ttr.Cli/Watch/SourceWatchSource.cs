namespace Ttr.Cli.Watch;

/// <summary>
/// Watch mode A (plan §9): a <see cref="FileSystemWatcher"/> per project directory. A relevant change
/// (a <c>.cs</c> source or a <c>.csproj/.props/.targets</c> project file, ignoring <c>obj/</c>, <c>bin/</c>,
/// and editor temp artifacts) maps to that project and is pushed to the debouncer. Every declared project
/// directory is watched — including non-test libraries — because a library edit must reach its dependent
/// test projects through the graph closure (D5); the coordinator does that mapping.
/// </summary>
public sealed class SourceWatchSource : IWatchSource
{
    private readonly IReadOnlyList<(string ProjectPath, string Directory)> _projects;
    private readonly List<FileSystemWatcher> _watchers = [];
    private Action<WatchChange>? _onChange;

    public SourceWatchSource(IReadOnlyList<(string ProjectPath, string Directory)> projects)
    {
        _projects = projects;
    }

    public void Start(Action<WatchChange> onChange)
    {
        _onChange = onChange;
        foreach (var (projectPath, dir) in _projects)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            void Handle(object? _, FileSystemEventArgs e) => OnEvent(projectPath, e.FullPath);
            w.Changed += Handle;
            w.Created += Handle;
            w.Deleted += Handle;
            w.Renamed += (_, e) => OnEvent(projectPath, e.FullPath);
            // A buffer overflow means we may have missed events — be conservative and signal a change.
            w.Error += (_, _) => _onChange?.Invoke(new WatchChange(projectPath, false));
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    private void OnEvent(string projectPath, string fullPath)
    {
        if (!IsRelevant(fullPath)) return;
        _onChange?.Invoke(new WatchChange(projectPath, IsProjectFile(fullPath)));
    }

    /// <summary>A change worth a cycle: a source or project file, not an <c>obj/</c>/<c>bin/</c> artifact
    /// and not an editor temp file (POC-6's rename-save storm writes <c>.tmp</c>/<c>~</c>/vim's <c>4913</c>).</summary>
    internal static bool IsRelevant(string fullPath)
    {
        var norm = fullPath.Replace('\\', '/');
        if (norm.Contains("/obj/", StringComparison.Ordinal) || norm.Contains("/bin/", StringComparison.Ordinal))
            return false;

        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) return false;
        if (name is "4913") return false;                                   // vim's probe file
        if (name.StartsWith('.')) return false;                            // dotfiles / editor swap
        if (name.EndsWith('~')) return false;                              // gedit/emacs backup
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".tmp" or ".swp" or ".swx" or ".swo") return false;

        return ext is ".cs" || IsProjectFile(fullPath);
    }

    internal static bool IsProjectFile(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext is ".csproj" or ".props" or ".targets";
    }

    public ValueTask DisposeAsync()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        _watchers.Clear();
        _onChange = null;
        return ValueTask.CompletedTask;
    }
}
