using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Ttr.Core;
using CoreOutcome = Ttr.Core.TestOutcome;
using VsOutcome = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome;

namespace Ttr.Runners;

/// <summary>A (project, TFM) whose built assembly should be discovered by VSTest.</summary>
public sealed record VsTestSource(string ProjectPath, string Tfm, string AssemblyPath);

/// <summary>
/// VSTest discovery AND execution via the translation layer (plan §6.3). Owns ONE persistent
/// <see cref="VsTestConsoleWrapper"/> for the process lifetime (recreating it costs 300–500 ms; it is
/// reused across discovery and every subsequent run — brief M2) and a cache of the native
/// <c>TestCase</c> per derived <see cref="TestCaseId"/> so a subset run passes real objects.
/// <list type="bullet">
/// <item>Discovery batches (<see cref="ITestDiscoveryEventsHandler"/>) map straight into
///   <see cref="AppEvent.TestsDiscovered"/>. Each <c>TestCase</c> becomes a derived id (param display hash
///   disambiguates theory rows, whose FQNs collide).</item>
/// <item>Run stats (<see cref="ITestRunEventsHandler"/>): <c>ActiveTests</c> → <see cref="AppEvent.TestStarted"/>
///   (the spinner signal); <c>NewTestResults</c> → <see cref="AppEvent.TestFinished"/> with the full
///   <see cref="TestResultDetail"/> (message/stack/stdout/duration). A non-serialisable theory discovers as
///   ONE case (display == FQN → the Method leaf) and the run reports N rows with distinct display names →
///   the reducer materialises N Case children (the 1→N shape, brief M2/AC4).</item>
/// <item>Cancellation is <c>AbortTestRun()</c> (~25 ms) — NEVER <c>CancelTestRun()</c> (18 s with xUnit v2,
///   CLAUDE.md). The reducer's <see cref="AppEvent.RunCompleted"/> sweep flips any still-Running leaf to
///   NotRun so an aborted run leaves no phantom spinner.</item>
/// </list>
/// <c>DOTNET_CLI_UI_LANGUAGE=en</c> is set on the spawned host (CLAUDE.md locale rule).
/// </summary>
public sealed class VsTestDiscoverer : IAsyncDisposable
{
    public const string AdapterKind = "vstest";

    private readonly VsTestConsoleWrapper _wrapper;
    private readonly ConcurrentDictionary<TestCaseId, TestCase> _cache = new();
    // Assembly full path → its (project, TFM), accumulated during discovery so the run handler can place
    // results (and run-all can address whole sources) without re-deriving.
    private readonly ConcurrentDictionary<string, VsTestSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sessionGate = new();
    private bool _sessionStarted;

    public VsTestDiscoverer(string vstestConsolePath, string? logPath = null)
    {
        var parameters = new ConsoleParameters
        {
            EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en" },
        };
        if (!string.IsNullOrEmpty(logPath)) parameters.LogFilePath = logPath;
        _wrapper = new VsTestConsoleWrapper(vstestConsolePath, parameters);
    }

    /// <summary>The native TestCase for a derived id (populated during discovery; used by the subset run).</summary>
    public IReadOnlyDictionary<TestCaseId, TestCase> Cache => _cache;

    /// <summary>Start the shared vstest.console session exactly once (reused for discovery + all runs).</summary>
    private void EnsureSession()
    {
        if (_sessionStarted) return;
        lock (_sessionGate)
        {
            if (_sessionStarted) return;
            _wrapper.StartSession();
            _sessionStarted = true;
        }
    }

    /// <summary>Discover every source in one streaming call. <paramref name="onSourceCounts"/> reports the
    /// per-project discovered count so the caller can raise the §6.2 zero-tests smoke warning.</summary>
    public async Task DiscoverAsync(
        IReadOnlyList<VsTestSource> sources, ChannelWriter<AppEvent> events,
        Action<IReadOnlyDictionary<string, int>>? onSourceCounts, CancellationToken ct)
    {
        if (sources.Count == 0) return;

        foreach (var s in sources) _sources[Path.GetFullPath(s.AssemblyPath)] = s;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var handler = new DiscoveryHandler(_sources, _cache, events, counts);

        await Task.Run(() =>
        {
            EnsureSession();
            // The RunConfiguration TargetFramework must match the assemblies, or the test host rejects
            // them as "not valid"; group sources by TFM so each DiscoverTests call carries the right one.
            foreach (var group in sources.GroupBy(s => s.Tfm))
            {
                var assemblies = group.Select(s => Path.GetFullPath(s.AssemblyPath)).ToList();
                _wrapper.DiscoverTests(assemblies, RunSettings(group.Key), handler);
            }
        }, ct).ConfigureAwait(false);

        onSourceCounts?.Invoke(counts);
    }

