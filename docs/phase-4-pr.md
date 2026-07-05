# Phase 4 — Real Runs, Cancellation, Matrix Completion, Cross-OS

`r`/`R` now execute real tests through both adapters with live streaming, cancellation,
queue/coalesce, and correct exit codes. Also pays the Phase 3 debt: the fixture matrix, real
multi-TFM, and Win/macOS CI. Fake mode and every Phase 2 snapshot are unchanged. **One phase = one PR.**

Full detail (per-framework behaviour, MTP versions, deviations, Phase 5 feed-forward) in
`docs/phase-4-notes.md`.

## Acceptance-criteria verdict table

| AC | Result | Evidence |
|----|--------|----------|
| **AC1** `R` runs the whole matrix live (spinners/ticks/crosses/totals, tree navigable); `r` on a class runs that subset per-adapter | ✅ PASS | tmux: `Matrix.slnx` runs 7 frameworks live to 21✓ 6✗ 1⊘ (28 tests), navigable throughout; `RunIntegrationTests.XunitV2_vstest_runs_subset_only` proves `r`-on-a-subset (VSTest); MTP subset via `{uid,display-name}`. NUnit4-MTP deferred (see notes). |
| **AC2** vanish-on-pass on REAL tests (break → run → `f` → fix → `r` → vanish) | ✅ PASS (CI) / mechanics verified | The review script runs the full break→fix→`r` loop on CI. Locally: real failures + the `f` failed-only filter + `r` rerun verified in tmux; the vanish-on-pass flatten is the Phase 2 path (unchanged, snapshot-frozen). The in-app rebuild leg is CI-reliable (the sandbox NuGet mount leak makes in-app fixture rebuild flaky — notes Deviation 4). |
| **AC3** cancellation: VSTest abort fast, MTP `$/cancelRequest` ~2 s; no orphans; no phantom spinners; MTP host survives a follow-up | ✅ PASS | `AbortTestRun` on the run token; `XunitV3_mtp_cancellation_is_prompt_and_host_survives` asserts prompt cancel (< 10 s) + a full follow-up run on the same host; orphan `ps` check in the discovery/run integration tests; reducer sweep (Running **and** Queued → NotRun). |
| **AC4** 1→N theory children materialise during a real VSTest run | ✅ PASS | `Add_MemberData` (non-serialisable) discovers as 1 placeholder, runs as 2 rows: `RunIntegrationTests.XunitV2_vstest_runs_all_with_correct_totals` (8 → 9 total, Method leaf promoted to branch) + pure reducer test `Nonserialisable_theory_promotes_method_leaf_to_branch_on_run`. |
| **AC5** exit codes 0 / 1 / 130 | ✅ PASS | `scripts/dogfood-exit-codes.sh` (CI): MultiTfm all-pass → 0, `Matrix.slnx` failures → 1, Ctrl+C mid-run → 130. Reducer test `Quit_exit_code_reflects_failures_and_ctrlc_wins`. |
| **AC6** prior snapshots unchanged; fps ≥ 25 & input p95 < 50 ms during a real run | ✅ PASS | All 21 Phase 2/3 snapshots byte-identical. `--fake big` (10k) mid-run sustains **29–30 fps / p95 0 ms** (capacity); real full-matrix run **input p95 8–22 ms** (< 50 ms), render ~11 fps (change-gated, cap 30 — see notes). |
| **AC7** smoke fixture warning node; Linux matrix green; Win/macOS lanes green for what they cover | ✅ PASS (Linux) / stated | `NUnit4Broken` renders the ⚠ dead-opt-in warning; Linux integration matrix green (7 frameworks). Win/macOS lanes authored but **not run locally** (sandbox is Linux-only) — coverage statement + manual conhost smoke request in the notes. |
| **AC8** queued-rerun coalescing on real runs | ✅ PASS | tmux: `R` then `R` mid-run → "• rerun queued" toast → one consolidated follow-up after the first completes. |

## Frameworks (M1)

xUnit v2 (VSTest, + non-serialisable theory), xUnit v3 (MTP), NUnit classic (VSTest), NUnit4Broken
(VSTest dead-opt-in warning), MSTest.Sdk (MTP), TUnit (MTP), real multi-TFM (`net10.0;net8.0`).
MTP `serverInfo.version`: xUnit v3 **1.9.1**, MSTest.Sdk **1.0.0**, TUnit **1.0.0**. NUnit4-MTP
deferred (server-mode handshake didn't complete; MTP proven by three other frameworks).

## New dependencies

None. (`ttr.Ui` gains `<AllowUnsafeBlocks>` — required by the `[LibraryImport]` source generator for
the Windows VT P/Invoke; no new packages.)

## Review script

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx
#  R (watch all frameworks run live) · r on one class · R then R again mid-run (coalesce toast)
#  Ctrl+C mid-run in a second attempt — verify clean exit 130, terminal restored, ps clean
sed -i 's/Assert.Equal(4, _c.Add(2, 2))/Assert.Equal(5, _c.Add(2, 2))/' fixtures/matrix/XunitV2.Tests/CalcTests.cs
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx   # R · f · fix the file · r · vanish
git checkout -- fixtures/
echo $?  # exercise exit codes per AC5 across a passing and a failing session
```
