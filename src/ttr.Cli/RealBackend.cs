using System.Threading.Channels;
using Ttr.Build;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>
/// The real backend as an <see cref="ITestSessionAdapter"/>: it turns resolved targets into the same
/// <see cref="AppEvent"/> stream the Fake adapter produces (the backend is invisible behind the event stream).
/// <see cref="DiscoverAsync"/> registers evaluated projects (§6.2), raises phantom/unparseable notices (§4),
/// builds stale projects in parallel (§7), and streams discovered tests — VSTest via a persistent
/// <see cref="VsTestDiscoverer"/> (§6.3) and MTP via one long-lived <see cref="MtpSession"/> per (project, TFM),
/// discovered in parallel capped at logical-CPU/2 sessions (§6.4), keeping those adapters ALIVE. <see cref="RunAsync"/> (Phase 4) routes a subset to the owning
/// adapters, rebuilding stale projects first (restarting their MTP host on a rebuild — staleness ⇒ new
/// process), fanning the per-adapter runs out concurrently and merging their streams, then emitting one
/// <see cref="AppEvent.RunCompleted"/>. A single gate serialises discovery and runs so the shared vstest
/// wrapper / one-request-per-session MTP hosts never see overlapping operations (run-vs-run coalescing is the
/// reducer's job; this gate only prevents structural overlap).
/// </summary>
public sealed class RealBackend : ITestSessionAdapter
{
    private readonly IReadOnlyList<ProjectEvaluation> _projects;
    private readonly IReadOnlyList<PhantomProject> _phantoms;
    private readonly IReadOnlyList<SolutionError> _solutionErrors;
    private readonly bool _noBuild;
    private readonly DiagnosticLog? _diag;
    private readonly string? _vsTestDiagPath;
    private readonly EvaluationService? _evaluator;
    private readonly TtrConfig? _config;
    private readonly string? _tfmFilter;

