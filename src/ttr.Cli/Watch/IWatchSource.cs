namespace Ttr.Cli.Watch;

/// <summary>One raw change a watch source detected, already mapped to the owning project (plan §9).
/// <paramref name="IsProjectFile"/> marks a <c>.csproj/.props/.targets</c> change, which additionally
/// invalidates that project's evaluation cache and reloads the graph (brief M4).</summary>
public readonly record struct WatchChange(string ProjectPath, bool IsProjectFile);

/// <summary>
/// A watch source (plan §9, D4): source-file watching (mode A) or output-assembly watching (mode B).
/// Both detect raw filesystem changes, map them to the owning project, and push <see cref="WatchChange"/>s
/// to the coordinator, which debounces and coalesces them into cycles. The abstraction keeps the coordinator
/// identical for both modes.
/// </summary>
public interface IWatchSource : IAsyncDisposable
{
    /// <summary>Begin watching; each detected change is delivered to <paramref name="onChange"/> (which may
    /// be called from arbitrary threads — the coordinator's debouncer is the synchronisation point).</summary>
    void Start(Action<WatchChange> onChange);
}
