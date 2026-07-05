# ttr — Terminal Test Runner

**ttr** is a cross-platform .NET terminal (TUI) unit-test runner and visualiser. It discovers and runs
your tests through the real test-platform APIs — **VSTest** and **Microsoft.Testing.Platform (MTP)**, never
by scraping `dotnet test` output — and shows them in a live, navigable tree you drive from the keyboard.
Fix a failing test and watch it turn green (or vanish under the failed-only filter); edit a source file and
watch the affected tests re-run; quit and pick up exactly where you left off.

```
ttr · MySolution.sln · 214 tests · 3✗ 209✓ 2⊘ · watch: idle · [ft]
✗ MySolution.sln                                                    3✗ 209✓ 2⊘
  ✓ Core.Tests                                                          142✓
  ✗ Api.Tests                                                       3✗  67✓ 2⊘
    ✗ net10.0
      ✗ Api.Tests.Routing
        ✗ RoutesMatchInOrder                                              12 ms
        ✓ FallthroughIs404                                                 3 ms
  ↑↓ move · →← expand · s detail · f failed · r/R rerun · o open · ? help · q quit
```

*(A recorded [asciinema](https://asciinema.org) cast of the core loop — open → run → `f` → fix → vanish →
watch — is linked from the release notes; regenerate it with `scripts/record-demo.sh`.)*

## Requirements

- **.NET 10 SDK** on `PATH`. ttr resolves `vstest.console` from the active SDK directory (POC-1 "Strategy A"),
  so the SDK is a hard requirement — this is why the standalone `Microsoft.TestPlatform.Portable` payload is
  **not** shipped: the SDK you already have carries it. A runtime-only install is not enough.
- A VT-capable terminal (Windows Terminal, or conhost on Windows 10 1809+; any modern Unix terminal). A
  legacy no-VT console or `TERM=dumb` is refused with a one-line message rather than rendering garbage.

## Install

```bash
# From NuGet (once published):
dotnet tool install -g ttr

# From a local feed (e.g. the CI .nupkg artifact / a release candidate):
dotnet tool install -g ttr --add-source /path/to/feed
```

Update with `dotnet tool update -g ttr`; uninstall with `dotnet tool uninstall -g ttr`.

## Quick start

```bash
cd path/to/your/solution        # a dir with a .sln/.slnx, or a bare .csproj
ttr                             # scan the CWD, build stale test projects, discover + run

# or point at a target explicitly:
ttr MySolution.slnx
ttr tests/Unit.Tests/Unit.Tests.csproj

ttr --watch                     # re-run affected tests on every save
ttr --continue                  # restore the previous session (results, layout, filters)
```

Navigate with the arrow keys, press `s` for the detail pane, `f` to filter to failures, `r` to rerun the
selection, `o` to open a failing test's source file, and `q` to quit.

## Keymap (plan §11.3)

| Key | Context | Action |
|---|---|---|
| ↑/↓ (k/j) | tree | Move selection |
| →/← | tree | Expand / collapse (← on a leaf jumps to parent) |
| PgUp/PgDn, Home/End | tree/detail | Page / jump |
| `e` | tree | Toggle expand/collapse of the selected node |
| `E` (Shift+E) | tree | Toggle the node **and all descendants** |
| `f` | global | Toggle the failed-only filter |
| `s` | global | Toggle the detail pane |
| `b` | global | Detail pane right ↔ beneath |
| `w` | detail | Toggle word wrap in the detail pane |
| `t` | global | Toggle durations |
| `r` | tree | Rerun tests under the selected node (failed-only respects `f`) |
| `R` (Shift+R) | global | Rerun all (all-failed when `f` is active) |
| `o` | tree/detail | Open the first file reference of the selected result in a modal |
| `Tab` | global | Cycle focus tree ↔ detail |
| `?` | global | Help overlay |
| `q` / Ctrl+C | global | Quit (session state is saved) |
| Modal | `c`/Esc close · `n`/`p` next/prev file · `w` wrap · ↑↓/PgUp/PgDn scroll |

## Flags

```
ttr [<targets>...] [options]

<targets>            Zero or more .csproj / .sln / .slnx paths. None given → scan the CWD
                     top level (one candidate → auto-select; several → interactive picker).
--fake [<scenario>]  Run against the built-in fake adapter (no real tests). Scenarios:
                     default | big (10k) | flaky | slow | files. Composes with --watch/--continue.
--fake-seed <int>    Deterministic RNG seed for --fake.
--watch [<mode>]     Watch mode. <mode> = build (default; rebuild on source change) | external
                     (react to builds you run elsewhere). See "Watch modes" below.
--continue           Restore the previous session for these targets; changed results show as Stale.
--no-build           Never build; discover/run against existing binaries.
--tfm <moniker>      Restrict evaluation/discovery/run to one target framework (e.g. net10.0).
                     Unknown moniker for the target set → exit 2 with the valid list.
--state-dir <path>   Override the default .ttr/ state directory location.
--log <path>         Write diagnostics to <path>: one timestamped, category-prefixed file
                     ([SESSION]/[BUILD]/[ADAPTER]/[WATCH]). VSTest's own trace goes to a sibling
                     <path>.vstest.diag file.
-h, --help           Show help.
--version            Show the version.
```

### Exit codes

Scriptable even though it is a TUI: `0` clean exit · `1` exited with failing tests · `2` usage / target /
terminal-capability error · `3` a build failure prevented any run · `130` Ctrl+C.

## Watch modes

- **`--watch` (build/source mode, the default)** — watches your source files and re-evaluates only the
  changed project, rebuilds the affected test projects in parallel, re-discovers, diffs the tree, and
  re-runs the affected tests (respecting the `f` filter). Fires on save, before any build exists.
- **`--watch external`** — watches the test projects' output assemblies and reacts to builds you run
  elsewhere (in your IDE, a `dotnet watch`, etc.), one cycle per real build.

## `--continue` (session resume)

State is saved after every run and on clean exit, under a `.ttr/` folder **next to your primary target**
(the first `.sln`/`.slnx`, else the first `.csproj`) — never next to the installed tool, so a global-tool
install resumes correctly. `--state-dir` overrides the location (useful for read-only trees / CI).

On the next `ttr --continue`, ttr restores results, expansion, selection, filters, pane layout and scroll.
Results whose assembly has changed since they were recorded are shown **Stale** (a dimmed ✓/✗, or a `~`
prefix in a colourless terminal) until you re-run them. `.ttr/` is self-ignoring — ttr writes a
`.ttr/.gitignore` containing `*` on first use, so you do not need to touch your own `.gitignore`.

State is fingerprinted by absolute target path + content hash, so a moved checkout or a changed target set
degrades safely to a fresh session with a notice (no cross-machine / portable state).

## Supported frameworks

| Framework | Adapter | Status |
|---|---|---|
| xUnit v2 (+ `Microsoft.NET.Test.Sdk`) | VSTest | ✅ Supported |
| xUnit v3 | MTP | ✅ Supported |
| NUnit 3 (classic, NUnit3TestAdapter) | VSTest | ✅ Supported |
| MSTest.Sdk | MTP | ✅ Supported |
| TUnit | MTP | ✅ Supported |
| NUnit 4 via MTP | MTP | ⚠️ **Broken upstream** — see below |

**Honest rows:**

- **NUnit 4 via MTP** — NUnit 4.6.1's MTP host currently crashes on startup (exit 134, "adapter not
  registered") *before* any protocol handshake — a packaging regression versus what earlier testing
  observed. ttr's per-project smoke validation catches this by design and surfaces it as an explicit
  **warning node**, never a silently empty subtree. Track the upstream issue and re-test when NUnit / the
  adapter ship a fix. NUnit 3 classic (VSTest) and NUnit 4 under VSTest are unaffected.
- **Restored run-materialised theory rows** — theory rows that only appear via *run* events (e.g. xUnit
  `[MemberData]` / non-serialisable theory data) are not re-materialised by discovery, so on `--continue`
  their restored results have no leaf to attach to and are dropped (counted as "N gone" in the restore
  notice). The parent shows `NotRun` until you rerun it, which re-materialises the rows. This is the one
  visible rough edge of restore.

## Troubleshooting

- **Nothing renders / "terminal too small"** — ttr needs at least ~40×10 cells. Enlarge the window.
- **"This terminal does not support ANSI/VT sequences" (Windows) or "TERM=dumb"** — run ttr in a VT-capable
  terminal (Windows Terminal, or conhost on Windows 10 1809+). ttr refuses rather than spray escape codes.
- **Boxes/glyphs look like garbage** — your locale is not UTF-8. ttr auto-detects this and falls back to
  ASCII glyphs; force it with `TTR_ASCII=1`. `NO_COLOR=1` disables colour (Stale then shows as a `~` prefix).
- **Diagnose adapter / build / watch behaviour** — run with `--log /tmp/ttr.log` and inspect the
  category-prefixed lines. (`TTR_WATCH_LOG=<file>` remains as an alias for just the watch-cycle timing.)

## Building from source

```bash
dotnet build ttr.sln -c Release
dotnet test  ttr.sln -c Release
dotnet pack  src/ttr.Cli -c Release -o ./artifacts        # produces ttr.1.0.0.nupkg
```

See `docs/dev-notes.md` for contributor notes (internal test hooks, the dependency-audit posture, and the
sandbox NuGet-restore caveat). Architecture and phase history live in `docs/ttr-implementation-plan.md`.

## License

MIT — see [LICENSE](LICENSE).
