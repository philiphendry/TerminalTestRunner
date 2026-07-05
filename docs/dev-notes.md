# ttr — developer / contributor notes

Companion to `README.md` (user-facing) and `docs/ttr-implementation-plan.md` (architecture + phase history).
This file records the internal knobs and build posture a contributor needs and a user should not.

## Building & testing

```bash
dotnet build ttr.sln -c Release
dotnet test  ttr.sln -c Release          # xUnit + Verify snapshot suite
dotnet pack  src/ttr.Cli -c Release -o ./artifacts
bash scripts/install-test.sh             # clean-machine global-tool install test (needs tmux)
```

CI (`.github/workflows/ci.yml`) runs, per OS:

| Lane | OS | What runs |
|---|---|---|
| `test` | ubuntu, windows, macos | build + full xUnit/snapshot/integration suite + fixture-matrix build |
| `over-width` | ubuntu | tmux assertion that no rendered line exceeds its pane |
| `dogfood` | ubuntu | real runs via tmux, asserts exit codes 0/1/130 |
| `watch-latency` | ubuntu | authoritative T0→T4 < 4 s watch-cycle bar |
| `pack` | ubuntu | packs a prerelease-suffixed `.nupkg`, uploads it as an artifact |
| `install-test` | ubuntu, macos | installs the packed tool from a local feed and runs it against the fixture matrix (AC1) |
| `release` | ubuntu (on `v*` tag) | packs the clean tag version and publishes a GitHub Release with the `.nupkg` |

Windows runs build + the full unit/snapshot/integration suite in the `test` lane; the install test is Linux +
macOS (tmux-driven), since the install test needs a PTY and macOS is the representative non-Windows Unix lane
alongside Linux. The Windows manual smoke (Windows Terminal + legacy conhost) is an owner step — see
`docs/phase-7-notes.md`.

## Internal environment hooks (NOT user-facing)

These exist for testing / diagnostics and are inert unless set. They are deliberately undocumented in the
README. None of them touches production behaviour when unset.

| Var | Purpose | Where |
|---|---|---|
| `TTR_CRASH=1` | Throw immediately after entering the alternate screen, to verify the terminal is always restored on a crash (try/finally). | `src/ttr.Cli/App.cs` |
| `TTR_CRASH_SAVE_DIR=<dir>` | Turns the process into a loop-save crash-safety harness: does one full session save into `<dir>` so `state.json` exists, prints `ready`, then loop-saves forever so a parent test can `kill -9` mid-write and assert the file still parses (POC-8 atomicity). Checked before `MSBuildLocator` and references no `Microsoft.Build` type, so it is safe pre-registration. | `src/ttr.Cli/SessionCrashHarness.cs` |
| `TTR_CRASH_SAVE_COUNT=<n>` | Bounds the loop-save harness (default 10 000). | `src/ttr.Cli/SessionCrashHarness.cs` |
| `TTR_VSTEST_CONSOLE=<path>` | Override the resolved `vstest.console` path (Strategy A). Escape hatch for an unusual SDK layout / for tests. | `src/ttr.Runners/SdkTools.cs` |
| `TTR_WATCH_LOG=<file>` | The dedicated per-cycle watch-timing file (raw `<cycle> <stage> <iso> ticks=` lines the latency harness parses). A documented **alias** for the `WATCH` category of `--log`; `--log` additionally receives those marks as category-prefixed lines. | `src/ttr.Cli/Watch/WatchLog.cs` |

`TTR_ASCII=1` is the one user-facing override (force the ASCII glyph tier) and IS documented in the README.

## Dependency audit posture

`dotnet list package --vulnerable --include-transitive` must be clean. One historical advisory is
**neutralised by design, not suppressed**: the `Microsoft.Build.*` family (compile-time type surface only,
plan §7) carries `ExcludeAssets="runtime"`, and `Microsoft.NET.StringTools` is `ExcludeAssets="runtime"` +
`PrivateAssets="all"`. `MSBuildLocator.RegisterDefaults()` redirects the whole MSBuild assembly family —
StringTools included — to the host SDK's copies at runtime, so the shipped-but-excluded versions are never
loaded and cannot conflict. `DisableMSBuildAssemblyCopyCheck` in `Directory.Build.props` documents that the
MSBL001 analyzer does not model this redirect. All package versions are pinned exactly via central package
management (`Directory.Packages.props`); a new dependency requires explicit approval in the PR.

## Sandbox NuGet-restore caveat (agent dev environment only)

In the mounted-host Linux sandbox, NuGet restore can leak the host's Windows package paths into
`obj/*.nuget.g.props` (`NETSDK1064` / "fallback package folder" errors), and it is worse for the
multi-project `tests/ttr.Tests` graph restore, which cannot be built there. This is a sandbox artifact only —
a clean CI / a real host restores natively. Do **not** commit any NuGet.config / restore-pin workaround (it
leaks onto the host mount and breaks the host build). The authoritative lane for the xUnit suite is CI.

## Where things live

See `CLAUDE.md` (repo layout + architecture invariants + binding technical rules) and
`docs/ttr-implementation-plan.md` (§-referenced design). Deferred work is in `docs/phase-8-backlog.md`.