    /// <summary>Cap on concurrent MTP discovery sessions (plan §6.4: logical-CPU/2).</summary>
    private static readonly int MtpDiscoveryConcurrency = Math.Max(1, Environment.ProcessorCount / 2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ProjectEvaluation> _registered = [];
    private readonly Dictionary<string, MtpSession> _mtpSessions = new(StringComparer.Ordinal);
    private VsTestDiscoverer? _vsTest;
    private string? _vsTestConsole;

    public RealBackend(
        IReadOnlyList<ProjectEvaluation> projects,
        IReadOnlyList<PhantomProject> phantoms,
        IReadOnlyList<SolutionError> solutionErrors,
        bool noBuild,
        DiagnosticLog? diag = null,
        EvaluationService? evaluator = null,
        TtrConfig? config = null,
        string? tfmFilter = null)
    {
        _projects = projects;
        _phantoms = phantoms;
        _solutionErrors = solutionErrors;
        _noBuild = noBuild;
        _diag = diag;
        // VSTest's diagnostics use vstest.console's own multi-line trace format, so they get a sibling file
        // rather than being interleaved into the category-prefixed umbrella; the umbrella carries a pointer.
        _vsTestDiagPath = diag is null ? null : diag.Path + ".vstest.diag";
        _evaluator = evaluator;
        _config = config;
        _tfmFilter = tfmFilter;
    }

    /// <summary>The MTP server versions observed at handshake (project → serverInfo.version), for the notes/PR.</summary>
    public IReadOnlyDictionary<string, string> MtpVersions =>
        _mtpSessions.Where(kv => kv.Value.ServerVersion is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ServerVersion!, StringComparer.Ordinal);

    // --- ITestSessionAdapter: discovery ----------------------------------------

    public async Task DiscoverAsync(TestTarget target, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Standalone diagnostic nodes: phantom projects + unparseable solution entries (§4).
            var noticeId = 0;
            foreach (var p in _phantoms)
                events.TryWrite(new AppEvent.NoticeRaised($"phantom:{p.ResolvedPath}", p.Name,
                    new NodeNotice(NoticeSeverity.Warning, "project file not found", p.ResolvedPath)));
            foreach (var e in _solutionErrors)
                events.TryWrite(new AppEvent.NoticeRaised($"slnerror:{noticeId++}", "<solution error>",
                    new NodeNotice(NoticeSeverity.Error, MessageFor(e.Kind), e.Message)));

            // 2. Register every test project with its detection (§6.2). Non-test projects never enter the tree.
            foreach (var proj in _projects)
            {
                var (runner, notice) = Classify(proj);
                if (runner is RunnerKind.NotATest) continue;
                events.TryWrite(new AppEvent.ProjectRegistered(
                    proj.ProjectPath, proj.DisplayName, proj.TfmMonikers, runner, notice));
                _registered.Add(proj);
            }

            // Watch mode over a target with no test projects stays alive with a notice instead of exiting
            // (plan §4, brief M6) — a project may still be added/built while watching.
            if (_registered.Count == 0)
                events.TryWrite(new AppEvent.NoticeRaised("no-test-projects", "no test projects",
                    new NodeNotice(NoticeSeverity.Warning, "no test projects in the target(s) yet",
                        "Add or build a test project and it will appear on the next watch cycle.")));

            // 3. Build stale projects in parallel (§7); discovery is gated on build success per project.
            var built = await BuildProjectsAsync(_registered, events, ct).ConfigureAwait(false);

            // 4. Discover each built project through the persistent adapters (kept alive for runs).
            await DiscoverProjectsAsync(_registered.Where(p => built.Contains(p.ProjectPath)).ToList(), events, ct)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // --- ITestSessionAdapter: runs (Phase 4) -----------------------------------

    public async Task RunAsync(
        TestTarget target, IReadOnlyList<TestCaseId> subset, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Affected projects: run-all touches every registered test project; a subset touches only those
            // that own one of its ids (parsed from the derived id — adapterKind|projectPath|tfm|…).
            var runAll = subset.Count == 0;
            var affectedPaths = runAll
                ? _registered.Select(p => p.ProjectPath).ToHashSet(StringComparer.Ordinal)
                : subset.Select(ProjectOf).Where(p => p is not null).Select(p => p!).ToHashSet(StringComparer.Ordinal);
            var affected = _registered.Where(p => affectedPaths.Contains(p.ProjectPath)).ToList();

            // Staleness → parallel rebuild of the stale affected projects (Phase 3 BuildService). A build
            // failure aborts THAT project's run (error node) while the others proceed. A rebuilt project is
            // re-discovered (its MTP host restarted — staleness ⇒ new process) so its run uses fresh binaries.
            var rebuilt = new List<ProjectEvaluation>();
            if (!_noBuild)
            {
                var stale = affected.Where(IsStale).ToList();
                if (stale.Count > 0)
                {
                    var ok = await BuildProjectsAsync(stale, events, ct).ConfigureAwait(false);
                    rebuilt = stale.Where(p => ok.Contains(p.ProjectPath)).ToList();
                    affected = affected.Where(p => !stale.Contains(p) || ok.Contains(p.ProjectPath)).ToList();
                }
            }
            if (rebuilt.Count > 0)
                await DiscoverProjectsAsync(rebuilt, events, ct, rediscover: true).ConfigureAwait(false);

            // Fan out per-adapter runs concurrently and merge their event streams.
            var runs = new List<Task>();
            var affectedSet = affected.Select(p => p.ProjectPath).ToHashSet(StringComparer.Ordinal);

            if (_vsTest is not null && affected.Any(p => Runner(p) == RunnerKind.VsTest))
            {
                var vsSubset = runAll ? [] : subset.Where(id => AdapterOf(id) == VsTestDiscoverer.AdapterKind).ToList();
                runs.Add(Guarded(() => _vsTest.RunAsync(vsSubset, events, ct), ct));
            }

            foreach (var session in _mtpSessions.Values)
                if (affectedSet.Contains(session.ProjectPath))
                    runs.Add(Guarded(() => session.RunAsync(runAll ? [] : subset, events, ct), ct));

            _diag?.Adapter($"run {(runAll ? "all" : $"{subset.Count} test(s)")} across {affected.Count} project(s)");
            // Guarded() converts a single adapter's failure into completion (not a throw), so the merge
            // always finishes and RunCompleted fires — the reducer then sweeps any still-Running leaf.
            await Task.WhenAll(runs).ConfigureAwait(false);
            _diag?.Adapter("run complete");
            events.TryWrite(new AppEvent.RunCompleted());
        }
        finally { _gate.Release(); }
    }

    // --- Watch cycle (Phase 5, plan §9) ----------------------------------------

    /// <summary>The registered test projects' paths (populated during <see cref="DiscoverAsync"/>) — the
    /// set the watch coordinator intersects the dependents closure against.</summary>
    public IReadOnlyList<string> RegisteredTestProjectPaths => _registered.Select(p => p.ProjectPath).ToList();

    /// <summary>
    /// A watch cycle's build + re-discovery (plan §9, brief M4/M5): rebuild the affected test projects in
    /// PARALLEL (<paramref name="forceBuild"/> for source mode, since a dependency edit leaves the test
    /// project's own sources looking fresh; off for external mode, whose assemblies are already built), then
    /// for each project that built, re-discover through the persistent adapters (MTP host restarted on the
    /// rebuild) bracketed by <see cref="AppEvent.RediscoveryStarted"/>/<see cref="AppEvent.RediscoveryCompleted"/>
    /// so the reducer diffs the tree. Discovery is gated on build success — a compile error surfaces the
    /// Phase 3 error node and drops that project before discovery. Returns the projects that were re-discovered
    /// (the auto-rerun set). Shares the discovery/run gate so it never overlaps an in-flight run.
    /// </summary>
    public async Task<IReadOnlyList<string>> RediscoverAsync(
        IReadOnlyCollection<string> affectedProjectPaths, ChannelWriter<AppEvent> events,
        CancellationToken ct, bool forceBuild, bool noRestore = false, Watch.WatchLog? log = null, int cycle = 0)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var affected = _registered.Where(p => affectedProjectPaths.Contains(p.ProjectPath)).ToList();
            if (affected.Count == 0) return [];

            var built = await BuildProjectsAsync(affected, events, ct, force: forceBuild, noRestore: noRestore).ConfigureAwait(false);
            log?.Mark(cycle, Watch.WatchLog.BuildsDone);   // T2: parallel builds done
            var toRediscover = affected.Where(p => built.Contains(p.ProjectPath)).ToList();
            if (toRediscover.Count == 0) return [];   // every build failed → cycle stops before discovery

            var paths = toRediscover.Select(p => p.ProjectPath).ToList();
            events.TryWrite(new AppEvent.RediscoveryStarted(paths));
            await DiscoverProjectsAsync(toRediscover, events, ct, rediscover: true).ConfigureAwait(false);
            log?.Mark(cycle, Watch.WatchLog.DiscoverDone);   // T3: re-discovery done
            events.TryWrite(new AppEvent.RediscoveryCompleted(paths));
            return paths;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-evaluate the given projects (a <c>.csproj/.props/.targets</c> change, plan §9 — changed
    /// project only) and re-emit their <see cref="AppEvent.ProjectRegistered"/> so detection/TFM changes
    /// take effect. Best-effort: a project that fails to re-evaluate keeps its previous evaluation.</summary>
    public async Task ReevaluateAsync(
        IEnumerable<string> projectPaths, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        if (_evaluator is null) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var raw in projectPaths.Distinct(StringComparer.Ordinal))
            {
                var full = Path.GetFullPath(raw);
                var index = _registered.FindIndex(p => string.Equals(p.ProjectPath, full, StringComparison.Ordinal));
                if (index < 0) continue;   // not a registered test project — nothing to refresh
                try
                {
                    var reEval = _evaluator.Evaluate(full, _config?.OverrideFor(full));
                    // Keep --tfm (M1) honoured across a watch re-evaluation: re-applying the single-TFM filter
                    // so a .csproj edit can't silently re-introduce the other target frameworks mid-session.
                    if (_tfmFilter is not null)
                        reEval = reEval with { Tfms = reEval.Tfms
                            .Where(t => string.Equals(t.Tfm, _tfmFilter, StringComparison.OrdinalIgnoreCase)).ToList() };
                    if (reEval.Tfms.Count == 0) continue;   // the changed project no longer targets the filtered TFM
                    _registered[index] = reEval;
                    var (runner, notice) = Classify(reEval);
                    if (runner is not RunnerKind.NotATest)
                        events.TryWrite(new AppEvent.ProjectRegistered(
                            reEval.ProjectPath, reEval.DisplayName, reEval.TfmMonikers, runner, notice));
                }
                catch (Exception) { /* keep the previous evaluation; the rebuild will still run */ }
            }
        }
        finally { _gate.Release(); }
    }

