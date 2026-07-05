# Phase 5 notes — Watch Mode

Phase 5 adds `--watch`: react to source changes (mode A, default) or externally-produced builds (mode B) by
rebuilding affected projects in parallel, re-discovering, **diffing the tree by `TestCaseId`**, and
auto-rerunning the affected set — while the user keeps navigating. Fake mode, the Phase 2 UX contract, and
every prior snapshot are unchanged (the watch header segment appears ONLY in watch mode).

## Milestone 0 — sandbox rebuild-flake reconciliation (outcome recorded)

**Reproduced, root-caused, and bounded — no workaround committed.** The sandbox NuGet mount leak is real but
**does not affect watch mode's in-app rebuild**:

- The leak fires only on the **central-package-managed, multi-project _graph_ restore** of ttr's own
  solution: `dotnet build ttr.sln` / `tests/ttr.Tests` bakes the host's Windows fallback-package paths
  (`C:\Program Files (x86)\…\NuGetPackages`) into `obj/project.assets.json` → `MSB4018 / NETSDK1064`. This is
  unchanged from Phase 4 and is a sandbox-only artifact (clean CI restores natively).
- **Watch mode rebuilds the _user's_ projects with a per-project `dotnet build`**, which restores natively and
  is stable. Verified: repeated edit→rebuild cycles of a matrix fixture AND of a test project **with a
  `ProjectReference`** (the watch shape) all succeed in-sandbox with zero flake. The isolation is because the
  target/fixture projects opt out of central package management (empty `Directory.Build.props` +
  `ManagePackageVersionsCentrally=false`), so their restore uses inline versions and clean Linux paths.

Conclusion: no root fix needed for watch; nothing committed (per the Phase 2/3/4 lesson — keep the repo build
native). ttr's own xUnit suite still cannot build in-sandbox, so **the watch tests are CI-authoritative**;
they were verified locally via tmux against a pre-built `ttr.dll` + pre-built `fixtures/watch`, plus the pure
reducer/debouncer/graph unit tests which run anywhere.

A second environment artifact surfaced in M7 (below): the host-mounted filesystem makes `dotnet build`
**~7× slower** than a normal FS, which dominates the latency measurement — see AC5.

## Milestone 1 — fake-first watch states

`AppState.Watch` (`Off`/`Build`/`External`) gates a header segment shown ONLY in watch mode
(`watch: idle` / `change detected` / `building <proj>` / `running (n/m)` / `queued (change during run)`), so
every non-watch snapshot is byte-identical. `building`/`running` derive from `Busy`/`Running`; the coordinator
supplies the `change-detected`/`queued`/`idle` activity via `AppEvent.WatchStateChanged`.

`--fake --watch` (`FakeWatchAdapter` + `FakeWatchDemo`) scripts full cycles on a timer over a tiny registered
project: change → build spinner → re-discovery diff (**+Divide / −Multiply**) → auto-rerun, with one cycle
arriving mid-run to show the queued state. The reruns go through the real orchestrator → adapter path, so the
demo is watch mode exercised end-to-end. Snapshots (`WatchSnapshotTests`) pin each header state and the tree
before/after the diff.

## Milestone 2 — ProjectGraph service

`ttr.Build/ProjectGraphService` loads `Microsoft.Build.Graph.ProjectGraph` over the target set once at
startup (no new dependency — `Microsoft.Build` was already referenced), holds the reverse edges
(`ProjectGraphNode.ReferencingProjects`) in memory, and exposes the transitive reverse-dependents closure
(D5). It reloads only when a `.csproj/.props/.targets` file changes. Graceful degradation: a load failure
falls back to a self-only closure (never fewer rebuilds than needed). The pure closure walk is unit-tested
(`ProjectGraphTests`) over the same shape as `fixtures/watch` — the three POC-6 correctness cases (chained +
diamond deps implicate both test roots; the unrelated chain is untouched; a test-project-only change
implicates only itself). The real MSBuild-backed graph is proven end-to-end by AC1 (below).

