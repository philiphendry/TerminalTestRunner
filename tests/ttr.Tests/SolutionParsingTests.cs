using Ttr.Build;
using Xunit;

namespace Ttr.Tests;

/// <summary>Solution parsing + target resolution (plan §4). Uses tiny on-disk .slnx/.sln + empty .csproj
/// files — pure parsing, NO restore/MSBuild. Covers the four-way error contract, the phantom pass, and
/// the scan/auto-select/error branches of target selection (brief M4).</summary>
public class SolutionParsingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ttrsln_" + Path.GetRandomFileName());

    public SolutionParsingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private string Write(string relPath, string content)
    {
        var full = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private string MakeProject(string name)
        => Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

    private static string Slnx(params string[] projectPaths) =>
        "<Solution>\n" + string.Join("\n", projectPaths.Select(p => $"  <Project Path=\"{p}\" />")) + "\n</Solution>\n";

    [Fact]
    public async Task Valid_slnx_resolves_existing_projects()
    {
        MakeProject("Alpha");
        MakeProject("Beta");
        var sln = Write("m.slnx", Slnx("Alpha/Alpha.csproj", "Beta/Beta.csproj"));

        var r = await SolutionParser.ParseAsync(sln);
        Assert.Null(r.Error);
        Assert.Equal(2, r.ProjectPaths.Count);
        Assert.Empty(r.Phantoms);
        Assert.All(r.ProjectPaths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task Phantom_project_becomes_a_warning_not_an_omission()
    {
        MakeProject("Alpha");
        var sln = Write("m.slnx", Slnx("Alpha/Alpha.csproj", "Ghost/Ghost.csproj"));

        var r = await SolutionParser.ParseAsync(sln);
        Assert.Null(r.Error);
        Assert.Single(r.ProjectPaths);
        var phantom = Assert.Single(r.Phantoms);
        Assert.Equal("Ghost", phantom.Name);
    }

    [Fact]
    public async Task Backslash_authored_paths_resolve_on_linux()
    {
        MakeProject("Alpha");
        var sln = Write("m.slnx", Slnx(@"Alpha\Alpha.csproj"));
        var r = await SolutionParser.ParseAsync(sln);
        Assert.Null(r.Error);
        Assert.Single(r.ProjectPaths);
    }

    [Fact]
    public async Task Missing_solution_is_NotFound()
    {
        var r = await SolutionParser.ParseAsync(Path.Combine(_dir, "nope.slnx"));
        Assert.Equal(SolutionErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task Unsupported_extension_is_reported()
    {
        var f = Write("thing.txt", "not a solution");
        var r = await SolutionParser.ParseAsync(f);
        Assert.Equal(SolutionErrorKind.UnsupportedExtension, r.Error!.Kind);
    }

    [Fact]
    public async Task Malformed_slnx_is_raw_xml_error()
    {
        var f = Write("bad.slnx", "<Solution><Project Path=\"x\"></Solution");   // broken XML
        var r = await SolutionParser.ParseAsync(f);
        Assert.Equal(SolutionErrorKind.MalformedXml, r.Error!.Kind);
    }

    [Fact]
    public async Task Garbage_sln_is_malformed()
    {
        var f = Write("bad.sln", "not really a solution file\n\0\0garbage");
        var r = await SolutionParser.ParseAsync(f);
        Assert.NotNull(r.Error);   // Malformed (or NotFound-style) — the point is it does not throw
        Assert.Equal(SolutionErrorKind.Malformed, r.Error!.Kind);
    }

    // --- Target resolution ------------------------------------------------------

    [Fact]
    public void Scan_prefers_solutions_over_bare_projects()
    {
        MakeProject("Alpha");
        Write("m.slnx", Slnx("Alpha/Alpha.csproj"));
        var candidates = TargetResolver.ScanCandidates(_dir);
        Assert.Single(candidates);
        Assert.EndsWith(".slnx", candidates[0]);
    }

    [Fact]
    public void Scan_returns_bare_projects_when_no_solution()
    {
        MakeProject("Alpha");
        // A .csproj sits in Alpha/ subdir, not top level — scan is top-level only, so none here.
        Assert.Empty(TargetResolver.ScanCandidates(_dir));
        // A top-level csproj is found.
        File.WriteAllText(Path.Combine(_dir, "Top.csproj"), "<Project/>");
        Assert.Single(TargetResolver.ScanCandidates(_dir));
    }

    [Fact]
    public async Task Resolve_expands_solution_and_anchors_primary()
    {
        MakeProject("Alpha");
        var sln = Write("m.slnx", Slnx("Alpha/Alpha.csproj"));
        var res = await TargetResolver.ResolveAsync([sln]);
        Assert.Equal(TargetOutcomeKind.Resolved, res.Kind);
        Assert.Equal(sln, res.Resolved!.PrimaryTargetPath);
        Assert.Single(res.Resolved.ProjectPaths);
    }

    [Fact]
    public async Task Resolve_bare_csproj_target()
    {
        var proj = MakeProject("Alpha");
        var res = await TargetResolver.ResolveAsync([proj]);
        Assert.Equal(TargetOutcomeKind.Resolved, res.Kind);
        Assert.Equal(proj, res.Resolved!.PrimaryTargetPath);
    }

    [Fact]
    public async Task Resolve_missing_csproj_is_exit_2()
    {
        var res = await TargetResolver.ResolveAsync([Path.Combine(_dir, "Nope.csproj")]);
        Assert.Equal(TargetOutcomeKind.Error, res.Kind);
        Assert.Equal(2, res.ExitCode);
    }

    [Fact]
    public async Task Resolve_solution_with_only_phantoms_is_exit_2()
    {
        var sln = Write("m.slnx", Slnx("Ghost/Ghost.csproj"));
        var res = await TargetResolver.ResolveAsync([sln]);
        Assert.Equal(TargetOutcomeKind.Error, res.Kind);
        Assert.Equal(2, res.ExitCode);
    }
}
