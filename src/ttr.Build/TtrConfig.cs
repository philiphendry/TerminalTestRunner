using System.Text.Json;
using Ttr.Core;

namespace Ttr.Build;

/// <summary>
/// Read-only <c>.ttr/config.json</c> support (plan §6.2 / brief M2 — no writer this phase). The only
/// key honoured now is a per-project runner override for pathological detection cases:
/// <code>{ "runnerOverrides": { "Some.Tests": "vstest", "…/Other.Tests.csproj": "mtp" } }</code>
/// Keys match either the project file name (with or without <c>.csproj</c>) or its full path.
/// Unknown keys and a malformed file are ignored (best-effort; config never blocks discovery).
/// </summary>
public sealed class TtrConfig
{
    private readonly IReadOnlyDictionary<string, RunnerKind> _overrides;

    private TtrConfig(IReadOnlyDictionary<string, RunnerKind> overrides) => _overrides = overrides;

    public static TtrConfig Empty { get; } = new(new Dictionary<string, RunnerKind>());

    /// <summary>Load <c>&lt;stateDir&gt;/config.json</c> if present, else an empty config.</summary>
    public static TtrConfig Load(string stateDir)
    {
        var path = Path.Combine(stateDir, "config.json");
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runnerOverrides", out var ro)
                || ro.ValueKind != JsonValueKind.Object)
                return Empty;

            var map = new Dictionary<string, RunnerKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in ro.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && ParseRunner(prop.Value.GetString()) is { } r)
                    map[prop.Name] = r;
            return new TtrConfig(map);
        }
        catch (JsonException) { return Empty; }
        catch (IOException) { return Empty; }
    }

    /// <summary>The runner override for a project, matched by full path or (base) file name; null if none.</summary>
    public RunnerKind? OverrideFor(string projectPath)
    {
        if (_overrides.Count == 0) return null;
        if (_overrides.TryGetValue(projectPath, out var byPath)) return byPath;
        var withExt = Path.GetFileName(projectPath);
        if (_overrides.TryGetValue(withExt, out var byFile)) return byFile;
        var noExt = Path.GetFileNameWithoutExtension(projectPath);
        return _overrides.TryGetValue(noExt, out var byName) ? byName : null;
    }

    private static RunnerKind? ParseRunner(string? value) => value?.ToLowerInvariant() switch
    {
        "vstest" => RunnerKind.VsTest,
        "mtp" => RunnerKind.Mtp,
        "unknown" => RunnerKind.Unknown,
        _ => null,
    };
}
