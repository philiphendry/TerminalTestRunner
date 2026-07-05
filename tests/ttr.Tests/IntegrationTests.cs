using System.Diagnostics;
using System.Threading.Channels;
using Ttr.Build;
using Ttr.Core;
using Ttr.Runners;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// Real end-to-end discovery against the fixture matrix (plan §12, brief M8): drive the actual VSTest /
/// MTP adapters at built fixture assemblies and assert discovered counts + tree shape. These need the
/// fixtures built (CI builds them first); when a fixture assembly is absent they Skip rather than fail,
/// so the pure suite stays green in environments that haven't built the matrix.
/// </summary>
[Collection("integration")]
public class IntegrationTests
{
    private static string? RepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\')))
            if (File.Exists(Path.Combine(dir, "ttr.sln"))) return dir;
        return null;
    }

    private static string? FixtureAssembly(string project)
    {
        var root = RepoRoot();
        if (root is null) return null;
        var dll = Path.Combine(root, "fixtures", "matrix", project, "bin", "Debug", "net10.0", $"{project}.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static string FixtureProject(string project) =>
        Path.Combine(RepoRoot()!, "fixtures", "matrix", project, $"{project}.csproj");

    /// <summary>Feed a discovery event stream through the reducer and return the built tree state.</summary>
    private static async Task<AppState> DiscoverThroughReducer(Func<ChannelWriter<AppEvent>, Task> discover)
    {
        var channel = Channel.CreateUnbounded<AppEvent>();
        await discover(channel.Writer);
        channel.Writer.Complete();
        var s = AppState.Initial("integration", runsEnabled: false);
        await foreach (var e in channel.Reader.ReadAllAsync())
            s = Reducer.Reduce(s, e);
        return s;
    }

    [Fact]
    public async Task XunitV2_vstest_discovers_expected_tree()
    {
        var dll = FixtureAssembly("XunitV2.Tests");
        if (dll is null) return;   // skip: fixture not built (run: dotnet build fixtures/matrix/XunitV2.Tests)

        var console = SdkTools.FindVsTestConsole();
        if (console is null) return;   // skip: vstest.console not found

        var proj = FixtureProject("XunitV2.Tests");
        var state = await DiscoverThroughReducer(async writer =>
        {
            // Register the project first (as the real backend does) so the display name + single-TFM
            // collapse are exercised, then stream real VSTest discovery into the same tree.
            writer.TryWrite(new AppEvent.ProjectRegistered(proj, "XunitV2.Tests", ["net10.0"], RunnerKind.VsTest));
            await using var d = new VsTestDiscoverer(console!);
            await d.DiscoverAsync([new VsTestSource(proj, "net10.0", dll!)], writer, null, default);
        });

        // 5 methods (Add_ReturnsSum, Divide_Works, Failing_Assertion, Skipped_Test, Add_Theory[3 rows]) = 7 leaves.
        Assert.Equal(7, state.TotalTests);
        var project = Assert.Single(state.Root.Children);
        Assert.Equal("XunitV2.Tests", project.Name);
        // Single-TFM → the TFM level is collapsed (namespace hangs off the project).
        Assert.Equal(TestNodeKind.Namespace, project.Children[0].Kind);
        // The theory materialised its 3 InlineData rows as Case children.
        var theory = FindNode(state.Root, n => n.Name == "Add_Theory");
        Assert.NotNull(theory);
        Assert.Equal(3, theory!.Children.Count);
    }

    [Fact]
    public async Task XunitV3_mtp_discovers_via_jsonrpc()
    {
        var dll = FixtureAssembly("XunitV3.Tests");
        if (dll is null) return;   // skip: fixture not built (run: dotnet build fixtures/matrix/XunitV3.Tests)

        var proj = FixtureProject("XunitV3.Tests");
        var before = ProcessSnapshot();
        var state = await DiscoverThroughReducer(async writer =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await MtpDiscoverer.DiscoverAsync(proj, dll!, "net10.0", null, writer, null, cts.Token);
        });

        Assert.True(state.TotalTests >= 3, $"expected ≥3 MTP tests, got {state.TotalTests}");
        var getSucceeds = FindNode(state.Root, n => n.Name == "Get_Succeeds");
        Assert.NotNull(getSucceeds);
        // The structured MTP location is wired as the default 'o' target.
        Assert.Contains(getSucceeds!.FileRefs, r => r.FileName == "ApiTests.cs");

        // No orphaned test hosts survive discovery (AC9).
        await Task.Delay(1000);
        Assert.True(ProcessSnapshot() <= before + 0, "an MTP test host was left running after discovery");
    }

    [Fact]
    public async Task Slnx_and_sln_resolve_the_same_project_set()
    {
        var root = RepoRoot();
        if (root is null) return;   // skip: repo root not found
        var slnx = await SolutionParser.ParseAsync(Path.Combine(root!, "fixtures", "matrix", "Matrix.slnx"));
        var sln = await SolutionParser.ParseAsync(Path.Combine(root!, "fixtures", "matrix", "Matrix.sln"));

        Assert.Null(slnx.Error);
        Assert.Null(sln.Error);
        var slnxProjects = slnx.ProjectPaths.Select(Path.GetFileName).OrderBy(x => x);
        var slnProjects = sln.ProjectPaths.Select(Path.GetFileName).OrderBy(x => x);
        Assert.Equal(slnProjects, slnxProjects);
        Assert.Contains(slnx.Phantoms, p => p.Name == "Ghost.Tests");   // the slnx carries the phantom
    }

    private static TestNode? FindNode(TestNode node, Func<TestNode, bool> match)
    {
        if (match(node)) return node;
        foreach (var c in node.Children)
            if (FindNode(c, match) is { } found) return found;
        return null;
    }

    /// <summary>Count live dotnet/testhost/vstest processes — a coarse orphan check (AC9).</summary>
    private static int ProcessSnapshot()
    {
        try
        {
            return Process.GetProcesses()
                .Count(p => SafeName(p) is "testhost" or "vstest.console" or "dotnet-exec");
        }
        catch { return 0; }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return ""; }
    }
}
