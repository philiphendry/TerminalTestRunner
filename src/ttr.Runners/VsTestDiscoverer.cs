using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Ttr.Core;

namespace Ttr.Runners;

/// <summary>A (project, TFM) whose built assembly should be discovered by VSTest.</summary>
public sealed record VsTestSource(string ProjectPath, string Tfm, string AssemblyPath);

/// <summary>
/// VSTest discovery via the translation layer (plan §6.3). Owns ONE persistent
/// <see cref="VsTestConsoleWrapper"/> for the process lifetime (recreating it costs 300–500 ms) and
/// discovers assemblies in a single streaming <c>DiscoverTests</c> call — batches arrive on
/// <see cref="ITestDiscoveryEventsHandler"/> and are mapped straight into <see cref="AppEvent.TestsDiscovered"/>.
/// Each <c>TestCase</c> becomes a derived <see cref="TestCaseId"/> (param display hash disambiguates theory
/// rows, whose FQNs collide) and the native <c>TestCase</c> is cached per id for the Phase 4 subset run.
/// No test code executes. <c>DOTNET_CLI_UI_LANGUAGE=en</c> is set on the spawned host (CLAUDE.md locale rule).
/// </summary>
public sealed class VsTestDiscoverer : IAsyncDisposable
{
    public const string AdapterKind = "vstest";

    private readonly VsTestConsoleWrapper _wrapper;
    private readonly ConcurrentDictionary<TestCaseId, TestCase> _cache = new();

    public VsTestDiscoverer(string vstestConsolePath, string? logPath = null)
    {
        var parameters = new ConsoleParameters
        {
            EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en" },
        };
        if (!string.IsNullOrEmpty(logPath)) parameters.LogFilePath = logPath;
        _wrapper = new VsTestConsoleWrapper(vstestConsolePath, parameters);
    }

    /// <summary>The native TestCase for a derived id (populated during discovery; used by the Phase 4 run).</summary>
    public IReadOnlyDictionary<TestCaseId, TestCase> Cache => _cache;

    /// <summary>Discover every source in one streaming call. <paramref name="onSourceCounts"/> reports the
    /// per-project discovered count so the caller can raise the §6.2 zero-tests smoke warning.</summary>
    public async Task DiscoverAsync(
        IReadOnlyList<VsTestSource> sources, ChannelWriter<AppEvent> events,
        Action<IReadOnlyDictionary<string, int>>? onSourceCounts, CancellationToken ct)
    {
        if (sources.Count == 0) return;

        var byAssembly = sources.ToDictionary(s => Path.GetFullPath(s.AssemblyPath), s => s, StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var handler = new DiscoveryHandler(byAssembly, _cache, events, counts);

        await Task.Run(() =>
        {
            _wrapper.StartSession();
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

    /// <summary>Map a discovered <c>TestCase</c> to a <see cref="TestIdentity"/> under its owning (project, TFM).</summary>
    internal static TestIdentity ToIdentity(TestCase tc, VsTestSource source)
    {
        var fqn = tc.FullyQualifiedName;
        var (ns, cls, method) = SplitFqn(fqn);

        // xUnit's default DisplayName is the full FQN for a plain test; theory rows append "(args…)".
        string? caseDisplay = null;
        var display = tc.DisplayName;
        if (!string.IsNullOrEmpty(display) && display != fqn)
        {
            caseDisplay = display.StartsWith(fqn, StringComparison.Ordinal)
                ? display[fqn.Length..].Trim()
                : display;
            if (caseDisplay.Length == 0) caseDisplay = display;
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
                var src = Resolve(tc.Source);
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

        private VsTestSource? Resolve(string? source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            return byAssembly.TryGetValue(Path.GetFullPath(source), out var s) ? s : null;
        }
    }
}