    /// <summary>
    /// Run a subset by derived id (empty = every discovered VSTest source). Cases resolve to their cached
    /// native <c>TestCase</c>; run-all addresses whole assemblies (brief M2). Grouped by TFM so each run
    /// carries a matching <c>TargetFrameworkVersion</c>. Cancellation aborts the in-flight run.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<TestCaseId> subset, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var handler = new RunHandler(_sources, events);

        // ct → AbortTestRun (fast, ~25 ms). The blocking RunTests then returns with IsAborted=true; the
        // reducer sweeps any still-Running leaf to NotRun (no phantom spinners).
        await using var _ = ct.Register(() => { try { _wrapper.AbortTestRun(); } catch (Exception) { } });

        await Task.Run(() =>
        {
            EnsureSession();
            if (subset.Count == 0)
            {
                foreach (var group in _sources.Values.GroupBy(s => s.Tfm))
                {
                    var assemblies = group.Select(s => Path.GetFullPath(s.AssemblyPath)).Distinct().ToList();
                    _wrapper.RunTests(assemblies, RunSettings(group.Key), handler);
                }
                return;
            }

            // Subset: only the ids we actually discovered as VSTest cases (the caller may pass a mixed set).
            var cases = subset
                .Select(id => _cache.TryGetValue(id, out var tc) ? (tc, id) : default)
                .Where(x => x.tc is not null)
                .ToList();
            foreach (var group in cases.GroupBy(x => TfmOf(x.tc)))
            {
                var testCases = group.Select(x => x.tc).ToList();
                _wrapper.RunTests(testCases, RunSettings(group.Key), handler);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The TFM registered for a discovered case's source assembly (falls back to net10.0).</summary>
    private string TfmOf(TestCase tc) =>
        _sources.TryGetValue(Path.GetFullPath(tc.Source), out var s) ? s.Tfm : "net10.0";

    /// <summary>Minimal design-mode run settings pinned to the target framework of the assemblies.</summary>
    private static string RunSettings(string tfm) =>
        $"<RunSettings><RunConfiguration><DesignMode>true</DesignMode>" +
        $"<TargetFrameworkVersion>{System.Security.SecurityElement.Escape(tfm)}</TargetFrameworkVersion>" +
        $"</RunConfiguration></RunSettings>";

    public ValueTask DisposeAsync()
    {
        try { _wrapper.EndSession(); }
        catch (Exception) { /* best-effort teardown */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>Map a discovered <c>TestCase</c> to a <see cref="TestIdentity"/> under its owning (project, TFM).
    /// Uses <paramref name="displayName"/> (the DISCOVERY display) to derive the theory-row label.</summary>
    internal static TestIdentity ToIdentity(TestCase tc, VsTestSource source) =>
        ToIdentity(tc, tc.DisplayName, source);

    /// <summary>Map a case using an explicit display name — at RUN time the per-result display carries the
    /// row (e.g. <c>FQN(a: 1, b: 2)</c>) even when the discovered TestCase's display was just the FQN.</summary>
    internal static TestIdentity ToIdentity(TestCase tc, string? displayName, VsTestSource source)
    {
        var fqn = tc.FullyQualifiedName;
        var (ns, cls, method) = SplitFqn(fqn);

        // xUnit's default DisplayName is the full FQN for a plain test; theory rows append "(args…)".
        string? caseDisplay = null;
        if (!string.IsNullOrEmpty(displayName) && displayName != fqn)
        {
            caseDisplay = displayName.StartsWith(fqn, StringComparison.Ordinal)
                ? displayName[fqn.Length..].Trim()
                : displayName;
            if (caseDisplay.Length == 0) caseDisplay = displayName;
        }

        var id = TestCaseId.ForCase(AdapterKind, source.ProjectPath, source.Tfm, fqn, caseDisplay);
        return new TestIdentity(id, source.ProjectPath, source.Tfm, ns, cls, method, caseDisplay);
    }

    /// <summary>Split <c>Namespace.Class.Method</c>; tolerates missing namespace / nested types by taking the
    /// last segment as the method and the segment before it as the class.</summary>
    internal static (string Namespace, string Class, string Method) SplitFqn(string fqn)
    {
        var lastDot = fqn.LastIndexOf('.');
        if (lastDot < 0) return ("", "", fqn);
        var method = fqn[(lastDot + 1)..];
        var type = fqn[..lastDot];
        var typeDot = type.LastIndexOf('.');
        return typeDot < 0 ? ("", type, method) : (type[..typeDot], type[(typeDot + 1)..], method);
    }

    private sealed class DiscoveryHandler(
        IReadOnlyDictionary<string, VsTestSource> byAssembly,
        ConcurrentDictionary<TestCaseId, TestCase> cache,
        ChannelWriter<AppEvent> events,
        Dictionary<string, int> counts) : ITestDiscoveryEventsHandler
    {
        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
        {
            if (discoveredTestCases is null) return;
            var batch = new List<TestIdentity>();
            foreach (var tc in discoveredTestCases)
            {
                var src = Resolve(byAssembly, tc.Source);
                if (src is null) continue;
                var identity = ToIdentity(tc, src);
                cache[identity.Id] = tc;
                batch.Add(identity);
                counts[src.ProjectPath] = counts.GetValueOrDefault(src.ProjectPath) + 1;
            }
            if (batch.Count > 0) events.TryWrite(new AppEvent.TestsDiscovered(batch));
        }

        public void HandleDiscoveryComplete(long totalTests, IEnumerable<TestCase>? lastChunk, bool isAborted)
            => HandleDiscoveredTests(lastChunk);

        public void HandleLogMessage(TestMessageLevel level, string? message) { }
        public void HandleRawMessage(string rawMessage) { }
    }

    /// <summary>Receives run stats: active tests → started/spinner; new results → finished with detail.
    /// Result identities are derived from the per-result display name so theory rows land on distinct Case
    /// leaves; the reducer promotes a Method leaf to a branch on the first row (the 1→N shape).</summary>
    private sealed class RunHandler(
        IReadOnlyDictionary<string, VsTestSource> byAssembly,
        ChannelWriter<AppEvent> events) : ITestRunEventsHandler
    {
        public void HandleTestRunStatsChange(TestRunChangedEventArgs? stats)
        {
            if (stats is null) return;
            if (stats.ActiveTests is not null)
                foreach (var tc in stats.ActiveTests)
                    if (Resolve(byAssembly, tc.Source) is { } src)
                        events.TryWrite(new AppEvent.TestStarted(ToIdentity(tc, tc.DisplayName, src)));
            EmitResults(stats.NewTestResults);
        }

        public void HandleTestRunComplete(
            TestRunCompleteEventArgs testRunCompleteArgs, TestRunChangedEventArgs? lastChunkArgs,
            ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
            => EmitResults(lastChunkArgs?.NewTestResults);

        public void HandleLogMessage(TestMessageLevel level, string? message) { }
        public void HandleRawMessage(string rawMessage) { }
        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => -1;

        private void EmitResults(IEnumerable<TestResult>? results)
        {
            if (results is null) return;
            foreach (var r in results)
            {
                var src = Resolve(byAssembly, r.TestCase.Source);
                if (src is null) continue;
                var identity = ToIdentity(r.TestCase, r.DisplayName ?? r.TestCase.DisplayName, src);
                events.TryWrite(new AppEvent.TestFinished(identity, MapOutcome(r.Outcome), r.Duration, BuildDetail(r)));
            }
        }

        private static CoreOutcome MapOutcome(VsOutcome o) => o switch
        {
            VsOutcome.Passed => CoreOutcome.Passed,
            VsOutcome.Failed => CoreOutcome.Failed,
            _ => CoreOutcome.Skipped,   // Skipped / None / NotFound render as skipped
        };

        /// <summary>Detail is attached for failures only (parity with the fake adapter; a pass/skip clears
        /// stale failure detail in the reducer so vanish-on-pass stays honest).</summary>
        private static TestResultDetail? BuildDetail(TestResult r)
        {
            if (r.Outcome != VsOutcome.Failed) return null;
            string? stdout = null;
            foreach (var m in r.Messages)
                if (m.Category == TestResultMessage.StandardOutCategory)
                    stdout = (stdout ?? "") + m.Text;
            return new TestResultDetail(
                Message: r.ErrorMessage,
                ExceptionChain: null,
                StackTrace: r.ErrorStackTrace,
                StandardOutput: string.IsNullOrEmpty(stdout) ? null : stdout);
        }
    }

    private static VsTestSource? Resolve(IReadOnlyDictionary<string, VsTestSource> byAssembly, string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        return byAssembly.TryGetValue(Path.GetFullPath(source), out var s) ? s : null;
    }
}
