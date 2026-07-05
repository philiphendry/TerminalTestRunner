using System.CommandLine;

namespace Ttr.Cli;

/// <summary>
/// The Phase-1 command surface (plan §3). Only <c>--fake [scenario] [--fake-seed]</c> does
/// anything this phase; the flags for later phases are *reserved* — defined so they parse, but
/// they reject with "not yet implemented" (exit 2). Everything else (real targets) exits 2 too.
/// </summary>
public sealed class CliOptions
{
    public Argument<string[]> Targets { get; } =
        new("targets") { Description = "Zero or more .csproj/.sln/.slnx paths (or a --fake scenario name).", Arity = ArgumentArity.ZeroOrMore };

    public Option<bool> Fake { get; } =
        new("--fake") { Description = "Run against the fake adapter. Scenario: default | big | flaky | slow | files." };

    public Option<int> FakeSeed { get; } =
        new("--fake-seed") { Description = "Deterministic RNG seed for --fake." };

    // --- Reserved for later phases (defined-but-rejecting) ---
    public Option<bool> Continue { get; } = new("--continue") { Description = "(reserved) Restore previous session." };
    public Option<string?> Watch { get; } = new("--watch") { Description = "(reserved) Watch mode.", Arity = ArgumentArity.ZeroOrOne };
    public Option<bool> NoBuild { get; } = new("--no-build") { Description = "(reserved) Never build." };
    public Option<string?> Tfm { get; } = new("--tfm") { Description = "(reserved) Restrict to one TFM." };
    public Option<string?> StateDir { get; } = new("--state-dir") { Description = "(reserved) Override .ttr/ location." };
    public Option<string?> Log { get; } = new("--log") { Description = "(reserved) Diagnostic log path." };

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
    /// True if any reserved flag was *explicitly* supplied. <c>GetResult</c> returns a result even
    /// for unsupplied options (carrying their default), so presence is detected via
    /// <see cref="OptionResult.Implicit"/> being false.
    /// </summary>
    public bool AnyReserved(ParseResult pr) =>
        IsSupplied(pr, Continue) || IsSupplied(pr, Watch)
        || IsSupplied(pr, Tfm) || IsSupplied(pr, StateDir);

    public bool IsSupplied(ParseResult pr, Option option) =>
        pr.GetResult(option) is { Implicit: false };
}
