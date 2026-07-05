using Ttr.Core;

namespace Ttr.Build;

/// <summary>
/// The per-(project,TFM) MSBuild signals detection keys on (plan §6.2). Populated by
/// <see cref="EvaluationService"/> from a real evaluation; <see cref="Detection"/> classifies them.
/// Package ids and property names are compared case-insensitively.
/// </summary>
public sealed record EvalSignals(
    bool IsTestProject,
    bool IsTestingPlatformApplication,
    IReadOnlySet<string> PackageReferences,
    IReadOnlyDictionary<string, bool> OptInFlags);

/// <summary>The classification outcome: the runner plus an optional user-facing note.</summary>
public sealed record DetectionResult(RunnerKind Runner, NodeNotice? Notice = null);

/// <summary>
/// The §6.2 V2 detection rule, verbatim and PURE (settled by POC-3, hardened by POC-9). Keyed on
/// <c>IsTestingPlatformApplication</c> per-TFM as the single authoritative MTP signal; the underlying
/// opt-in trigger is recorded for diagnostics only, never for the decision. Dead opt-in flags on a
/// classic VSTest project are surfaced as an "incomplete MTP migration?" warning (POC-3's correction of
/// the original "both signals → prefer MTP" rule, which misclassified real migration-in-progress
/// projects onto a protocol their binaries don't speak). A per-project override in <c>.ttr/config.json</c>
/// wins outright when present. No MSBuild types here — this is unit-tested without any evaluation.
/// </summary>
public static class Detection
{
    /// <summary>Classic VSTest adapter package ids (lowercased).</summary>
    private static readonly string[] ClassicAdapters =
        ["xunit.runner.visualstudio", "nunit3testadapter", "mstest.testadapter"];

    /// <summary>Raw MTP opt-in property names — set true here despite IsTestingPlatformApplication=false is dead.</summary>
    private static readonly string[] MtpOptInFlags =
        ["UseMicrosoftTestingPlatformRunner", "EnableMSTestRunner", "EnableNUnitRunner",
         "TestingPlatformDotnetTestSupport"];

    public static DetectionResult Detect(EvalSignals s, RunnerKind? configOverride = null)
    {
        if (configOverride is { } forced)
            return new DetectionResult(forced,
                new NodeNotice(NoticeSeverity.Info, $"runner overridden to {forced} via .ttr/config.json"));

        // 1. IsTestingPlatformApplication is authoritative → MTP.
        if (s.IsTestingPlatformApplication)
        {
            var alsoClassic = HasAnyClassicAdapter(s);
            return new DetectionResult(RunnerKind.Mtp,
                alsoClassic
                    ? new NodeNotice(NoticeSeverity.Info,
                        "dual-mode: a classic VSTest adapter is also present",
                        "Both the MTP and VSTest paths work; ttr drives MTP.")
                    : null);
        }

        // 2. Test.Sdk + a classic adapter → VSTest.
        if (s.PackageReferences.Contains("microsoft.net.test.sdk") && HasAnyClassicAdapter(s))
        {
            var deadFlag = FirstDeadOptIn(s);
            return new DetectionResult(RunnerKind.VsTest,
                deadFlag is null
                    ? null
                    : new NodeNotice(NoticeSeverity.Warning, "incomplete MTP migration?",
                        $"{deadFlag}=true but no referenced package reads it (IsTestingPlatformApplication " +
                        "is false) — this project runs pure VSTest. Remove the dead opt-in or finish the migration."));
        }

        // 3. Test-shaped but no recognised runner → Unknown, warn.
        if (s.IsTestProject)
            return new DetectionResult(RunnerKind.Unknown,
                new NodeNotice(NoticeSeverity.Warning, "test-shaped project, no recognised runner",
                    "IsTestProject=true but no xUnit/NUnit/MSTest/TUnit adapter was found."));

        // 4. Not a test project.
        return new DetectionResult(RunnerKind.NotATest);
    }

    private static bool HasAnyClassicAdapter(EvalSignals s)
    {
        foreach (var a in ClassicAdapters)
            if (s.PackageReferences.Contains(a)) return true;
        return false;
    }

    /// <summary>The first raw MTP opt-in property set true — the dead-flag diagnostic name (original casing).</summary>
    private static string? FirstDeadOptIn(EvalSignals s)
    {
        foreach (var flag in MtpOptInFlags)
            if (s.OptInFlags.TryGetValue(flag, out var value) && value) return flag;
        // The dictionary may carry the property under different casing (MSBuild is case-insensitive).
        foreach (var (name, value) in s.OptInFlags)
            if (value && MtpOptInFlags.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        return null;
    }
}
