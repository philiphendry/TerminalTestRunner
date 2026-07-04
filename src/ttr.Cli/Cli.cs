using System.CommandLine;
using Ttr.Runners;

namespace Ttr.Cli;

/// <summary>Command dispatch: validate the parse and route to the app or an early exit code.</summary>
public static class Cli
{
    public static int Run(CliOptions o, ParseResult pr)
    {
        // Reserved flags reject regardless of --fake (plan §3 non-goals).
        if (o.AnyReserved(pr))
        {
            Console.Error.WriteLine(
                "not yet implemented: --continue/--watch/--no-build/--tfm/--state-dir/--log arrive in a later phase.");
            return 2;
        }

        var positionals = pr.GetValue(o.Targets) ?? [];

        if (!pr.GetValue(o.Fake))
        {
            Console.Error.WriteLine("real targets arrive in Phase 3 — run with `--fake [scenario]` for now.");
            return 2;
        }

        var scenario = positionals.Length > 0 ? positionals[0] : "default";
        if (!ScenarioBuilder.IsKnown(scenario))
        {
            Console.Error.WriteLine(
                $"unknown fake scenario '{scenario}'. Known scenarios: default, big, flaky, slow.");
            return 2;
        }

        var seed = pr.GetValue(o.FakeSeed);
        return App.Run(scenario, seed);
    }
}
