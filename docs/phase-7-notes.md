# Phase 7 notes — Packaging & polish (final v1 phase)

Phase 7 turns ttr into an installable v1: `--tfm`/`--log` finalised, exit codes swept to plan §3, capability
detection + ASCII fallback, `dotnet` global-tool packaging with a clean-machine install test, README + dev
notes, and a hardening sweep. One PR. `docs/ttr-phase-7-brief.md` is the task; `CLAUDE.md` is standing law.

## Milestone 0 — Phase 6 reconciliation

No contradictions. Verified the load-bearing feed-forwards:
- **State dir is target-relative, not tool-relative** — confirmed by the clean-machine install test: the
  installed tool, run from a neutral CWD (`/tmp`) against a fixture, wrote `.ttr/` beside the *fixture*, never
  beside the tool install. Packaging needs no special-casing.
- **`TTR_CRASH_SAVE_DIR`** kept env-gated and inert unless set; documented as internal in `docs/dev-notes.md`.
- **Serialised integration collection** — the installer test is a CI *shell* job (`scripts/install-test.sh`),
  not an xUnit test, so it does not share or race the `[Collection("integration")]` suite.

## Milestone 1 — Flag completion & CLI truth

- **`--tfm <moniker>`** is functional: it filters every project's evaluation to the one TFM (case-insensitive)
  so discovery/build/run all key off the pared-down set; a project not targeting it drops out. An unknown
  moniker for the target set → **exit 2** with the valid list. Composes with `--watch` (the filter is
  re-applied on watch re-evaluation, so a `.csproj` edit can't re-introduce other TFMs) and `--continue`. In
  `--fake` mode it is accepted and logged as ignored (the synthetic tree has no real TFMs), so the AC2 review
  command runs.
- **`--log <path>`** is the single diagnostics umbrella: one timestamped, category-prefixed file
  (`[SESSION]` lifecycle/targets/restore/save; `[BUILD]` invocations + outcomes; `[ADAPTER]` VSTest/MTP
  traffic; `[WATCH]` cycle timing). VSTest's own multi-line trace goes to a sibling `<path>.vstest.diag` with a
  pointer line in the umbrella. `TTR_WATCH_LOG` is folded in as the `WATCH` category and kept as a documented
  alias (its raw `<cycle> <stage> <iso>` format is preserved for `scripts/watch-latency.sh`).
- **Exit codes** swept to plan §3: `0` clean · `1` failing tests · `2` usage/target/**terminal-capability**
  refusal · `3` **build failure preventing any run** · `130` Ctrl+C. *Drift fixed:* the VT-enable failure
  previously returned `3`; a terminal-capability refusal is an environment error → now `2` (and M2's
  dumb/no-VT refusal shares it). `3` is now produced for real (a project build failed AND nothing ran). `--help`
  descriptions updated to the truth (no more "reserved").

## Milestone 2 — Capability detection + ASCII fallback (plan §11.2)

- `Capabilities.Detect` reads the VT-enable result + environment: **refuse** (exit 2, one line) on `TERM=dumb`
  or a Windows console where VT can't be enabled; else **ASCII** tier when the locale is non-UTF-8 (POSIX
  `LC_ALL`/`LC_CTYPE`/`LANG`, in precedence order — the .NET runtime reports UTF-8 for `Console.OutputEncoding`
  on Unix regardless, so it can't be the primary signal) or `TTR_ASCII=1`; else **full**. `NO_COLOR` disables
  colour on either glyph tier.
- The renderers keep emitting the full Unicode + colour frame; `Caps.Apply` is the single degrade chokepoint —
  a **whitelist** glyph transliteration (braille spinner → `|/-\`; `✓✗○⊘` → `+ x o s`; box/arrows/punctuation
  → ASCII; every mapped glyph is 1 cell → 1 cell, so invariant-5 width maths is preserved and **only ttr's own
  glyph vocabulary is touched, never user test names**) and a colour-code strip that keeps bold/dim/reverse/
  underline. The colourless stale overlay uses a `~` prefix (dim needs colour). `Caps.Full` is the identity, so
  every pre-Phase-7 frame stays byte-identical (verified — see AC6).
- **New ASCII snapshot lane** (`AsciiSnapshotTests`): the four main screens (tree, detail, modal, help) plus a
  guard that no Unicode glyph leaks and that full mode still shows `✓`/`✗`.

## Milestone 3 — Global tool packaging

`PackAsTool`, `ToolCommandName=ttr`, `PackageId=ttr`, `Version=1.0.0` (CI `--version-suffix ci.<run>` for
prerelease, clean tag version on release), MIT `PackageLicenseExpression` + `LICENSE`, README packed. CI:
`pack` uploads the `.nupkg` artifact; `release` (on a `v*` tag) packs the clean version and creates a GitHub
Release. **Decision recorded (not re-litigated):** the `Microsoft.TestPlatform.Portable` payload is **not**
shipped — D2 already requires the .NET 10 SDK, which carries `vstest.console` (Strategy A); the fallback
remains a documented code hook (`TTR_VSTEST_CONSOLE`). Stated in the README requirements section.

## Milestone 4 — The rename (owner input 1)

**Owner chose to keep the name `ttr`.** M4 therefore collapses to a grep-verify (no stale alternate
user-facing name exists; command/package id/`.ttr` state folder/`TTR_*` env vars are all consistent) — a
mechanical no-op, flagged here rather than silently skipped.

## Milestone 5 — Docs & demo

`README.md`: what-it-is + an ASCII screenshot, requirements (.NET 10 SDK; why Portable is not shipped),
install, quick start, the full §11.3 keymap, flags + exit codes, watch modes, `--continue` + state folder +
self-`.gitignore`, the supported-frameworks table **with the honest rows** (NUnit4-MTP upstream regression →
warning node; restored theory-row drop), and troubleshooting (`--log`, `TTR_ASCII`, VT/conhost/dumb). The
`?` help overlay is in parity with the README keymap (identical key set). **Demo cast:** `asciinema` is not
installable in the sandbox (no pypi/apt for it), so `scripts/record-demo.sh` reproduces the core-loop cast
(open → run → `f` → fix → vanish → watch); recording + the hosted asciinema.org link is a publish step (like
the Windows smoke), noted in the README.

## Milestone 6 — Cleanup & hardening

- **Path-scrubber flake FIXED at source** (not skipped): `BackendSnapshotTests.Backend_build_failure_detail`
  embedded an absolute machine path whose volatile prefix is truncated at different points on different
  machines (hence "/tmp-only"). The fake `backend` scenario's build-failure diagnostic now uses a stable,
  project-relative path (`src/Broken/Calculator.cs`), so the snapshot is deterministic everywhere. The single
  affected baseline was regenerated from the *real* renderer (a helper referencing the src DLLs), and the same
  helper confirmed the new ASCII baselines match the renderer exactly and that existing Unicode baselines are
  byte-identical.
- **TODO/HACK sweep:** none outstanding in `src`/`tests`.
- **Internal hooks documented** in `docs/dev-notes.md` (`TTR_CRASH`, `TTR_CRASH_SAVE_DIR/COUNT`,
  `TTR_VSTEST_CONSOLE`, `TTR_WATCH_LOG` alias). Deferred work moved to `docs/phase-8-backlog.md`.
- **Dependency audit clean:** `dotnet list package --vulnerable --include-transitive` reports no vulnerable
  packages for all shipped projects (ttr.Cli/Core/Ui/Runners/Build). The `Microsoft.Build.*` advisory remains
  neutralised by `ExcludeAssets="runtime"` (redirected to the SDK's copies at runtime) — restated in dev-notes.

## Milestone 7 — Windows/macOS closure

CI lanes (`.github/workflows/ci.yml`): the `test` lane runs build + the full unit/snapshot/integration suite +
fixture-matrix build on **ubuntu, windows, macos**; `install-test` runs the clean-machine install test on
**ubuntu + macos** (tmux-driven PTY); `over-width`/`dogfood`/`watch-latency` on Linux. Windows thus runs
build + full unit suite; the tmux-driven install/dogfood lanes are Linux + macOS. The **owner manual smoke
script** for Windows Terminal + legacy conhost is `docs/windows-smoke.md`.

## Acceptance criteria

| AC | Verdict | Evidence |
|---|---|---|
| **AC1** clean-machine install → discover → run → correct exit codes, SDK-resolved vstest from the installed tool | ✅ PASS (sandbox) / CI `install-test` | `scripts/install-test.sh` ran locally: installed 1.0.0 to an isolated tool-path, ran the installed tool from `/tmp` against the fixture matrix → discovered + ran; exit 1 (failures) / 0 (all-pass); `[ADAPTER] vstest.console=…` in the log proves Strategy A from the install path. |
| **AC2** `--tfm` + unified `--log`; help/exit codes match the plan | ✅ PASS | `--tfm net10.0` filters + unknown → exit 2 with valid list (verified); `--log` produces `[SESSION]/[BUILD]/[ADAPTER]/[WATCH]` + sibling `.vstest.diag` (verified); `--help` and exit codes swept to §3. |
| **AC3** ASCII fallback renders every main screen legibly; `TTR_ASCII=1` and non-UTF-8 locale trigger it; no-VT → refusal | ✅ PASS | tmux: `TTR_ASCII=1` and `LC_ALL=C` both render ASCII; `NO_COLOR` strips colour; `TERM=dumb` → refusal + exit 2. `AsciiSnapshotTests` (4 screens, baselines validated against the real renderer) + `CapabilitiesTests`/`CapsTests`. |
| **AC4** rename complete per M4 (or checklist if pending) | ✅ PASS (no-op) | Owner kept `ttr`; grep-verified consistent. |
| **AC5** newcomer follows the README cold | ⏳ owner (reviewer) | README quick-start is verbatim-runnable; the install test IS the quick-start executed in a fresh tool-path. Final "doc test" is the reviewer's cold run. |
| **AC6** full suite green in sandbox + CI; scrubber flake resolved; 203+ tests | ⏳ CI (asserted) | Scrubber flake fixed at source (deterministic). New tests: `AsciiSnapshotTests` (6), `CapabilitiesTests` (7), `CapsTests` (5), `ExitCodeTests` (5) → well over 203. The xUnit suite cannot run in the mounted-host sandbox (documented NuGet-restore leak, dev-notes) — CI is the authoritative lane; src builds clean and every snapshot delta was validated against the real renderer. |
| **AC7** owner Windows Terminal + conhost smoke recorded | ⏳ owner input 2 | Script: `docs/windows-smoke.md`. Result to be pasted below on completion. |
| **AC8** tagged RC produced by CI with the `.nupkg`; installing THAT is what AC1 tests | ✅ PASS (mechanism) | `pack` (every run) + `release` (on `v*` tag) jobs added; `dotnet pack` produces `ttr.1.0.0.nupkg` locally; AC1 installs from that feed. Tag `v1.0.0` on merge + owner sign-off. |

## Owner inputs

1. **Product name** — **provided: keep `ttr`.** M4 done.
2. **Windows Terminal + conhost smoke** — **pending.** Run `docs/windows-smoke.md` and paste the PASS/issues
   here. The PR is not "done" until this is recorded.

> AC7 result (owner): _pending_

## Deviations from the brief / plan

1. **Demo cast is a script, not a committed/hosted cast.** `asciinema` is not installable in the sandbox;
   `scripts/record-demo.sh` reproduces it and the hosted link is a publish step (parallels the Windows smoke).
2. **ASCII spinner uses the classic `|/-\` twirl**, not a literal static `*` as the brief illustrated — a
   static glyph wouldn't animate; the braille frames map onto the twirl so it still spins.
3. **`--log` VSTest diagnostics go to a sibling `.vstest.diag` file**, not inlined into the umbrella —
   vstest.console emits its own multi-line trace format that can't be cleanly category-prefixed; the umbrella
   carries a pointer line to it.
4. **The xUnit suite is CI-verified, not sandbox-verified** (Phase 4/6's documented mounted-host NuGet-restore
   leak; dev-notes). Every rendering change was validated against the real renderer via a helper referencing
   the src DLLs, and src builds clean; correctness of the test *code* rests on CI.

## Final supported-frameworks statement

xUnit v2 (VSTest), xUnit v3 (MTP), NUnit 3 classic (VSTest), MSTest.Sdk (MTP), TUnit (MTP) — **supported**.
NUnit 4 via MTP is **broken upstream** (host exits 134 pre-handshake); ttr's smoke validation surfaces it as a
warning node by design (tracked in `docs/phase-8-backlog.md`). Restored run-materialised theory rows drop on
`--continue` until a rerun re-materialises them (counted in the restore notice) — the one visible rough edge.

## Deliverables & merge

Code + packaging + docs (README, dev-notes, phase-8-backlog, windows-smoke, these notes). On merge + owner
Windows smoke sign-off: tag `v1.0.0`. The project's delivery phases are then complete.