    // --- Build + discover shared helpers ---------------------------------------

    /// <summary>Build the stale members of <paramref name="projects"/> in parallel; up-to-date projects pass
    /// through. Returns the set of project paths ready to discover/run (built OK or already fresh). When
    /// <paramref name="force"/> is set (watch mode A), EVERY project builds regardless of self-staleness —
    /// a cross-project source edit doesn't touch the dependent test project's own sources, so its output
    /// timestamp looks fresh even though a referenced library changed (the graph closure is the authority).</summary>
    private async Task<HashSet<string>> BuildProjectsAsync(
        IReadOnlyList<ProjectEvaluation> projects, ChannelWriter<AppEvent> events, CancellationToken ct,
        bool force = false, bool noRestore = false)
    {
        var ready = new HashSet<string>(StringComparer.Ordinal);
        if (_noBuild)
        {
            foreach (var p in projects) ready.Add(p.ProjectPath);
            return ready;   // --no-build: discover/run against existing binaries (plan §7)
        }

        var stale = force ? projects.ToList() : projects.Where(IsStale).ToList();
        foreach (var p in projects.Where(p => !stale.Contains(p))) ready.Add(p.ProjectPath);

        var results = await Task.WhenAll(stale.Select(async p =>
        {
            events.TryWrite(new AppEvent.BuildStarted(p.ProjectPath));
            _diag?.Build($"build {p.DisplayName} (noRestore={noRestore})");
            var outcome = await BuildService.BuildAsync(p.ProjectPath, ct, noRestore).ConfigureAwait(false);
            if (outcome.Success)
            {
                _diag?.Build($"build {p.DisplayName}: OK");
                events.TryWrite(new AppEvent.BuildSucceeded(p.ProjectPath));
            }
            else
            {
                _diag?.Build($"build {p.DisplayName}: FAILED ({outcome.Diagnostics.Count} diagnostic(s))");
                events.TryWrite(new AppEvent.BuildFailed(p.ProjectPath, outcome.Diagnostics, outcome.RawOutput));
            }
            return (p.ProjectPath, outcome.Success);
        })).ConfigureAwait(false);

        foreach (var (path, success) in results)
            if (success) ready.Add(path);
        return ready;
    }

