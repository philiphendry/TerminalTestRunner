using System.CommandLine;
using System.Threading.Channels;
using Ttr.Build;
using Ttr.Cli.Watch;
using Ttr.Core;
using Ttr.Core.Persistence;
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
            Console.Error.WriteLine("not yet implemented: --tfm arrives in a later phase.");
            return 2;
        }

        var (watchKind, watchError) = o.ResolveWatch(pr);
        if (watchError is not null)
        {
            Console.Error.WriteLine(watchError);
            return 2;
        }

        var positionals = pr.GetValue(o.Targets) ?? [];
        var doContinue = o.IsSupplied(pr, o.Continue);

        if (pr.GetValue(o.Fake))
        {
            // --fake --continue is the scripted restore demo (brief M1); --fake --watch is the watch demo.
            if (doContinue) return App.RunFakeContinue();
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
        var stateDirOverride = o.IsSupplied(pr, o.StateDir) ? pr.GetValue(o.StateDir) : null;
        return RunRealAsync(positionals, noBuild, logPath, watchKind, doContinue, stateDirOverride)
            .GetAwaiter().GetResult();
    }

    /// <summary>Resolve targets (§3/§4), evaluate + detect (§6.2), then hand the pipeline to the TUI. When
    /// <paramref name="watchKind"/> is set, also build the watch coordinator (Phase 5, plan §9).</summary>
    private static async Task<int> RunRealAsync(
        string[] positionals, bool noBuild, string? logPath, WatchKind watchKind,
        bool doContinue, string? stateDirOverride)
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
        // State dir: --state-dir override, else .ttr/ beside the primary target (plan §10, brief M2).
        var stateDir = stateDirOverride is { Length: > 0 }
            ? Path.GetFullPath(stateDirOverride)
            : Path.Combine(Path.GetDirectoryName(resolved.PrimaryTargetPath ?? ".") ?? ".", ".ttr");
        var config = TtrConfig.Load(stateDir);
        var evaluator = new EvaluationService();
        var evaluations = EvaluateAll(evaluator, config, resolved.ProjectPaths);

        // Session store + fingerprint (the target files' content hashes). Restore, if asked, is validated
        // now (schema + fingerprint) but APPLIED after discovery so it attaches to the real tree (brief M4).
        var fingerprint = SessionStore.Fingerprint(resolved.PrimaryTargetPath, resolved.ProjectPaths);
        var store = new SessionStore(stateDir, fingerprint);
        var restore = doContinue ? store.Restore() : new RestoreOutcome(RestoreStatus.NoState);

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

        // A post-discovery action feeds the restore into the populated tree, and/or a degrade toast.
        var afterDiscovery = BuildRestoreAction(restore, evaluations);

        var watchLogPath = watchKind == WatchKind.Off
            ? null
            // Watch-cycle timing (brief M7 / AC5) goes to its OWN file via TTR_WATCH_LOG, kept separate from
            // --log (which is VSTest's diagnostic trace) so the two never clobber each other.
            : Environment.GetEnvironmentVariable("TTR_WATCH_LOG");
        var watchFactory = watchKind == WatchKind.Off
            ? null
            : BuildWatchFactory(backend, resolved.ProjectPaths, evaluations, watchKind, watchLogPath);

        return App.RunReal(backend, title, store, afterDiscovery, watchKind, watchFactory);
    }

    /// <summary>Build the post-discovery step (brief M4): if a prior session restored, emit
    /// <see cref="AppEvent.SessionRestored"/> with each result's staleness (its project assembly newer than the
    /// result) computed against the on-disk assemblies AFTER discovery/rebuild; if it degraded, emit a specific
    /// toast and proceed fresh. Null when there is nothing to do.</summary>
    private static Func<System.Threading.Channels.ChannelWriter<AppEvent>, Task>? BuildRestoreAction(
        RestoreOutcome restore, IReadOnlyList<ProjectEvaluation> evaluations)
    {
        if (restore.Status == RestoreStatus.NoState) return null;

        if (restore.Status != RestoreStatus.Restored)
        {
            var msg = restore.Status switch
            {
                RestoreStatus.TargetsChanged => "targets changed since last session — starting fresh",
                RestoreStatus.SchemaMismatch => "state format changed — starting fresh",
                _ => "previous session state was unreadable — starting fresh",
            };
            return writer => { writer.TryWrite(new AppEvent.Toast(msg)); return Task.CompletedTask; };
        }

        var session = restore.Session!;
        return writer =>
        {
            // Assembly mtimes are read HERE (post-discovery), so a project rebuilt during discovery is already
            // reflected: its assembly is newer than the restored result → the result is Stale (plan §10).
            var mtimes = AssemblyMtimes(evaluations);
            var results = new List<RestoredResult>(session.Tests.Count);
            foreach (var t in session.Tests)
            {
                var stale = StaleAgainst(t.Id, t.FinishedUtc, mtimes);
                results.Add(new RestoredResult(t.Id, t.Status, t.Duration, t.HasDetail, stale));
            }
            writer.TryWrite(new AppEvent.SessionRestored(results, session.Ui, RelativeTime(session.SavedUtc)));
            return Task.CompletedTask;
        };
    }

    /// <summary>Newest output-assembly write time per (projectPath, tfm), for staleness. Missing assembly →
    /// <see cref="DateTime.MaxValue"/> so a result whose binary is gone reads as stale, never fresh.</summary>
    private static Dictionary<(string, string), DateTime> AssemblyMtimes(IReadOnlyList<ProjectEvaluation> evaluations)
    {
        var map = new Dictionary<(string, string), DateTime>();
        foreach (var e in evaluations)
            foreach (var tfm in e.Tfms)
            {
                var t = !string.IsNullOrEmpty(tfm.OutputAssemblyPath) && File.Exists(tfm.OutputAssemblyPath)
                    ? File.GetLastWriteTimeUtc(tfm.OutputAssemblyPath)
                    : DateTime.MaxValue;
                map[(e.ProjectPath, tfm.Tfm)] = t;
            }
        return map;
    }

    /// <summary>A restored result is stale if its project's assembly is newer than when the result finished.
    /// The (projectPath, tfm) are the 2nd/3rd fields of the derived id (<c>adapterKind|project|tfm|…</c>).</summary>
    private static bool StaleAgainst(TestCaseId id, DateTime finishedUtc, Dictionary<(string, string), DateTime> mtimes)
    {
        var parts = id.Value.Split('|');
        if (parts.Length < 3) return false;
        return mtimes.TryGetValue((parts[1], parts[2]), out var mtime) && mtime > finishedUtc;
    }

    /// <summary>Format a save time as a coarse "&lt;n&gt; ago" for the restore notice.</summary>
    private static string RelativeTime(DateTime savedUtc)
    {
        if (savedUtc == default) return "a previous session";
        var span = DateTime.UtcNow - savedUtc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 45) return "moments ago";
        if (span.TotalMinutes < 45) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min ago";
        if (span.TotalHours < 22) return $"{Math.Max(1, (int)Math.Round(span.TotalHours))} h ago";
        return $"{Math.Max(1, (int)Math.Round(span.TotalDays))} d ago";
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
