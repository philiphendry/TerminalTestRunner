# ttr — Phase 4 Brief: Real Runs, Cancellation, Matrix Completion, Cross-OS

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is at
`docs/ttr-implementation-plan.md`. Where this brief and CLAUDE.md conflict, stop and flag it.

## Context and goal

Phase 3 delivered real streamed discovery through both adapters and proved the pipeline
end-to-end on xUnit v2 (VSTest) and xUnit v3 (MTP), deferring the remaining matrix fixtures,
real multi-TFM verification, and Win/macOS CI. Phase 4 does two things: **pays that debt**,
and implements **real test execution** — `r`/`R` run actual tests through both adapters with
live streaming, cancellation, queue/coalesce, and correct exit codes. When it merges, ttr is
a working test runner; only watch, sessions, and packaging remain.

## Explicit non-goals

- No `--watch`, no re-discovery diffing, no `--continue`/persistence (Phases 5–6).
- No packaging changes (Phase 7). No new CLI flags.
- No UX changes: all Phase 2/3 snapshots pass unchanged. Run states (Queued/Running/
  terminal, queued-rerun toast) already exist from Phase 2's fake implementation — real
  adapters feed the SAME events; if a real behaviour can't be expressed in the existing
  event vocabulary, stop and flag it rather than inventing UI.
- New dependencies: none pre-approved. Default no.

## Milestone 0 — Reconcile Phase 3 (~30 min)

Read `docs/phase-3-notes.md` + PR. Note: the live-protocol corrections (single-object
params, flat dotted keys — now in CLAUDE.md), the `DisableMSBuildAssemblyCopyCheck`
workaround, the sandbox run pattern (prebuilt fixtures + `dotnet <path>/ttr.dll`), and the
native `TestCase` cache built for this phase. Flag contradictions before building.

## Milestone 1 — Pay the Phase 3 debt: complete the fixture matrix

Per the Phase 3 brief M3 (unchanged spec): add NUnit classic (NUnit3TestAdapter, VSTest),
NUnit 4 MTP (`EnableNUnitRunner` path), MSTest.Sdk (MTP), TUnit, the **deliberately broken
NUnit4 + `EnableMicrosoftTestingPlatformRunner` + adapter fixture** (smoke-validation
regression — must yield the warning node, never an empty subtree), and verify the multi-TFM
project's net10.0 TFM builds/discovers for real (net8.0 may remain evaluate-only in the
sandbox). Extend the Phase 3 integration tests to assert discovery counts/tree shape for
ALL of them, both solution formats. Record each framework's MTP `serverInfo.version`.

## Milestone 2 — VSTest execution (plan §6.3)

- `RunAsync(subset)`: pass cached native `TestCase` objects (from the Phase 3 discovery
  cache keyed by derived `TestCaseId`); empty subset = whole source.
- `HandleTestRunStatsChange`: newly completed `TestResult`s → `TestFinished` (with full
  `TestResultDetail` — message/stack/stdout/duration, parser wired as in fake mode);
  `ActiveTests` → `TestStarted`/Running (the spinner signal).
- The 1→N mapping live: the non-serialisable theory fixture discovers 1 case, must render
  N case children materialising during the run (the reducer path exists — prove it real).
- **Cancellation = `AbortTestRun()`** (never `CancelTestRun` — CLAUDE.md). On run-end via
  abort, the reducer sweep flips any still-Running to NotRun — assert no phantom spinners.
- Wrapper stays persistent across runs; run-level runsettings kept minimal (DesignMode).

## Milestone 3 — MTP execution (plan §6.4)

- `testing/runTests`: `tests` OMITTED for run-all; `{uid, display-name}` array for subsets;
  single-object flat-dotted-key params (Phase 3 correction).
- `in-progress` notifications → Running/spinners; terminal states → `TestFinished` with
  details + structured location; expect fast suites to arrive as one 200 ms-batched burst
  (the coalescing loop absorbs it — do not add artificial pacing).
- **Cancellation via `$/cancelRequest`**: ~2 s, server process must survive and service a
  subsequent run (process reuse across runs on the same build is the point — assert it).
  Restart the host only after a rebuild (staleness → rebuild → new process).