    private static bool IsStale(ProjectEvaluation p) =>
        p.Tfms.Any(t => BuildService.IsStale(p.ProjectPath, t));

    /// <summary>Discover each project through its adapter, keeping the adapter live for runs. VSTest sources
    /// go through the one persistent wrapper; MTP gets a long-lived session per (project, TFM). When
    /// <paramref name="rediscover"/> is set (post-rebuild), a stale MTP session is torn down and replaced so
    /// the fresh binaries are served by a new host.</summary>
    private async Task DiscoverProjectsAsync(
        IReadOnlyList<ProjectEvaluation> projects, ChannelWriter<AppEvent> events, CancellationToken ct,
        bool rediscover = false)
    {
        var vstestSources = new List<VsTestSource>();
        var mtpTargets = new List<(ProjectEvaluation Proj, TfmEvaluation Tfm)>();

        foreach (var proj in projects)
            foreach (var tfm in proj.Tfms)
            {
                switch (tfm.Detection.Runner)
                {
                    case RunnerKind.VsTest:
                        if (File.Exists(tfm.OutputAssemblyPath))
                            vstestSources.Add(new VsTestSource(proj.ProjectPath, tfm.Tfm, tfm.OutputAssemblyPath));
                        break;
                    case RunnerKind.Mtp:
                        if (File.Exists(tfm.OutputAssemblyPath))
                            mtpTargets.Add((proj, tfm));
                        else
                            // e.g. a multi-TFM project whose secondary runtime isn't built in this env
                            // (net8.0 remains evaluate-only) — surface it, never a silently empty subtree.
                            events.TryWrite(new AppEvent.DiscoveryFailed(proj.ProjectPath,
                                proj.TfmMonikers.Count > 1 ? tfm.Tfm : null,
                                new NodeNotice(NoticeSeverity.Warning, "not built for this TFM",
                                    $"{Path.GetFileName(tfm.OutputAssemblyPath)} was not found — evaluate-only in this environment.")));
                        break;
                }
            }

        // VSTest discovery (one streaming call per unique-TFM group inside the wrapper).
        if (vstestSources.Count > 0)
        {
            var expected = vstestSources.Select(s => s.ProjectPath).ToHashSet(StringComparer.Ordinal);
            _vsTestConsole ??= SdkTools.FindVsTestConsole();
            if (_vsTestConsole is null)
                foreach (var path in expected)
                    events.TryWrite(new AppEvent.DiscoveryFailed(path, null,
                        new NodeNotice(NoticeSeverity.Error, "vstest.console not found",
                            "Could not locate vstest.console.dll in the active SDK.")));
            else
            {
                try
                {
                    if (_vsTest is null)
                    {
                        _vsTest = new VsTestDiscoverer(_vsTestConsole, _vsTestDiagPath);
                        if (_vsTestDiagPath is not null)
                            _diag?.Adapter($"vstest.console={Path.GetFileName(_vsTestConsole)}; diagnostics -> {_vsTestDiagPath}");
                    }
                    _diag?.Adapter($"vstest discover: {string.Join(", ", vstestSources.Select(s => Path.GetFileNameWithoutExtension(s.ProjectPath)))}");
                    await _vsTest.DiscoverAsync(vstestSources, events, counts =>
                    {
                        foreach (var path in expected)
                            if (counts.GetValueOrDefault(path) == 0)
                                events.TryWrite(ZeroTests(path));
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    foreach (var path in expected)
                        events.TryWrite(new AppEvent.DiscoveryFailed(path, null,
                            new NodeNotice(NoticeSeverity.Error, "VSTest discovery failed", ex.Message)));
                }
            }
        }

        // MTP discovery — one long-lived host per (project, TFM), fanned out in parallel and capped at
        // logical-CPU/2 concurrent sessions (plan §6.4) so a large solution doesn't launch every MTP host
        // at once. `_mtpSessions` is mutated from multiple targets concurrently, so all access to it below
        // is guarded by `sessionsLock` (Dictionary<> is not safe for concurrent structural changes even
        // across distinct keys).
        if (mtpTargets.Count > 0)
        {
            var sessionsLock = new object();
            using var throttle = new SemaphoreSlim(MtpDiscoveryConcurrency);

            await Task.WhenAll(mtpTargets.Select(async target =>
            {
                var (proj, tfm) = target;
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var key = SessionKey(proj.ProjectPath, tfm.Tfm);
                    MtpSession? old = null;
                    if (rediscover)
                        lock (sessionsLock)
                        {
                            if (_mtpSessions.TryGetValue(key, out old))
                                _mtpSessions.Remove(key);
                        }
                    if (old is not null)
                        await old.DisposeAsync().ConfigureAwait(false);   // staleness ⇒ rebuild ⇒ new process

                    bool alreadyAlive;
                    lock (sessionsLock) { alreadyAlive = _mtpSessions.ContainsKey(key); }
                    if (alreadyAlive) return;   // already discovered and alive

                    var multiTfm = proj.TfmMonikers.Count > 1;
                    var session = new MtpSession(proj.ProjectPath, tfm.OutputAssemblyPath, tfm.Tfm, multiTfm ? tfm.Tfm : null);
                    // A single MTP host's failure must not sink the whole backend — surface it as a notice and
                    // keep discovering the other projects (Phase 3 had this guard in the static discover path).
                    try
                    {
                        _diag?.Adapter($"mtp start {proj.DisplayName} ({tfm.Tfm})");
                        if (await session.StartAsync(events, ct).ConfigureAwait(false))
                        {
                            _diag?.Adapter($"mtp {proj.DisplayName} ({tfm.Tfm}) handshake serverInfo.version={session.ServerVersion ?? "?"}");
                            await session.DiscoverAsync(events, ct).ConfigureAwait(false);
                            _diag?.Adapter($"mtp {proj.DisplayName} ({tfm.Tfm}) discovery complete");
                            lock (sessionsLock) { _mtpSessions[key] = session; }
                        }
                        else
                        {
                            _diag?.Adapter($"mtp {proj.DisplayName} ({tfm.Tfm}) failed to hand shake — no host");
                            await session.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { await session.DisposeAsync().ConfigureAwait(false); throw; }
                    catch (Exception ex)
                    {
                        events.TryWrite(new AppEvent.DiscoveryFailed(proj.ProjectPath, multiTfm ? tfm.Tfm : null,
                            new NodeNotice(NoticeSeverity.Error, "MTP discovery failed", ex.Message)));
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);
        }
    }

    // --- Routing helpers --------------------------------------------------------

    /// <summary>Run an adapter task, swallowing non-cancellation failures so one adapter can't sink the
    /// merged run (cancellation still propagates so shutdown is prompt).</summary>
    private static async Task Guarded(Func<Task> run, CancellationToken ct)
    {
        try { await run().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception) { /* adapter error already surfaced; keep the merge alive */ }
    }

    private RunnerKind Runner(ProjectEvaluation p) => Classify(p).Runner;

    private static string SessionKey(string projectPath, string tfm) => $"{projectPath}|{tfm}";

    /// <summary>The adapter kind embedded in a derived case id (<c>adapterKind|projectPath|tfm|…</c>).</summary>
    private static string? AdapterOf(TestCaseId id)
    {
        var parts = id.Value.Split('|');
        return parts.Length >= 2 ? parts[0] : null;
    }

    /// <summary>The project path embedded in a derived case id.</summary>
    private static string? ProjectOf(TestCaseId id)
    {
        var parts = id.Value.Split('|');
        return parts.Length >= 3 ? parts[1] : null;
    }

    /// <summary>A project's single runner + note: MTP wins over VSTest wins over Unknown; the first
    /// non-null per-TFM notice is surfaced.</summary>
    private static (RunnerKind Runner, NodeNotice? Notice) Classify(ProjectEvaluation proj)
    {
        var runner = RunnerKind.NotATest;
        NodeNotice? notice = null;
        foreach (var tfm in proj.Tfms)
        {
            notice ??= tfm.Detection.Notice;
            runner = Max(runner, tfm.Detection.Runner);
        }
        return (runner, notice);
    }

    private static RunnerKind Max(RunnerKind a, RunnerKind b)
    {
        static int Rank(RunnerKind r) => r switch
        {
            RunnerKind.Mtp => 4,
            RunnerKind.VsTest => 3,
            RunnerKind.Unknown => 2,
            RunnerKind.Fake => 1,
            _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static AppEvent.DiscoveryFailed ZeroTests(string projectPath) =>
        new(projectPath, null, new NodeNotice(NoticeSeverity.Warning, "test project discovered zero tests",
            "The runner built and exited but produced no tests — a silent no-op configuration? (plan §6.2)"));

    private static string MessageFor(SolutionErrorKind kind) => kind switch
    {
        SolutionErrorKind.Malformed => "malformed solution file",
        SolutionErrorKind.MalformedXml => "malformed .slnx (XML)",
        SolutionErrorKind.NotFound => "solution not found",
        SolutionErrorKind.UnsupportedExtension => "unsupported solution format",
        _ => "solution error",
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _mtpSessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);
        _mtpSessions.Clear();
        if (_vsTest is not null) await _vsTest.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
