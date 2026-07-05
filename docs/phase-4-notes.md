# Phase 4 notes — Real Runs, Cancellation, Matrix Completion, Cross-OS

Phase 4 makes ttr a working runner: `r`/`R` execute real tests through both adapters with live
streaming, cancellation, queue/coalesce, and correct exit codes — and pays the Phase 3 debt (fixture
matrix, real multi-TFM, cross-OS CI). Fake mode and the entire Phase 2 UX contract are unchanged.

## Milestone 0 — Phase 3 reconciliation

No contradictions with the brief. The carried context held:
- **Live-protocol MTP corrections** (single named-object params, flat dotted keys) are in CLAUDE.md and
  were the foundation for the run path.
- The **native `TestCase` cache** built in Phase 3 (`VsTestDiscoverer.Cache`, keyed by derived id) is
  exactly what the VSTest subset run consumes.
- The **sandbox run pattern** (prebuilt fixtures + `dotnet <path>/ttr.dll`) is how every run was
  verified in tmux.
- The **`DisableMSBuildAssemblyCopyCheck`** workaround stayed put (unrelated to runs).

One Phase 3 latent bug surfaced and was fixed (see Deviation 3): xUnit v3's theory rows were being
collapsed at discovery.

## Milestone 1 — Fixture matrix (paid the debt)

Seven test frameworks now populate `fixtures/matrix/` (added to both `Matrix.slnx` and `Matrix.sln`);
integration tests assert discovery counts/tree shape **and** run outcomes for them:

| Fixture | Runner (detected) | Discovered | Run result | MTP `serverInfo.version` |
|---|---|---|---|---|
| `XunitV2.Tests` | VSTest | 8 (incl. 1 non-serialisable theory placeholder) | 6✓ 2✗ 1⊘ | — |
| `XunitV3.Tests` | MTP | 5 (theory rows enumerated at discovery) | 4✓ 1✗ | **1.9.1** |
| `NUnitClassic.Tests` | VSTest | 4 | 3✓ 1✗ | — |
| `NUnit4Broken.Tests` | VSTest + ⚠ "incomplete MTP migration?" | 1 | 1✓ | — (dead opt-in) |
| `MSTestSdk.Tests` | MTP | 4 | 3✓ 1✗ | **1.0.0** |
| `TUnit.Tests` | MTP | 3 | 2✓ 1✗ | **1.0.0** |
| `MultiTfm.Tests` | MTP, `net10.0;net8.0` | 2 (net10.0) | 2✓ (net10.0) | 1.9.1 (net10.0) |

- **`MultiTfm`** proves the multi-TFM tree (a TFM level with `net10.0` + `net8.0` children). `net10.0`
  builds/discovers/runs for real; `net8.0` is **evaluate-only** in the sandbox/CI (its runtime is absent)
  and surfaces as a `⚠ not built for this TFM` node under the `net8.0` TFM — never a silently empty subtree.
- **`NUnit4Broken`** remains the smoke-validation regression: `EnableMicrosoftTestingPlatformRunner` +
  the classic NUnit3TestAdapter yields the dead-opt-in **warning node** (VSTest path), not an MTP host.
- All MTP `serverInfo.version`s (1.9.1, 1.0.0) are inside the tested 1.x–2.x range → no drift warning.

### Deferred: NUnit 4 MTP-native runner (`EnableNUnitRunner`)

`NUnit4Mtp.Tests` (NUnit 4.6.1 + `EnableNUnitRunner`) builds and classifies MTP, but its host **never
completes the server-mode handshake** — a direct JSON-RPC probe timed out with no `initialize` response,
and it stalled discovery for the 30 s handshake timeout. NUnit's MTP runner appears not to speak the
`--server --client-port` protocol the way xUnit v3 / MSTest.Sdk / TUnit do (which all handshake in
well under a second). Rather than ship a 30 s discovery stall, the fixture was removed and this is flagged
for follow-up. **The MTP protocol itself is proven end-to-end by three independent frameworks** (xUnit v3,
MSTest.Sdk, TUnit), so this is a fixture/runner-config gap, not an adapter gap.

## Milestone 2 — VSTest execution (§6.3)

