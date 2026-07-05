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
/// <see cref="VsTestDiscoverer"/> (§6.3) and MTP via one long-lived <see cref="MtpSession"/> per (project, TFM)
/// (§6.4), keeping those adapters ALIVE. <see cref="RunAsync"/> (Phase 4) routes a subset to the owning
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
    private readonly string? _logPath;

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
        string? logPath = null)
    {
        _projects = projects;
        _phantoms = phantoms;
        _solutionErrors = solutionErrors;
        _noBuild = noBuild;
        _logPath = logPath;
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

            // Guarded() converts a single adapter's failure into completion (not a throw), so the merge
            // always finishes and RunCompleted fires — the reducer then sweeps any still-Running leaf.
            await Task.WhenAll(runs).ConfigureAwait(false);
            events.TryWrite(new AppEvent.RunCompleted());
        }
        finally { _gate.Release(); }
    }

    // --- Build + discover shared helpers ---------------------------------------

    /// <summary>Build the stale members of <paramref name="projects"/> in parallel; up-to-date projects pass
    /// through. Returns the set of project paths ready to discover/run (built OK or already fresh).</summary>
    private async Task<HashSet<string>> BuildProjectsAsync(
        IReadOnlyList<ProjectEvaluation> projects, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var ready = new HashSet<string>(StringComparer.Ordinal);
        if (_noBuild)
        {
            foreach (var p in projects) ready.Add(p.ProjectPath);
            return ready;   // --no-build: discover/run against existing binaries (plan §7)
        }

        var stale = projects.Where(IsStale).ToList();
        foreach (var p in projects.Where(p => !stale.Contains(p))) ready.Add(p.ProjectPath);

        var results = await Task.WhenAll(stale.Select(async p =>
        {
            events.TryWrite(new AppEvent.BuildStarted(p.ProjectPath));
            var outcome = await BuildService.BuildAsync(p.ProjectPath, ct).ConfigureAwait(false);
            if (outcome.Success)
                events.TryWrite(new AppEvent.BuildSucceeded(p.ProjectPath));
            else
                events.TryWrite(new AppEvent.BuildFailed(p.ProjectPath, outcome.Diagnostics, outcome.RawOutput));
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
                    _vsTest ??= new VsTestDiscoverer(_vsTestConsole, _logPath);
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

        // MTP discovery — one long-lived host per (project, TFM).
        foreach (var (proj, tfm) in mtpTargets)
        {
            var key = SessionKey(proj.ProjectPath, tfm.Tfm);
            if (rediscover && _mtpSessions.TryGetValue(key, out var old))
            {
                await old.DisposeAsync().ConfigureAwait(false);   // staleness ⇒ rebuild ⇒ new process
                _mtpSessions.Remove(key);
            }
            if (_mtpSessions.ContainsKey(key)) continue;   // already discovered and alive

            var multiTfm = proj.TfmMonikers.Count > 1;
            var session = new MtpSession(proj.ProjectPath, tfm.OutputAssemblyPath, tfm.Tfm, multiTfm ? tfm.Tfm : null);
            // A single MTP host's failure must not sink the whole backend — surface it as a notice and
            // keep discovering the other projects (Phase 3 had this guard in the static discover path).
            try
            {
                if (await session.StartAsync(events, ct).ConfigureAwait(false))
                {
                    await session.DiscoverAsync(events, ct).ConfigureAwait(false);
                    _mtpSessions[key] = session;
                }
                else
                {
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
