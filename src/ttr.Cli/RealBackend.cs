using System.Threading.Channels;
using Ttr.Build;
using Ttr.Core;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>
/// The Phase 3 real backend: turns resolved targets into the same <see cref="AppEvent"/> stream the Fake
/// adapter produces (brief M1 says the backend must be invisible behind the event stream). It registers
/// evaluated projects (§6.2 detection), raises phantom / unparseable notices (§4), builds stale projects
/// out-of-process in parallel (§7), and streams discovered tests — VSTest via <see cref="VsTestDiscoverer"/>
/// (§6.3) and MTP via <see cref="MtpDiscoverer"/> (§6.4) — gating discovery on build success and surfacing
/// the §6.2 zero-tests smoke warning. No test execution (Phase 4).
/// </summary>
public sealed class RealBackend
{
    private readonly IReadOnlyList<ProjectEvaluation> _projects;
    private readonly IReadOnlyList<PhantomProject> _phantoms;
    private readonly IReadOnlyList<SolutionError> _solutionErrors;
    private readonly bool _noBuild;
    private readonly string? _logPath;

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

    public async Task RunAsync(ChannelWriter<AppEvent> events, CancellationToken ct)
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
        var registered = new List<ProjectEvaluation>();
        foreach (var proj in _projects)
        {
            var (runner, notice) = Classify(proj);
            if (runner is RunnerKind.NotATest) continue;
            events.TryWrite(new AppEvent.ProjectRegistered(
                proj.ProjectPath, proj.DisplayName, proj.TfmMonikers, runner, notice));
            registered.Add(proj);
        }

        // 3. Build stale projects in parallel (§7); discovery is gated on build success per project.
        var built = await BuildAsync(registered, events, ct).ConfigureAwait(false);

        // 4. Discover. VSTest sources go through one persistent wrapper; MTP hosts are per project.
        await DiscoverAsync(registered, built, events, ct).ConfigureAwait(false);
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

    private async Task<HashSet<string>> BuildAsync(
        IReadOnlyList<ProjectEvaluation> projects, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var built = new HashSet<string>(StringComparer.Ordinal);
        if (_noBuild)
        {
            foreach (var p in projects) built.Add(p.ProjectPath);
            return built;   // --no-build: discover against existing binaries (plan §7)
        }

        var stale = projects.Where(p => p.Tfms.Any(t => BuildService.IsStale(p.ProjectPath, t))).ToList();
        foreach (var p in projects.Where(p => !stale.Contains(p)))
            built.Add(p.ProjectPath);   // up to date → discover directly

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

        foreach (var (path, ok) in results)
            if (ok) built.Add(path);
        return built;
    }

    private async Task DiscoverAsync(
        IReadOnlyList<ProjectEvaluation> projects, HashSet<string> built,
        ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var vstestSources = new List<VsTestSource>();
        var mtpTargets = new List<(ProjectEvaluation Proj, TfmEvaluation Tfm)>();

        foreach (var proj in projects)
        {
            if (!built.Contains(proj.ProjectPath)) continue;   // build failed → its error node stands
            foreach (var tfm in proj.Tfms)
            {
                switch (tfm.Detection.Runner)
                {
                    case RunnerKind.VsTest:
                        if (File.Exists(tfm.OutputAssemblyPath))
                            vstestSources.Add(new VsTestSource(proj.ProjectPath, tfm.Tfm, tfm.OutputAssemblyPath));
                        break;
                    case RunnerKind.Mtp:
                        mtpTargets.Add((proj, tfm));
                        break;
                }
            }
        }

        var expectedVsTest = vstestSources.Select(s => s.ProjectPath).ToHashSet(StringComparer.Ordinal);

        if (vstestSources.Count > 0)
        {
            var console = SdkTools.FindVsTestConsole();
            if (console is null)
                foreach (var path in expectedVsTest)
                    events.TryWrite(new AppEvent.DiscoveryFailed(path, null,
                        new NodeNotice(NoticeSeverity.Error, "vstest.console not found",
                            "Could not locate vstest.console.dll in the active SDK.")));
            else
            {
                await using var discoverer = new VsTestDiscoverer(console, _logPath);
                await discoverer.DiscoverAsync(vstestSources, events, counts =>
                {
                    foreach (var path in expectedVsTest)
                        if (counts.GetValueOrDefault(path) == 0)
                            events.TryWrite(ZeroTests(path));
                }, ct).ConfigureAwait(false);
            }
        }

        // MTP discovery (plan §6.4) — one host per (project, TFM).
        foreach (var (proj, tfm) in mtpTargets)
        {
            var multiTfm = proj.TfmMonikers.Count > 1;
            await MtpDiscoverer.DiscoverAsync(
                proj.ProjectPath, tfm.OutputAssemblyPath, tfm.Tfm, multiTfm ? tfm.Tfm : null,
                events, _logPath, ct).ConfigureAwait(false);
        }
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
}