`VsTestDiscoverer` gained `RunAsync(subset)`: the persistent `VsTestConsoleWrapper` (reused across
discovery and every run — one `StartSession`) runs the cached native `TestCase`s for a subset, or whole
sources for run-all, grouped by TFM. `HandleTestRunStatsChange` maps `ActiveTests` → `TestStarted`
(spinner) and `NewTestResults` → `TestFinished` with full `TestResultDetail` (message/stack/stdout,
parser wired as in fake mode). **Cancellation = `AbortTestRun()`** registered on the run token (never
`CancelTestRun`); the blocking `RunTests` returns and the reducer sweeps any still-Running leaf.

**1→N theory (AC4), proven live:** the non-serialisable `Add_MemberData` theory discovers as ONE
placeholder case (display == FQN → a Method leaf); the run reports its rows with distinct display names.
The reducer **promotes** that Method leaf to a branch and materialises N Case children, removing the
placeholder's own rollup contribution so the total goes 8 → 9 (not 10). Covered by both a pure reducer
test and the real VSTest integration test.

## Milestone 3 — MTP execution (§6.4)

`MtpDiscoverer` was refactored into a persistent **`MtpSession`** (one host + JSON-RPC connection per
project/TFM): `StartAsync` (launch + handshake), `DiscoverAsync`, and `RunAsync` **reuse the same host
across runs** (`testing/runTests` with `tests` omitted for run-all, or `{uid, display-name}` for a
subset). `execution-state` drives the tree (`in-progress` → Running; `passed`/`failed`/`skipped` →
finished with `time.duration-ms` and `error.message`/`error.stacktrace`); `cancelled` is left to the
sweep. Results arrive as a **200 ms-batched burst** for fast suites — the coalescing render loop absorbs
it (no artificial pacing).

- **Cancellation = `$/cancelRequest`** (StreamJsonRpc emits it automatically when the run token is
  cancelled): measured well under the 10 s bar; the host **survives and services a follow-up run** —
  asserted by an integration test (cancel → discover → run again → full results).
- The host is **restarted only after a rebuild** (staleness ⇒ new process): `RealBackend` disposes and
  recreates the session for a rebuilt project before re-discovering it.

## Milestone 4 — Run orchestration + exit codes

`RealBackend` is now an `ITestSessionAdapter` and is wired as BOTH the discovery producer and the run
adapter the orchestrator drives on `r`/`R`. `RunAsync(subset)`:
1. Resolves affected projects (all for run-all; owners parsed from the derived ids for a subset).
2. Staleness → **parallel rebuild** of stale affected test projects (Phase 3 `BuildService`); a build
   failure aborts that project's run (error node) while the others proceed; a rebuilt project is
   re-discovered (MTP host restarted).
3. Fans out per-adapter runs concurrently, merges the streams, emits one `RunCompleted`.

