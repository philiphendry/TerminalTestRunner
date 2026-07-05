using System.CommandLine;
using Ttr.Build;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>Command dispatch: validate the parse and route to the fake or real backend, or an early exit
/// code (plan §3). Exit codes: 0 clean · 2 usage/target error.</summary>
public static class Cli
{
    public static int Run(CliOptions o, ParseResult pr)
    {
        // Flags for later phases still reject; --no-build/--log are functional this phase.
        if (o.AnyReserved(pr))
        {
            Console.Error.WriteLine(
                "not yet implemented: --continue/--watch/--tfm/--state-dir arrive in a later phase.");
            return 2;
        }

        var positionals = pr.GetValue(o.Targets) ?? [];

        if (pr.GetValue(o.Fake))
        {
            var scenario = positionals.Length > 0 ? positionals[0] : "default";
            if (!ScenarioBuilder.IsKnown(scenario))
            {
                Console.Error.WriteLine(
                    $"unknown fake scenario '{scenario}'. Known scenarios: default, big, flaky, slow, files, backend.");
                return 2;
            }
            return App.Run(scenario, pr.GetValue(o.FakeSeed));
        }

        var noBuild = o.IsSupplied(pr, o.NoBuild);
        var logPath = pr.GetValue(o.Log);
        return RunRealAsync(positionals, noBuild, logPath).GetAwaiter().GetResult();
    }

    /// <summary>Resolve targets (§3/§4), evaluate + detect (§6.2), then hand the pipeline to the TUI.</summary>
    private static async Task<int> RunRealAsync(string[] positionals, bool noBuild, string? logPath)
    {
        // Target selection: positional targets, else a CWD top-level scan (one → auto; several → picker).
        IReadOnlyList<string> targets = positionals;
        if (targets.Count == 0)
        {
            var candidates = TargetResolver.ScanCandidates(Directory.GetCurrentDirectory());
            if (candidates.Count == 0)
            {
                Console.Error.WriteLine("no .sln/.slnx/.csproj found in the current directory. Pass a target explicitly.");
                return 2;
            }
            targets = candidates.Count == 1 ? [candidates[0]] : Picker.Choose(candidates);
            if (targets.Count == 0) return 0;   // user cancelled the picker
        }

        var resolution = await TargetResolver.ResolveAsync(targets).ConfigureAwait(false);
        if (resolution.Kind != TargetOutcomeKind.Resolved)
        {
            Console.Error.WriteLine(resolution.Message ?? "could not resolve targets.");
            return resolution.ExitCode == 0 ? 2 : resolution.ExitCode;
        }

        var resolved = resolution.Resolved!;
        var evaluations = EvaluateAll(resolved.ProjectPaths, resolved.PrimaryTargetPath);

        // Zero test projects in an explicit target → exit 2 (plan §4).
        if (!evaluations.Any(IsTestProject))
        {
            Console.Error.WriteLine("no test projects found in the target(s).");
            return 2;
        }

        var backend = new RealBackend(evaluations, resolved.Phantoms, resolved.SolutionErrors, noBuild, logPath);
        var title = Path.GetFileName(resolved.PrimaryTargetPath ?? "targets");
        return App.RunReal(backend, title);
    }

    private static bool IsTestProject(ProjectEvaluation e) =>
        e.Tfms.Any(t => t.Detection.Runner is RunnerKind.VsTest or RunnerKind.Mtp or RunnerKind.Unknown);

    /// <summary>Evaluate each project per-TFM (§6.2). Honours a per-project override from
    /// <c>.ttr/config.json</c> beside the primary target. Best-effort: a project that fails to evaluate
    /// (e.g. not restored) is skipped with a stderr note rather than aborting the whole session.</summary>
    private static IReadOnlyList<ProjectEvaluation> EvaluateAll(IReadOnlyList<string> projectPaths, string? primary)
    {
        var stateDir = Path.Combine(Path.GetDirectoryName(primary ?? ".") ?? ".", ".ttr");
        var config = TtrConfig.Load(stateDir);
        var service = new EvaluationService();

        var results = new List<ProjectEvaluation>(projectPaths.Count);
        foreach (var path in projectPaths)
        {
            try { results.Add(service.Evaluate(path, config.OverrideFor(path))); }
            catch (Exception ex) { Console.Error.WriteLine($"warning: could not evaluate {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return results;
    }
}
