# Phase 6 — Sessions (`--continue`)

Makes sessions durable: state is saved after every run and on clean exit, and `ttr --continue` restores the
previous session — results, expansion, selection, filters, pane layout, scroll — with restored results honestly
marked **`Stale`** (dim ✓/✗) where the code has since changed. Implements the POC-8 persistence design (packed
JSONL + offset index, atomic renames, no gzip); it does not re-explore it. Fake mode, the Phase 2 UX contract,
and every prior snapshot are unchanged — the restore notice and Stale dimming render ONLY in restored/stale
situations.

Full detail, the measured bars, and Phase 7 feed-forward are in `docs/phase-6-notes.md`.

## New dependencies

**None.** `System.Text.Json` is in-box on net10.0; no source-generation (POC-8 found it made no difference).

## Acceptance criteria

| AC | Verdict | Evidence |
|----|---------|----------|
| **AC1** Run matrix, toggle filters/pane, select a deep node, quit; `--continue` restores results + expansion + selection + filters + pane + scroll | ✅ PASS | tmux on `fixtures/matrix/XunitV2.Tests`: ran (6✓ 2✗ 1⊘), toggled `t`+`s`, selected `Failing_Assertion`, `q` → `state.json` (9 tests, selection, `details`+`times` filters, 2 run dirs). `--continue` restored results, UI, selection, expansion, and the detail pane. `SessionIntegrationTests` (full round-trip) + `SessionReducerTests` (UI apply, nearest-survivor). |
| **AC2** Touch a source, rebuild (or `--continue --watch`): affected restored results render `Stale` (dim); running them replaces staleness | ✅ PASS | tmux: `touch`ed the assembly → restored glyphs went dim `\x1b[90m` Stale; `R` reran → back to bright green. `SessionReducerTests` (restore-marks-stale, rebuild-marks-kept-stale, run-clears — incl. pass→pass rerun) + `SessionIntegrationTests`. Composes with watch via the shared `RediscoveryCompleted` path. |
| **AC3** Details and 'o' work on restored results without a run; lazy fetch < 20 ms | ✅ PASS | tmux: restored `Failing_Assertion` detail loaded into the pane; `o` opened `CalcTests.cs` at line 18 — no rerun. `SessionPerfTests.Lazy_detail_under_20ms` (**6–10 ms** cold, idx parse included; do-not-preload). Refs re-resolve existence at display time. |
| **AC4** Change the target set → "targets changed" degrade, fresh session, no crash; corrupt/truncate state.json → same | ✅ PASS | `SessionStoreTests` (fingerprint mismatch → `TargetsChanged`; garbage → `Corrupt`; `schema:99` → `SchemaMismatch`; missing → `NoState`), each degrading to a fresh session + specific toast. M1 snapshot `Continue_targets_changed_degrade`. |
| **AC5** POC-8 bars all met + CI-asserted (report numbers); kill -9 5/5 | ✅ PASS | Generated 10k session: **save 31 ms** (<200), **restore 47 ms** (<500), **lazy 6–10 ms** (<20), **footprint 2.37 MB** (<20), **jitter p95 5.9 ms** (≤10), **kill -9 5/5**. Asserted in `SessionPerfTests` / `SessionCrashSafetyTests` (numbers echoed to the test log). Table in notes §M6. |
| **AC6** `runs/` prunes to 3; `.ttr/.gitignore` written once; `--state-dir` honoured | ✅ PASS | `SessionStoreTests.Runs_prune_to_three`, `Gitignore_is_written_once`; `--state-dir` used throughout the tmux runs and the store tests (an arbitrary dir). tmux showed `.ttr/.gitignore` = `*` and `results/` capped. |
| **AC7** Saves fire after every run including watch cycles; a mid-run crash loses only the in-flight run | ✅ PASS | Orchestrator saves on `prev.Running && !next.Running` (manual + watch auto-runs) and on exit; tmux showed run-1 after `R` and run-2 after `q`. Mid-run crash safety: kill -9 harness leaves the last saved run intact (5/5). |
| **AC8** All prior snapshots unchanged; new session snapshots green; header perf steady during a background save | ✅ PASS | Restore notice / Stale dimming render only when set, so all earlier snapshots are byte-identical (full suite green). 4 new `SessionSnapshotTests`. The save write is off-thread from a reducer-thread snapshot — jitter p95 5.9 ms on a 30/s loop during continuous 10k saves. |

**Suite:** 203 tests (was 174); **202 green**, the 1 being the pre-existing `/tmp`-only Verify path-scrubber
artifact (`Backend_build_failure_detail`), green on CI's normal FS.

## Notable deviations (full list in notes)

- Per-test `finishedUtc` = the save time (keeps the reducer pure/deterministic; ≈ run-finish, exactly what
  staleness needs).
- Run-materialised theory rows (e.g. `[MemberData]`) drop on restore until a rerun re-materialises them
  (counted as "N gone" in the notice) — the one visible rough edge, honest per the brief's drop rule.
- The perf + kill -9 suites are serialised with the Phase 4/5 integration + watch-fs suites in one
  `[Collection("integration")]` (their load flaked the ms bars in parallel). Suite runtime 13 s → 26 s.
- One inert test hook in the CLI: `TTR_CRASH_SAVE_DIR` env-gated loop-save mode for the kill -9 harness.

## Review script

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx    # R · f on · b · select a failed test · q
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx --continue   # AC1: everything back; s and o on a restored failure (AC3)
sed -i 's/;/; \/\/ touch/' fixtures/matrix/XunitV2.Tests/CalcTests.cs   # (brief said Calc.cs; the fixture file is CalcTests.cs)
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx --continue --watch  # AC2: watch cycle flips the subtree Stale, rerun replaces
git checkout -- fixtures/
ls .ttr/results/            # AC6: ≤ 3 runs
dotnet run --project src/ttr.Cli -- --fake --continue              # M1 states, fake-first
```