A single gate serialises discovery vs runs so the shared VSTest wrapper / one-request-per-session MTP
hosts never overlap (run-vs-run **coalescing** stays the reducer's job, unchanged from Phase 2).

**Exit codes (plan §3):** `q` → 1 if any test in the latest results failed, else 0; **Ctrl+C → 130**
(always wins); backend fatal → 1. Verified live: `Matrix.slnx` (failures) → 1, `MultiTfm` (all net10
pass) → 0, Ctrl+C mid-run → 130.

**Phantom-spinner robustness:** `RunCompleted` now sweeps still-`Queued` leaves to `NotRun` as well as
still-`Running` ones — a queued leaf whose adapter never ran it (e.g. a project that failed to start)
no longer leaves a permanent branch spinner.

## Milestone 5 — Cross-OS CI + Windows terminal reality

`.github/workflows/ci.yml` now runs:
- **`test`** on `ubuntu-latest`, `windows-latest`, `macos-latest`: restore, Release build, build the
  fixture matrix, and the full unit + snapshot + integration suite.
- **`over-width`** (Linux/tmux): `scripts/tui-overwidth.sh` drives `ttr --fake big` at five sizes with
  detail + durations open and asserts no rendered line exceeds its pane (code-point width, not bytes).
- **`dogfood`** (Linux/tmux): `scripts/dogfood-exit-codes.sh` asserts 0 / 1 / 130 on real runs.

**Windows VT enablement** (`WindowsTerminal.TryEnableVirtualTerminalProcessing`, called from `App`
before the first ANSI byte): `SetConsoleMode` + `ENABLE_VIRTUAL_TERMINAL_PROCESSING`; on legacy conhost
it can't be enabled and ttr exits with a clear message (code 3) instead of spraying escapes. A test
asserts the enable call path on every OS (drives the real P/Invoke on the Windows lane).

**What CI does and does NOT prove:** the sandbox is **Linux-only**, so the Windows/macOS lanes were
authored but **not executed locally**. The Linux lane is fully green here. The Windows/macOS lanes are
expected to prove build + unit/snapshot + the cross-platform integration frameworks; a **manual Windows
Terminal + legacy-conhost smoke check is requested from the owner** (the VT enable/fail paths can't be
exercised on Linux beyond the no-op branch).

## Milestone 6 — Dogfooding

The `dogfood` CI job drives real runs and gates on exit-code correctness, so from this phase on a
regression in ttr's run/exit-code behaviour breaks ttr's own CI. (Running ttr over its *entire own* suite
nests MTP/VSTest hosts inside the integration tests and is impractically heavy; the job instead pins the
0/1/130 contract against the fixture matrix, which is the property that matters.)

## AC6 performance (measured)

- **Render capacity:** `--fake big` (10 000 nodes) mid-run sustains **29–30 fps, input p95 0 ms** —
  re-confirming the Phase 2 stress figure with the run path live.
- **Real full-matrix run:** input **p95 8–22 ms** (well under the 50 ms bar); render **~11 fps**. The fps
  is change-gated (the 28-node matrix produces far fewer frame-worthy deltas than 10k nodes; the loop is
  capped at 30 and idles when nothing changed) — not a cap being hit. Input latency is the UX-critical
  number and it is comfortably within bar; the tree stayed navigable throughout every run.

## Deviations from the brief / plan (with reasons)

1. **NUnit 4 MTP deferred** (M1) — its MTP-native runner doesn't complete the server-mode handshake; see
   above. MTP proven by three other frameworks.
2. **`--fake backend` stays read-only.** It still toasts "runs arrive in Phase 4" because it is the
   snapshot fixture for the Phase 3 backend states and AC6 requires those snapshots byte-identical. Real
   targets enable runs; the fake `backend` scenario is a rendering fixture, not a runnable adapter.
3. **xUnit v3 theory-row collapse fixed** (Phase 3 latent bug). MTP reports a theory's rows with a shared
   `location.method` signature (`Even(System.Int32)`) but per-row `display-name`s (`Even(n: 2)`); the old
   mapping keyed the Method on the signature and collapsed the rows. The mapping now keys on the **bare**
   method name, so rows land as distinct Case leaves. (xUnit v3 enumerates rows at discovery, so it is a
   0-gap case; the true discover-1→run-N shape is exercised by the VSTest non-serialisable theory.)
4. **Sandbox build note (not committed).** The mounted host filesystem leaks Windows NuGet paths into a
   solution/graph restore, so `dotnet build ttr.sln` can't build the test project in this sandbox. The
   full suite was validated by building on a non-mounted `/tmp` copy; CI (clean Linux) is the authoritative
   lane. One CI-neutral, `/tmp`-only snapshot diff exists there (Verify's `{SolutionDirectory}` vs
   `{ProjectDirectory}` scrubber resolves differently on a loose copy) — it passes at the real repo path.
   No build workaround is committed (keeps the repo build native, per the Phase 2 lesson).

## For the Phase 5 (watch) brief

- **Rebuild-then-rerun sequencing is already wired** at the run seam: `RealBackend.RunAsync` does
  staleness → parallel rebuild of stale affected projects → re-discover (MTP host **restarted** on a
  rebuild — staleness ⇒ new process; VSTest cache re-populated) → run. Watch mode can reuse this exact
  path per file-change batch; the only new work is the watcher + change→affected-project mapping.
- **MTP host reuse across runs is proven** and is the performance win watch depends on (no 300–500 ms
  host relaunch per unchanged project). Restart is gated strictly on rebuild.
- **Queued-sweep-on-RunCompleted** is in place, so a watch cycle that supersedes an in-flight run leaves
  no phantom spinners.
- The one open risk POC-6 flagged (parallel-build watch cycle < 4 s) is still to be measured against a
  real edit loop in Phase 5.