## Milestone 3 — re-discovery diffing

A two-phase, pure-reducer diff keyed on the derived `TestCaseId`: `RediscoveryStarted(projects)` tombstones
every existing leaf under the changed projects; the re-arriving `TestsDiscovered` events clear the tombstone
on survivors (and, for a run-materialised theory branch, its whole subtree — so a non-serialisable theory
re-supplied as its Method keeps its Case children); `RediscoveryCompleted` sweeps whatever is still
tombstoned and prunes emptied Namespace/Class/Method branches. Kept tests retain their status/detail (the
diff never blanks a result); new tests arrive `NotRun`; selection survives by id, else moves to the nearest
surviving row. Covered by `WatchReducerTests` (add/remove/keep, no-op, empty-branch prune, selection
survival both ways, theory-across-rediscovery).

## Milestone 4 — watch mode A (source)

`SourceWatchSource` puts a `FileSystemWatcher` on **every** project directory (libraries included, so a
library edit reaches its dependents through the closure), filtering to `.cs` + `.csproj/.props/.targets` and
ignoring `obj/`, `bin/`, and editor temp artifacts (`.tmp`, `~`, vim's `4913`, `.swp`, dotfiles). Changes
feed a 300 ms trailing-edge `Debouncer` that coalesces bursts; `WatchCoordinator` maps the batch → dependents
closure → affected **test** projects → **parallel rebuild** (`Task.WhenAll`, `--no-restore` on the common
source-edit cycle) → on success re-discover + diff → auto-rerun the affected set (narrowed to failed-only
under `f`). A build failure surfaces the Phase 3 error node and stops the cycle before discovery. A
project-file change additionally reloads the graph and re-evaluates that project's detection (changed project
only). The Phase 4 run queue is reused for the rerun — the reducer coalesces `WatchRerunRequested` into
`QueuedRerun` when a run is active (no second queue, per M0.2).

The three editor-storm patterns each coalesce to exactly one batch: proven deterministically in
`WatchDebouncerTests` (accumulate/flush split) and against a live `FileSystemWatcher` in
`WatchIntegrationTests`.

## Milestone 5 — watch mode B (external builds)

`AssemblyWatchSource` watches only the per-TFM **primary output assemblies** of the test projects, with a
500 ms stability window (trailing timer = "no write for 500 ms") plus a belt-and-braces exclusive-open probe.
A real `dotnet build` in another pane rewrites the assembly → one cycle; a no-op incremental build doesn't
rewrite it → zero cycles (no special-casing). Downstream (re-discover → diff → auto-rerun) is identical to
mode A, except builds are skipped (assemblies are already fresh). Covered by `WatchIntegrationTests`
(one-on-rewrite, zero-on-noop).

## Milestone 6 — CLI + lifecycle

`--watch [build|external]`, bare `--watch` = build/source mode (`--continue`/`--tfm`/`--state-dir` remain the
only reserved flags). A watch target with zero test projects stays alive with a "no test projects yet" notice
instead of exit 2. Shutdown (AC9): `q`/Ctrl+C cancels the token first (any in-flight cycle/run unwinds through
the Phase 4 cancellation paths), THEN disposes the coordinator, which stops the watchers before draining its
loop — a watcher event can never fire into a torn-down pipeline (guarded by `Debouncer`/source dispose flags;
`WatchIntegrationTests.Disposed_source_never_fires_again`). Exit codes are unchanged from Phase 4.

## Milestone 7 — the real latency measurement (AC5) — **STOP-and-report: bar not met in-sandbox**

`fixtures/watch/` is the POC-6 10-project bed (LibA..F diamond, TestsCore→LibA, TestsApp→LibF→…→LibA, and the
independent LibX/LibY + TestsX). `scripts/watch-latency.sh` drives real watch cycles through ttr (tmux),
touches `LibA/Calc.cs` for N cycles, and reads per-stage timestamps ttr writes to `TTR_WATCH_LOG`. Measured
over 5 warm cycles (cold cycle dropped), on a normal (non-mounted) filesystem:

