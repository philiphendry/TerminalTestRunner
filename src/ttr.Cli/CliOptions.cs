using System.CommandLine;
using Ttr.Core;

namespace Ttr.Cli;

/// <summary>
/// The command surface (plan §3). Every flag is functional as of Phase 7: <c>--fake</c>, real targets,
/// <c>--no-build</c>, <c>--watch [build|external]</c>, <c>--continue</c>/<c>--state-dir</c>, and now
/// <c>--tfm</c> (single-TFM filter, M1) and <c>--log</c> (the diagnostics umbrella, M1).
/// </summary>
public sealed class CliOptions
{
    public Argument<string[]> Targets { get; } =
        new("targets") { Description = "Zero or more .csproj/.sln/.slnx paths (or a --fake scenario name).", Arity = ArgumentArity.ZeroOrMore };

    public Option<bool> Fake { get; } =
        new("--fake") { Description = "Run against the fake adapter. Scenario: default | big | flaky | slow | files." };

    public Option<int> FakeSeed { get; } =
        new("--fake-seed") { Description = "Deterministic RNG seed for --fake." };

    public Option<string?> Watch { get; } = new("--watch")
    {
        Description = "Watch mode: 'build' (default; rebuild on source change) or 'external' (react to builds you run).",
        Arity = ArgumentArity.ZeroOrOne,
    };
    public Option<bool> NoBuild { get; } = new("--no-build") { Description = "Discover/run against existing binaries; never build." };

    public Option<bool> Continue { get; } = new("--continue") { Description = "Restore the previous session (results, UI, expansion); changed results show as Stale." };
    public Option<string?> StateDir { get; } = new("--state-dir") { Description = "Override the .ttr/ state directory location." };

    public Option<string?> Tfm { get; } =
        new("--tfm") { Description = "Restrict evaluation/discovery/run to one target framework (e.g. net10.0)." };
    public Option<string?> Log { get; } =
        new("--log") { Description = "Write diagnostics (adapter traffic, builds, watch timing, session store) to <path>." };

    public RootCommand BuildRoot()
    {
        var root = new RootCommand("ttr — cross-platform terminal test runner & visualiser");
        root.Add(Targets);
        root.Add(Fake);
        root.Add(FakeSeed);
        root.Add(Continue);
        root.Add(Watch);
        root.Add(NoBuild);
        root.Add(Tfm);
        root.Add(StateDir);
        root.Add(Log);
        return root;
    }

    /// <summary>
    /// Whether an option was *explicitly* supplied. <c>GetResult</c> returns a result even for
    /// unsupplied options (carrying their default), so presence is detected via
    /// <see cref="OptionResult.Implicit"/> being false.
    /// </summary>
    public bool IsSupplied(ParseResult pr, Option option) =>
        pr.GetResult(option) is { Implicit: false };

    /// <summary>The requested single-TFM filter (M1), trimmed; null when <c>--tfm</c> was not supplied.</summary>
    public string? ResolveTfm(ParseResult pr)
    {
        if (!IsSupplied(pr, Tfm)) return null;
        var value = pr.GetValue(Tfm)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Resolve <c>--watch [build|external]</c> (plan §16): bare <c>--watch</c> = build/source mode.
    /// Returns <see cref="WatchKind.Off"/> when not supplied; an error message for an unknown mode.</summary>
    public (WatchKind Kind, string? Error) ResolveWatch(ParseResult pr)
    {
        if (!IsSupplied(pr, Watch)) return (WatchKind.Off, null);
        var value = pr.GetValue(Watch)?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" or "build" or "source" => (WatchKind.Build, null),
            "external" => (WatchKind.External, null),
            _ => (WatchKind.Off, $"unknown --watch mode '{value}'. Use 'build' (default) or 'external'."),
        };
    }
}
