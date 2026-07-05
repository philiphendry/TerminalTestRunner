using System.CommandLine;
using System.Threading.Channels;
using Ttr.Build;
using Ttr.Cli.Watch;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>Command dispatch: validate the parse and route to the fake or real backend, or an early exit
/// code (plan §3). Exit codes: 0 clean · 2 usage/target error.</summary>
public static class Cli
{
    public static int Run(CliOptions o, ParseResult pr)
    {
        if (o.AnyReserved(pr))
        {
            Console.Error.WriteLine(
                "not yet implemented: --continue/--tfm/--state-dir arrive in a later phase.");
            return 2;
        }

        var (watchKind, watchError) = o.ResolveWatch(pr);
        if (watchError is not null)
        {
            Console.Error.WriteLine(watchError);
            return 2;
        }

        var positionals = pr.GetValue(o.Targets) ?? [];

        if (pr.GetValue(o.Fake))
        {
            // --fake --watch is the scripted watch demo (brief M1); otherwise the normal fake session.
            if (watchKind != WatchKind.Off) return App.RunFakeWatch();

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
        return RunRealAsync(positionals, noBuild, logPath, watchKind).GetAwaiter().GetResult();
    }

    /// <summary>Resolve targets (§3/§4), evaluate + detect (§6.2), then hand the pipeline to the TUI. When
    /// <paramref name="watchKind"/> is set, also build the watch coordinator (Phase 5, plan §9).</summary>
    private static async Task<int> RunRealAsync(
        string[] positionals, bool noBuild, string? logPath, WatchKind watchKind)
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
        var stateDir = Path.Combine(Path.GetDirectoryName(resolved.PrimaryTargetPath ?? ".") ?? ".", ".ttr");
        var config = TtrConfig.Load(stateDir);
        var evaluator = new EvaluationService();
        var evaluations = EvaluateAll(evaluator, config, resolved.ProjectPaths);

        // Zero test projects: exit 2 normally, but watch mode stays alive with a notice (plan §4, brief M6) —
        // a test project may still be added/built while watching.
        if (!evaluations.Any(IsTestProject) && watchKind == WatchKind.Off)
        {
            Console.Error.WriteLine("no test projects found in the target(s).");
            return 2;
        }

        var backend = new RealBackend(
            evaluations, resolved.Phantoms, resolved.SolutionErrors, noBuild, logPath, evaluator, config);
        var title = Path.GetFileName(resolved.PrimaryTargetPath ?? "targets");

        if (watchKind == WatchKind.Off)
            return App.RunReal(backend, title);

        // Watch-cycle timing (brief M7 / AC5) goes to its OWN file via TTR_WATCH_LOG, kept separate from
        // --log (which is VSTest's diagnostic trace) so the two never clobber each other.
        var watchLogPath = Environment.GetEnvironmentVariable("TTR_WATCH_LOG");
        var watchFactory = BuildWatchFactory(backend, resolved.ProjectPaths, evaluations, watchKind, watchLogPath);
        return App.RunReal(backend, title, watchKind, watchFactory);
    }

    /// <summary>Build the watch-coordinator factory (plan §9): the graph over every project, the source
    /// (mode A watches all project dirs so a library edit reaches its dependents; mode B watches the test
    /// projects' output assemblies), and the test-project set the closure is intersected against.</summary>
    private static Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator> BuildWatchFactory(
        RealBackend backend, IReadOnlyList<string> allProjectPaths,
        IReadOnlyList<ProjectEvaluation> evaluations, WatchKind watchKind, string? logPath)
    {
        var log = new WatchLog(logPath);
        var graph = new ProjectGraphService(allProjectPaths);
        var testProjects = evaluations.Where(IsTestProject).Select(e => e.ProjectPath).ToList();

        var sourceDirs = evaluations
            .Select(e => (e.ProjectPath, Directory: Path.GetDirectoryName(e.ProjectPath) ?? ""))
            .Where(p => p.Directory.Length > 0)
            .ToList();

        var assemblies = evaluations
            .Where(IsTestProject)
            .SelectMany(e => e.Tfms.Select(t => (e.ProjectPath, t.OutputAssemblyPath)))
            .Where(a => !string.IsNullOrEmpty(a.OutputAssemblyPath))
            .ToList();

        return (writer, state, _) =>
        {
            IWatchSource source = watchKind == WatchKind.External
                ? new AssemblyWatchSource(assemblies)
                : new SourceWatchSource(sourceDirs);
            return new WatchCoordinator(
                source, backend, graph, testProjects, forceBuild: watchKind == WatchKind.Build, writer, state, log);
        };
    }

    private static bool IsTestProject(ProjectEvaluation e) =>
        e.Tfms.Any(t => t.Detection.Runner is RunnerKind.VsTest or RunnerKind.Mtp or RunnerKind.Unknown);

    /// <summary>Evaluate each project per-TFM (§6.2). Honours a per-project override from
    /// <c>.ttr/config.json</c>. Best-effort: a project that fails to evaluate is skipped with a stderr note.</summary>
    private static IReadOnlyList<ProjectEvaluation> EvaluateAll(
        EvaluationService service, TtrConfig config, IReadOnlyList<string> projectPaths)
    {
        var results = new List<ProjectEvaluation>(projectPaths.Count);
        foreach (var path in projectPaths)
        {
            try { results.Add(service.Evaluate(path, config.OverrideFor(path))); }
            catch (Exception ex) { Console.Error.WriteLine($"warning: could not evaluate {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return results;
    }
}