| segment | min | median | p95 | (ms) |
|---|---:|---:|---:|---|
| debounce (T0→T1) | 303 | 304 | 305 | trailing 300 ms — on budget |
| build (T1→T2) | 4439 | **4655** | 5142 | parallel `dotnet build --no-restore` of TestsCore + TestsApp (rebuilds the diamond) |
| discover (T2→T3) | 1279 | 1302 | 1330 | VSTest re-discovery of both projects (persistent wrapper) |
| diff (T3→T4) | 0 | **0** | 0 | pure reducer — free |
| **TOTAL (T0→T4)** | 6023 | **6261** | 6756 | **> 4000 ms bar → FAIL** |
| rerun (T4→T5, info) | 1473 | 1511 | 1527 | auto-rerun of the affected set |

**This is a STOP-and-report, not a relaxed threshold.** The `watch-latency` CI job asserts `< 4000 ms` and
currently fails on this measurement, so the miss is surfaced, not hidden. Analysis:

- **The pipeline meets the plan's budget; `dotnet build` throughput is the entire overage.** Non-build
  overhead is debounce + discover + diff = 0.30 + 1.30 + 0 = **1.6 s**. Plugging in POC-6's real-hardware
  parallel-build estimate (~1.75 s) gives ~**3.35 s** — inside the 4 s bar. The measured build here is
  **4.66 s** (2.7× that estimate): compiling the 7-project diamond chain after a base-library edit is
  CPU/FS-bound, and the agent sandbox is materially slower than the machine POC-6 was measured on.
- **Filesystem matters enormously.** On the sandbox's host-mounted (9p/virtiofs) FS the same measurement is
  **T0→T4 median ≈ 38 s** (build alone ≈ 34.6 s). The `/tmp` numbers above strip that artifact; they are the
  honest lower bound achievable in this sandbox.
- **CI (`watch-latency`, ubuntu-latest, normal FS) is the authoritative lane** for the bar and must be the
  number of record. If CI also exceeds 4 s, the plan §9 budget's "~3 s achievable" assumption needs
  revisiting for a diamond this deep — that is an owner decision, flagged here rather than absorbed.

