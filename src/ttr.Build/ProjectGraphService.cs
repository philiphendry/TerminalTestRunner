using Microsoft.Build.Graph;

namespace Ttr.Build;

/// <summary>
/// The in-memory project dependency graph (plan §7/§9, brief M2). Loads
/// <see cref="Microsoft.Build.Graph.ProjectGraph"/> over the target set once at startup and exposes the
/// reverse-dependents closure watch mode needs: a changed project plus everything that transitively
/// references it (D5 — no per-test impact analysis). It is reloaded ONLY when a project/solution file
/// changes (a source edit never changes the graph), matching the plan's "hold in memory" rule.
///
/// CLAUDE.md build rule: this type references <c>Microsoft.Build.Graph</c>, so — like
/// <see cref="EvaluationService"/> — it must only be touched AFTER <c>MSBuildLocator.RegisterDefaults()</c>
/// has run (see <c>Program.cs</c>): the JIT resolves the referenced MSBuild types on first entry.
/// </summary>
public sealed class ProjectGraphService
{
    private readonly IReadOnlyList<string> _entryPoints;

    // project full path → the projects that (directly) reference it. Transitive closure is walked on demand.
    private Dictionary<string, List<string>> _reverse = new(StringComparer.Ordinal);

    public ProjectGraphService(IEnumerable<string> projectPaths)
    {
        _entryPoints = projectPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True once the graph has loaded with reverse edges available; false ⇒ degraded (self-only
    /// closure). Exposed for the notes/diagnostics.</summary>
    public bool Loaded { get; private set; }

    /// <summary>The load failure message, if the last <see cref="Reload"/> failed (else null).</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Load or reload the dependency graph. Best-effort: on failure the graph degrades to "every project
    /// implicates only itself", which never rebuilds FEWER projects than a source edit strictly requires —
    /// it can only miss a transitive dependent, and a subsequent run-all still catches that. The caller
    /// re-invokes this when a <c>.csproj/.props/.targets</c> or solution file changes.
    /// </summary>
    public void Reload()
    {
        var reverse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            var graph = new ProjectGraph(_entryPoints);
            foreach (var node in graph.ProjectNodes)
            {
                var path = node.ProjectInstance.FullPath;
                // ReferencingProjects are the reverse edges directly (projects that depend on this one).
                var dependents = node.ReferencingProjects
                    .Select(r => r.ProjectInstance.FullPath);

                if (!reverse.TryGetValue(path, out var list))
                    reverse[path] = list = [];
                foreach (var d in dependents)
                    if (!list.Contains(d, StringComparer.Ordinal))
                        list.Add(d);   // multi-TFM: several nodes share a FullPath — union their dependents
            }
            _reverse = reverse;
            Loaded = true;
            Error = null;
        }
        catch (Exception ex)
        {
            _reverse = reverse;   // whatever partial map we built (usually empty) → self-only closure
            Loaded = false;
            Error = ex is AggregateException agg ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message)) : ex.Message;
        }
    }

    /// <summary>
    /// The transitive reverse-dependents closure of <paramref name="changedProjects"/>, inclusive of the
    /// changed projects themselves (D5). Every returned path is a normalised full path.
    /// </summary>
    public IReadOnlyCollection<string> DependentsClosure(IEnumerable<string> changedProjects) =>
        WalkClosure(_reverse, changedProjects.Select(Path.GetFullPath));

    /// <summary>
    /// The transitive reverse-dependents closure over an explicit reverse-edge map (project → its direct
    /// dependents), inclusive of the changed set. Pure — extracted so the POC-6 correctness cases (chained /
    /// diamond deps, unrelated project untouched, test-project-only change implicates only itself) are
    /// unit-testable without loading MSBuild.
    /// </summary>
    public static IReadOnlyCollection<string> WalkClosure(
        IReadOnlyDictionary<string, List<string>> reverse, IEnumerable<string> changed)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var p in changed)
            if (result.Add(p)) queue.Enqueue(p);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!reverse.TryGetValue(current, out var dependents)) continue;
            foreach (var d in dependents)
                if (result.Add(d)) queue.Enqueue(d);
        }
        return result;
    }
}
