namespace Ttr.Build;

/// <summary>What target resolution decided (plan §3/§4).</summary>
public enum TargetOutcomeKind
{
    /// <summary>A concrete set of projects to evaluate was resolved.</summary>
    Resolved,

    /// <summary>Several candidates were found in the CWD scan — the interactive picker must choose.</summary>
    Picker,

    /// <summary>No targets given and nothing found in the CWD — exit 2.</summary>
    NoCandidates,

    /// <summary>A usage/target error (bad path, unparseable solution with nothing usable) — exit 2.</summary>
    Error,
}

/// <summary>The projects a target set expands to, plus the non-fatal diagnostics found along the way.</summary>
public sealed record ResolvedTargets(
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<PhantomProject> Phantoms,
    IReadOnlyList<SolutionError> SolutionErrors,
    string? PrimaryTargetPath);

/// <summary>The result of resolving CLI targets. Interactive/error branches carry a message + exit code.</summary>
public sealed record TargetResolution(
    TargetOutcomeKind Kind,
    ResolvedTargets? Resolved = null,
    IReadOnlyList<string>? Candidates = null,
    string? Message = null,
    int ExitCode = 0);

/// <summary>
/// Target selection (plan §3/§4): positional <c>.csproj/.sln/.slnx</c> targets; a CWD top-level scan when
/// none are given (exactly one → auto-select; several → picker; none → exit 2); solutions expanded to their
/// project set via <see cref="SolutionParser"/>. Pure except for the filesystem reads it must do
/// (directory scan, solution parse, existence checks) — no MSBuild, no UI.
/// </summary>
public static class TargetResolver
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private const string ProjectExtension = ".csproj";

    /// <summary>Top-level <c>.sln/.slnx/.csproj</c> candidates in <paramref name="cwd"/> (a solution wins:
    /// if any solution is present the bare csprojs are not offered separately).</summary>
    public static IReadOnlyList<string> ScanCandidates(string cwd)
    {
        var solutions = Directory.EnumerateFiles(cwd)
            .Where(f => SolutionExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (solutions.Count > 0) return solutions;

        return Directory.EnumerateFiles(cwd)
            .Where(f => string.Equals(Path.GetExtension(f), ProjectExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Resolve the effective target list (no scan/picker here — the caller supplies the final set).</summary>
    public static async Task<TargetResolution> ResolveAsync(
        IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        var projects = new List<string>();
        var phantoms = new List<PhantomProject>();
        var solutionErrors = new List<SolutionError>();
        string? primary = null;

        foreach (var target in targets)
        {
            var full = Path.GetFullPath(target);
            var ext = Path.GetExtension(full).ToLowerInvariant();

            if (SolutionExtensions.Contains(ext))
            {
                primary ??= full;
                var parsed = await SolutionParser.ParseAsync(full, ct).ConfigureAwait(false);
                if (parsed.Error is { } err)
                {
                    solutionErrors.Add(err);
                    continue;
                }
                projects.AddRange(parsed.ProjectPaths);
                phantoms.AddRange(parsed.Phantoms);
            }
            else if (ext == ProjectExtension)
            {
                if (!File.Exists(full))
                    return new TargetResolution(TargetOutcomeKind.Error,
                        Message: $"project not found: {full}", ExitCode: 2);
                primary ??= full;
                projects.Add(full);
            }
            else
            {
                return new TargetResolution(TargetOutcomeKind.Error,
                    Message: $"unsupported target (need .csproj/.sln/.slnx): {target}", ExitCode: 2);
            }
        }

        // A solution parsed but yielded nothing usable, and no other project came in → hard error.
        if (projects.Count == 0)
        {
            var detail = solutionErrors.Count > 0 ? solutionErrors[0].Message : "no projects in the target(s)";
            return new TargetResolution(TargetOutcomeKind.Error, Message: detail, ExitCode: 2);
        }

        var resolved = new ResolvedTargets(
            Dedupe(projects), phantoms, solutionErrors, primary ?? projects[0]);
        return new TargetResolution(TargetOutcomeKind.Resolved, resolved);
    }

    private static IReadOnlyList<string> Dedupe(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var p in paths)
            if (seen.Add(p)) result.Add(p);
        return result;
    }
}