Legit optimisations already applied: `--no-restore` on the source-edit cycle (the target set is restored at
startup) shaved the per-cycle restore; measuring on a non-mounted FS removed the mount artifact. Not pursued
(would change the plan's mandated shape): building the whole solution in one invocation instead of
per-test-project parallel builds (POC-6 mandated the latter), or trimming the fixture (would be relaxing the
test).

## Milestone 8 — NUnit4-MTP investigation (timeboxed) — **outcome (b): documented version regression**

Rebuilt a minimal NUnit 4 MTP-native fixture (`EnableNUnitRunner`) with current packages and probed the
`--server` handshake directly. Findings (all reproduced this phase):

| Package set (NUnit 4.6.1) | `IsTestingPlatformApplication` | Output | `--server` behaviour |
|---|---|---|---|
| `NUnit` + `Microsoft.NET.Test.Sdk`, `EnableNUnitRunner`, `OutputType=Exe` | **empty** | only `nunit.framework.dll` — **no MTP runner** | silent no-op console app, exits 0, runs nothing |
| `NUnit` + `Microsoft.Testing.Extensions.TrxReport` | — | **CS5001** (no entry point generated) | n/a |
| `NUnit` + **`Microsoft.Testing.Platform.MSBuild`** | **True** | MTP entry point generated | **crashes on startup, exit 134** |

The last combo is the smoking gun: the generated `TestingPlatformEntryPoint.Main` throws
**`InvalidOperationException: The test framework adapter has not been registered. Use
'ITestApplicationBuilder.RegisterTestFramework' to register it`** — NUnit 4.6.1's `EnableNUnitRunner` opt-in
does **not** register NUnit's framework into the platform's generated entry point, so the host aborts (exit
134) **before** any server handshake. This is exactly the symptom Phase 4 saw, and it **contradicts POC-3's
empirical pass** — POC-3 must have pinned a package set in which the NUnit MTP registration hook was present;
current NUnit 4.6.1 + `Microsoft.Testing.Platform` no longer produces a working host.

Verdict: a **version-specific NUnit packaging regression**, not a ttr adapter gap (MTP is proven end-to-end by
three independent frameworks — xUnit v3, MSTest.Sdk, TUnit). ttr's §6.2 smoke validation is exactly the
safeguard for this: a host that crashes/exits without completing the handshake surfaces as an explicit
warning node, never a silently empty subtree. We therefore keep the existing `NUnit4Broken.Tests` smoke
fixture (dead opt-in) as the regression and do **not** add a live NUnit4-MTP fixture to the matrix (its
crash-on-startup timing can't be verified end-to-end in the sandbox). See the §16 open item + upstream issue
text below.

### §16 open item + upstream issue text

> **NUnit MTP-native runner (`EnableNUnitRunner`) fails to register its framework.** With NUnit 4.6.1 +
> `Microsoft.Testing.Platform.MSBuild` (net10.0), the generated MTP entry point throws "The test framework
> adapter has not been registered" and the host aborts (exit 134) before serving `--server`. `EnableNUnitRunner`
> sets `IsTestingPlatformApplication=true` but no NUnit registration hook is injected. Expected: the NUnit MTP
> runner registers itself (equivalent to `builder.AddNUnit()`) so `--server --client-port <n>` completes the
> initialize handshake, as xUnit v3 / MSTest.Sdk / TUnit do. Repro fixture + exact package graph attached.
> Track upstream (github.com/nunit/nunit) and re-test when the NUnit MTP runner package stabilises.

## Deviations from the brief / plan

1. **Watch timing log via `TTR_WATCH_LOG`, not `--log`.** `--log` is VSTest's diagnostic trace path; reusing
   it for the watch-cycle timing let VSTest's flood clobber the segment marks. The latency harness uses a
   dedicated `TTR_WATCH_LOG` file instead. No behaviour change for normal runs (the timing log is opt-in).
2. **Latency harness measures on a `/tmp` copy.** The agent sandbox's repo lives on a host-mounted (9p/virtiofs)
   filesystem where `dotnet build` is ~7× slower than a normal FS — an artifact no real deployment has. The
   harness copies `fixtures/watch` to a normal FS before measuring (on CI that is a no-op cost — the checkout
   is already on a normal FS). Documented rather than hidden.
3. **`--no-restore` on watch source-edit rebuilds.** POC-6's warm budget assumes no per-cycle restore; the
   target set is restored at startup, so watch rebuilds pass `--no-restore` (a project-file change re-enables
   restore, since a new package may be needed). This is an addition to the plan §7 build command, scoped to
   watch cycles only.

## Feed-forward for Phase 6 (`--continue`)

- **Result lifecycle across a re-discovery is now explicit** and Phase 6's `Stale` marking should mirror it:
  the diff KEEPS a surviving test's status/detail untouched (it is only ever replaced by a real run), and a
  rebuilt-but-not-yet-rerun test is precisely the "results are for the old binary" case `Stale` exists for.
  When `--continue` restores results and then a watch rebuild lands, the natural model is: on
  `RediscoveryCompleted` (or on `BuildSucceeded` for the affected projects), mark kept leaves `Stale` until
  the auto-rerun replaces them — reusing the tombstone/keep machinery here rather than blanking.
- The watch auto-rerun already routes through `WatchRerunRequested` → the reducer's Queued/launch/coalesce
  path; a `--continue` session that resumes mid-watch should restore `Watch`/`WatchActivity` = idle and let
  the first change drive a normal cycle (no persisted in-flight cycle state).
- `RunTotal` (added for the `running (n/m)` header) is a per-run scalar, not persisted — `--continue` should
  leave it zero until the first run.
