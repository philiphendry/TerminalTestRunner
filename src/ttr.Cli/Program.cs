using Microsoft.Build.Locator;
using Ttr.Cli;

// Test-only crash-safety harness (brief M6): if TTR_CRASH_SAVE_DIR is set, loop-save forever so a parent test
// can SIGKILL mid-write. Checked first (references no Microsoft.Build type) and never returns until killed.
if (SessionCrashHarness.TryRun()) return 0;

// CLAUDE.md build rule / plan §7: MSBuildLocator.RegisterDefaults() binds the tool to the host SDK's
// real MSBuild and MUST run in a method that references ZERO Microsoft.Build types — the JIT resolves
// a method's referenced types before its first line executes, so any Microsoft.Build use here would
// crash with "Microsoft.Build, Version=15.1.0.0 not found". This top-level Main touches only
// Microsoft.Build.Locator (a standalone shim); all Microsoft.Build use lives behind ttr.Build methods
// that are only JIT'd once Cli.Run dispatches into them (after this call).
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

var options = new CliOptions();
var root = options.BuildRoot();
root.SetAction(pr => Cli.Run(options, pr));
return root.Parse(args).Invoke();