- One request in flight per session; a run request while another is active queues at the
  orchestrator (never at the RPC layer).

## Milestone 4 — Run orchestration + exit codes

- `r`/`R` real: collect leaf ids under the node (failed-only when 'f' active) → staleness
  check → **parallel build of stale affected test projects** (Phase 3 BuildService) → fan
  out per-adapter runs → merge streams. Build failure aborts that project's run with the
  error node; other projects proceed.
- Queue/coalesce (Phase 2 semantics, now real): requests during an active run merge into
  one consolidated follow-up; toast preserved.
- Ctrl+C during a run: cancel adapters (abort/cancelRequest), sweep spinners, exit 130.
  `q` during a run: same cancellation path, then exit code per results.
- Exit codes on quit: 0 all-pass/none-run, **1 if any test in the session's latest results
  failed**, 130 Ctrl+C (plan §3).

## Milestone 5 — Cross-OS CI + Windows terminal reality

- Win + macOS CI lanes: build, unit + snapshot suites, and the integration matrix
  (frameworks that restore on those OSes).
- **Windows VT enablement** (new CLAUDE.md rule): `SetConsoleMode` +
  `ENABLE_VIRTUAL_TERMINAL_PROCESSING` at startup before any ANSI write; clear failure
  message on legacy conhost where it can't be enabled. A Windows-lane test asserts the
  enable call path; a manual Windows Terminal + conhost smoke check is requested from the
  owner in the PR (sandbox is Linux-only — be explicit about what CI does and doesn't prove).
- The tmux over-width assertion joins CI as a scripted Linux job (`--fake big`, resize,
  capture, awk) so the ellipsis guarantee is machine-checked every merge.

## Milestone 6 — Dogfooding

ttr's own test suite becomes a fixture: a CI job runs `ttr tests/... ` headless-ish (drive
via tmux: launch, wait for run completion by polling capture-pane for the totals, then `q`)
and asserts exit code correctness. From this phase on, regressions in ttr break ttr's own CI.

## Acceptance criteria (verdict table in PR description)

- AC1: `R` on the full matrix solution runs ALL six frameworks' tests with live spinners,
  ticks/crosses, and correct totals; tree navigable throughout; `r` on a class runs exactly
  that subset (verify per-adapter).
- AC2: The vanish-on-pass loop works on REAL tests: break a fixture test, run, 'f', fix,
  'r', watch it vanish.
- AC3: Cancellation — VSTest abort completes fast (<2 s) and MTP `$/cancelRequest` ~2 s;
  in both: no orphaned processes (`ps` integration check) and no phantom spinners
  (post-abort state asserted); MTP host survives and services a follow-up run.
- AC4: 1→N theory case children materialise during a real VSTest run.
- AC5: Exit codes: 0 / 1-on-failures / 130, asserted by the dogfood job.
- AC6: All prior snapshots unchanged; fps ≥ 25 and input p95 < 50 ms during a real
  full-matrix run (header numbers reported).
- AC7: Smoke fixture still yields the warning node; matrix integration tests green for all
  six frameworks on Linux CI; Win/macOS lanes green for what they cover (state exactly what
  was and wasn't provable in CI).
- AC8: Queued-rerun coalescing demonstrated on real runs.

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx
#  R (watch all six run live) · r on one class · R then R again mid-run (coalesce toast)
#  Ctrl+C mid-run in a second attempt — verify clean exit 130, terminal restored, ps clean
sed -i 's/Assert.Equal(4/Assert.Equal(5/' fixtures/matrix/XunitV2.Tests/CalcTests.cs
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx   # R · f · fix the file · r · vanish
git checkout -- fixtures/
echo $?  # exercise exit codes per AC5 across a passing and a failing session
```

## Deliverables

One PR: code + fixtures + tests + `docs/phase-4-notes.md` (per-framework run behaviour vs
the plan's POC expectations — especially MTP batching feel and cancellation timings; the
Windows/macOS coverage statement; measured AC6 numbers; deviations; feed-forward for the
Phase 5 watch brief, particularly anything about rebuild-then-rerun sequencing you learned).
