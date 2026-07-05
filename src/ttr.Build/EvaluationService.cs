using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Ttr.Core;

namespace Ttr.Build;

/// <summary>A project's full evaluation: display name + one entry per target framework (plan §6.2/§7).</summary>
public sealed record ProjectEvaluation(
    string ProjectPath,
    string DisplayName,
    IReadOnlyList<TfmEvaluation> Tfms)
{
    /// <summary>The distinct TFM monikers, in declared order (drives the tree's TFM level, brief M1).</summary>
    public IReadOnlyList<string> TfmMonikers => Tfms.Select(t => t.Tfm).ToList();

    /// <summary>True if any TFM is a runnable test project (drives whether it enters the tree).</summary>
    public bool IsTestProject => Tfms.Any(t => t.Signals.IsTestProject
        || t.Detection.Runner is RunnerKind.VsTest or RunnerKind.Mtp);
}

/// <summary>Per-(project,TFM) evaluation output: the built-assembly path, the Compile set (stored for the
/// Phase 5 watcher), the raw detection signals, and the §6.2 classification.</summary>
public sealed record TfmEvaluation(
    string Tfm,
    string OutputAssemblyPath,
    IReadOnlyList<string> CompileFiles,
    EvalSignals Signals,
    DetectionResult Detection);

/// <summary>
/// In-process MSBuild evaluation — NO target execution (plan §7). Evaluates each project per-TFM (passing
/// <c>TargetFramework</c> as a global property, since classification is only answerable per-TFM, never at
/// the cross-targeting level) and produces the §6.2 detection signals + output paths + Compile globs.
///
/// CLAUDE.md build rule: every method here references <c>Microsoft.Build.*</c> types, so the JIT resolves
/// them on first entry — this class must only ever be touched AFTER <c>MSBuildLocator.RegisterDefaults()</c>
/// has run in the entry point (see <c>Program.cs</c>). Evaluation reads package-provided props
/// (e.g. <c>Microsoft.Testing.Platform.MSBuild</c> sets <c>IsTestingPlatformApplication</c>), so the project
/// must be restored first; the build service (plan §7) sequences restore → evaluate → build → discover.
/// </summary>
public sealed class EvaluationService
{
    private static readonly string[] TrackedOptIns =
        ["UseMicrosoftTestingPlatformRunner", "EnableMicrosoftTestingPlatformRunner",
         "EnableMSTestRunner", "EnableNUnitRunner", "TestingPlatformDotnetTestSupport"];

    /// <summary>Evaluate one project across all its target frameworks. <paramref name="configOverride"/> is the
    /// per-project runner override read from <c>.ttr/config.json</c> (plan §6.2), or null.</summary>
    public ProjectEvaluation Evaluate(string projectPath, RunnerKind? configOverride = null)
    {
        var full = Path.GetFullPath(projectPath);
        var display = Path.GetFileNameWithoutExtension(full);
        var tfms = ReadTargetFrameworks(full);

        var results = new List<TfmEvaluation>(tfms.Count);
        foreach (var tfm in tfms)
            results.Add(EvaluateTfm(full, tfm, configOverride));

        return new ProjectEvaluation(full, display, results);
    }

    /// <summary>Read the declared TFM set with a single property-only evaluation (multi-target first).</summary>
    private static IReadOnlyList<string> ReadTargetFrameworks(string projectPath)
    {
        // A fresh collection per call keeps evaluations independent (the plan's changed-project-only
        // re-evaluation for watch attaches here); property-only, no TargetFramework global set yet.
        using var pc = new ProjectCollection();
        var project = Project.FromFile(projectPath, new ProjectOptions { ProjectCollection = pc });
        var multi = project.GetPropertyValue("TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(multi))
            return multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var single = project.GetPropertyValue("TargetFramework");
        return string.IsNullOrWhiteSpace(single) ? ["net10.0"] : [single];
    }

    private TfmEvaluation EvaluateTfm(string projectPath, string tfm, RunnerKind? configOverride)
    {
        using var pc = new ProjectCollection();
        var globals = new Dictionary<string, string> { ["TargetFramework"] = tfm };
        var project = Project.FromFile(projectPath,
            new ProjectOptions { ProjectCollection = pc, GlobalProperties = globals });

        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.GetItems("PackageReference"))
            packages.Add(item.EvaluatedInclude);

        var optIns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in TrackedOptIns)
            optIns[name] = IsTrue(project.GetPropertyValue(name));

        var signals = new EvalSignals(
            IsTestProject: IsTrue(project.GetPropertyValue("IsTestProject")),
            IsTestingPlatformApplication: IsTrue(project.GetPropertyValue("IsTestingPlatformApplication")),
            PackageReferences: packages,
            OptInFlags: optIns);

        var compile = new List<string>();
        foreach (var item in project.GetItems("Compile"))
        {
            var path = item.GetMetadataValue("FullPath");
            if (!string.IsNullOrEmpty(path)) compile.Add(path);
        }

        var output = project.GetPropertyValue("TargetPath");   // full path to the primary output assembly

        return new TfmEvaluation(tfm, output, compile, signals, Detection.Detect(signals, configOverride));
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
