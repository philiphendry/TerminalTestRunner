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
        var (watchKind, watchError) = o.ResolveWatch(pr);
        if (watchError is not null)
        {
            Console.Error.WriteLine(watchError);
            return 2;
        }

        var positionals = pr.GetValue(o.Targets) ?? [];
        var doContinue = o.IsSupplied(pr, o.Continue);
        var tfm = o.ResolveTfm(pr);

        // The --log umbrella (M1): one file, timestamped + category-prefixed. Created once here and threaded
        // through every producer (session lifecycle, builds, adapters, watch cycles). Null ⇒ every log?.X() no-ops.
        var logPathRaw = pr.GetValue(o.Log);
        var diag = string.IsNullOrWhiteSpace(logPathRaw) ? null : new DiagnosticLog(Path.GetFullPath(logPathRaw));

        if (pr.GetValue(o.Fake))
        {
            var seed = pr.GetValue(o.FakeSeed);
            // --tfm has no meaning against the synthetic fake tree; accept it (so --fake composes for the AC2
            // review command) and record that it was ignored rather than filtering nothing.
            if (tfm is not null) diag?.Session($"--tfm '{tfm}' ignored in --fake mode (fake has no real TFMs)");

            // --fake --continue is the scripted restore demo (brief M1); --fake --watch is the watch demo.
            if (doContinue) { diag?.Session("start: --fake --continue demo"); return App.RunFakeContinue(diag); }
            if (watchKind != WatchKind.Off) { diag?.Session("start: --fake --watch demo"); return App.RunFakeWatch(diag); }

            var scenario = positionals.Length > 0 ? positionals[0] : "default";
            if (!ScenarioBuilder.IsKnown(scenario))
            {
                Console.Error.WriteLine(
                    $"unknown fake scenario '{scenario}'. Known scenarios: default, big, flaky, slow, files, backend.");
                return 2;
            }
            diag?.Session($"start: --fake {scenario} (seed {seed})");
            return App.Run(scenario, seed, diag);
        }

        var noBuild = o.IsSupplied(pr, o.NoBuild);
        var stateDirOverride = o.IsSupplied(pr, o.StateDir) ? pr.GetValue(o.StateDir) : null;
        return RunRealAsync(positionals, noBuild, diag, watchKind, doContinue, stateDirOverride, tfm)
            .GetAwaiter().GetResult();
    }

    /// <summary>Resolve targets (§3/§4), evaluate + detect (§6.2), then hand the pipeline to the TUI. When
    /// <paramref name="watchKind"/> is set, also build the watch coordinator (Phase 5, plan §9).</summary>
    private static async Task<int> RunRealAsync(
        string[] positionals, bool noBuild, DiagnosticLog? diag, WatchKind watchKind,
        bool doContinue, string? stateDirOverride, string? tfm)
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
        diag?.Session($"targets resolved: primary={Path.GetFileName(resolved.PrimaryTargetPath ?? "targets")}, " +
                      $"{resolved.ProjectPaths.Count} project(s)" + (tfm is null ? "" : $", --tfm {tfm}") +
                      (noBuild ? ", --no-build" : "") + (watchKind != WatchKind.Off ? $", --watch {watchKind}" : "") +
                      (doContinue ? ", --continue" : ""));
        // State dir: --state-dir override, else .ttr/ beside the primary target (plan §10, brief M2).
        var stateDir = stateDirOverride is { Length: > 0 }
            ? Path.GetFullPath(stateDirOverride)
            : Path.Combine(Path.GetDirectoryName(resolved.PrimaryTargetPath ?? ".") ?? ".", ".ttr");
        var config = TtrConfig.Load(stateDir);
        var evaluator = new EvaluationService();
        var evaluations = EvaluateAll(evaluator, config, resolved.ProjectPaths);

        // --tfm (M1): filter every evaluation to the single requested TFM. A project with no matching TFM
        // drops out; if NO project across the target set declares it, that is a usage error (exit 2) with the
        // valid list — the same shape as an unknown --watch mode. Filtering here means discovery/build/run all
        // key off the pared-down Tfms list with no further special-casing (and it composes with --continue).
        if (tfm is not null)
        {
            var available = evaluations.SelectMany(e => e.TfmMonikers)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (!available.Any(t => string.Equals(t, tfm, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"unknown --tfm '{tfm}' for the target set. Available: {string.Join(", ", available)}");
                return 2;
            }
            evaluations = evaluations.Select(e => TfmFilter.Apply(e, tfm)).Where(e => e.Tfms.Count > 0).ToList();
        }

        // Session store + fingerprint (the target files' content hashes). Restore, if asked, is validated
        // now (schema + fingerprint) but APPLIED after discovery so it attaches to the real tree (brief M4).
        var fingerprint = SessionStore.Fingerprint(resolved.PrimaryTargetPath, resolved.ProjectPaths);
        var store = new SessionStore(stateDir, fingerprint);
        var restore = doContinue ? store.Restore() : new RestoreOutcome(RestoreStatus.NoState);
        if (doContinue) diag?.Session($"restore: status={restore.Status}" +
            (restore.Status == RestoreStatus.Restored ? $", {restore.Session!.Tests.Count} result(s)" : ""));

        // Zero test projects: exit 2 normally, but watch mode stays alive with a notice (plan §4, brief M6) —
        // a test project may still be added/built while watching.
        if (!evaluations.Any(IsTestProject) && watchKind == WatchKind.Off)
        {
            Console.Error.WriteLine("no test projects found in the target(s).");
            return 2;
        }

        var backend = new RealBackend(
            evaluations, resolved.Phantoms, resolved.SolutionErrors, noBuild, diag, evaluator, config, tfm);
        var title = Path.GetFileName(resolved.PrimaryTargetPath ?? "targets");

        // A post-discovery action feeds the restore into the populated tree, and/or a degrade toast.
        var afterDiscovery = BuildRestoreAction(restore, evaluations);

        // Watch-cycle timing (brief M7 / AC5): the dedicated TTR_WATCH_LOG file keeps the harness's raw
        // `<cycle> <stage> <iso>` format (kept as a documented alias), while --log additionally receives the
        // same stage marks under the WATCH category so all diagnostics live in one place.
        var watchLogPath = watchKind == WatchKind.Off ? null : Environment.GetEnvironmentVariable("TTR_WATCH_LOG");
        var watchFactory = watchKind == WatchKind.Off
            ? null
            : BuildWatchFactory(backend, resolved.ProjectPaths, evaluations, watchKind, watchLogPath, diag);

        return App.RunReal(backend, title, store, afterDiscovery, watchKind, watchFactory, diag);
    }

    /// <summary>Filter a project's per-TFM evaluations down to the single <c>--tfm</c> moniker (M1), keeping
    /// everything else (path, display name) intact. Case-insensitive; an empty result means the project does
    /// not target the requested TFM and is dropped by the caller.</summary>
    private static class TfmFilter
    {
        public static ProjectEvaluation Apply(ProjectEvaluation e, string tfm) =>
            e with { Tfms = e.Tfms.Where(t => string.Equals(t.Tfm, tfm, StringComparison.OrdinalIgnoreCase)).ToList() };
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
        IReadOnlyList<ProjectEvaluation> evaluations, WatchKind watchKind, string? logPath, DiagnosticLog? diag)
    {
        var log = new WatchLog(logPath, diag);
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
