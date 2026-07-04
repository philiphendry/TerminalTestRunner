using Ttr.Cli;
using Ttr.Runners;

// NOTE: MSBuildLocator.RegisterDefaults() is deliberately NOT here — Phase 1 has no MSBuild.
// When ttr.Build arrives (Phase 3), registration goes in this entry point and all Microsoft.Build
// usage stays behind a separate method boundary (CLAUDE.md build rule / plan §7).

var options = new CliOptions();
var root = options.BuildRoot();
root.SetAction(pr => Cli.Run(options, pr));
return root.Parse(args).Invoke();
