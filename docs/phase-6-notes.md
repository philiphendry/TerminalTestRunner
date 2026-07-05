# Phase 6 notes — Sessions (`--continue`)

Phase 6 makes sessions durable: state is saved after every run and on clean exit, and `ttr --continue`
restores the previous session — results, expansion, selection, filters, pane layout, scroll — with restored
results honestly marked **`Stale`** (dim ✓/✗) where the code has since changed. The persistence design was
settled by POC-8 (packed JSONL + offset index, atomic renames, no gzip); this phase implements it. Fake mode,
the Phase 2 UX contract, and every prior snapshot are unchanged (the restore notice / Stale dimming appear
ONLY in restored/stale situations, so earlier snapshots stay byte-identical — AC8).

## Milestone 0 — Phase 5 reconciliation

No contradictions. The Phase 5 feed-forward said the re-discovery diff KEEPS survivor results untouched and
`--continue`'s `Stale` marking should reuse that machinery. Implemented exactly so: `RediscoveryCompleted`
now marks every kept, resulted leaf `Stale` (until the auto-rerun replaces it), which is the SAME code path a
restored result goes through on the first watch rebuild — so `--continue --watch` composes for free. `RunTotal`
stays per-run and is not persisted; a resumed session leaves `Watch`/`WatchActivity` idle and lets the first
change drive a normal cycle. `TTR_WATCH_LOG` and the `/tmp` measurement hygiene are untouched.

## Milestone 1 — Stale presentation + fake-first session states

- **Staleness is an overlay, not a status.** A stale result keeps its real `Passed`/`Failed`/`Skipped` status
  and glyph; a subtree counter `TestNode.StaleLeaves` (propagated like the status counts) drives a dim (grey)
  recolour of the ✓/✗ in `Glyphs.ForNode` — so pass AND fail both dim, exactly as plan §11.2 reserves. The
  pre-existing `TestStatus.Stale` enum member is left in place (harmless) but unused as a status; the overlay
  is the mechanism. A real run (`TestStarted`/`TestFinished`) clears it unconditionally — even a
  pass→pass rerun, which is why the clear is NOT gated on a status change.
- **Restore notice** (`AppState.RestoreNotice`): a header segment "restored N results from <relative time>
  (· K gone)", shown only after a restore, cleared on the first `RunCompleted`. Degrade cases surface as a
  transient toast instead.
- **`--fake --continue`** (`FakeContinueScript` + `FakeContinueDemo`): a scripted restore over a small
  registered project — the tree arrives with restored pass/fail results (the StringTests subtree already
  Stale), pre-applied UI (detail pane open, durations on, the failure selected), the failure's detail via a
  lazy `DetailLoaded`, then a simulated rebuild flips the whole tree Stale. Every state is a real reducer
  state, so the demo and the M1 snapshots (`SessionSnapshotTests`) are the same contract.

## Milestone 2 — Persistence layer (`Ttr.Core.Persistence`)

- `SessionStore` writes `.ttr/` beside the primary target (first sln/slnx else first csproj); `--state-dir`
  overrides; `.ttr/.gitignore` containing `*` is written on first use.
- `state.json` is schema v1 exactly per Appendix A (`SessionDocument`, explicit `[JsonPropertyName]` — the
  on-disk names are the contract): `schema`, `createdUtc`, `savedUtc`, `targets` (path + sha256 content hash),
  `ui` (filters/expanded/selected/scroll), `tests` (id/status/durationMs/finishedUtc/hasDetail), `runs {keep:3, latest}`.
- Details: `results/<runId>/details.jsonl` (one self-describing record per line) + `details.idx`
  (JSON `id → [offset, length]`). **Two atomic renames per run (jsonl, idx) + one for state.json**, and the
  details are written BEFORE state.json, so a crash can only ever leave state.json pointing at a fully-written
  run. Plain `System.Text.Json`, no compression (POC-8). Pruning keeps the 3 most recent run dirs.
- **Restored-detail carry-forward:** a restored result whose detail was never opened is re-emitted from the
  previous run's jsonl on the next save (read-through via the old idx), so a partial (watch failed-only) rerun
  never silently drops the un-rerun results' details across saves.

## Milestone 3 — Save triggers + the jitter bar

- The `Orchestrator` saves after every run — detected as `prev.Running && !next.Running`, which fires for
  manual runs AND watch auto-runs — and `App` saves once more on clean exit. The **snapshot is captured on the
  reducer thread** (`SessionSnapshot.Capture`, an O(n) walk of the immutable data) and the serialise+write runs
  on a background `Task`, so the reducer/render loop never blocks on disk I/O. An internal lock in the store
  serialises overlapping saves (this is IO plumbing, not MVU state — CLAUDE.md's no-locks rule is about state
  mutation). No mid-run partial saves (plan §10/D8).

## Milestone 4 — Restore (`--continue`)

- The CLI loads + validates (schema + target fingerprint) at startup but APPLIES the restore AFTER discovery
  (`AppEvent.SessionRestored`, emitted by a post-discovery action), so results attach to the real tree by
  derived `TestCaseId`. Dropped ids (test gone) are counted in the notice; discovered tests with no restored
  result stay `NotRun`.
- **Staleness** is computed by the CLI (kept out of the pure reducer): a restored result is `Stale` iff its
  project's output assembly — re-stat'd AFTER discovery/rebuild — is newer than the result's `finishedUtc`.
- UI applies after the tree populates: filters/pane/wrap/times, the expansion id-set (replacing the
  auto-expanded default), selection (nearest-survivor via the Phase 5 logic if gone), scroll offsets clamped.
- Any non-`Restored` load (schema/targets/corrupt/none) degrades to a fresh session with a specific toast,
  never a crash, never silent.

## Milestone 5 — Lazy details + 'o' on restored results

- Restored details are NEVER preloaded. The orchestrator fetches a restored leaf's detail off-thread the
  moment it is selected (`store.ReadDetail` → `AppEvent.DetailLoaded`), so by the time the user opens the
  detail pane or 'o' the content is present. The reducer attaches it exactly as a live finish would, which
  **re-runs file-reference existence resolution at display time** (files may have moved) — so 'o' resolves /
  dims refs against the current filesystem, not last session's.

## Milestone 6 — Tests + measured bars

Unit: schema round-trip, fingerprint mismatch / schema / corrupt degrade, prune-to-3, .gitignore, `--state-dir`,
lazy read + carry-forward (`SessionStoreTests`); restore attach/drop matrices, Stale marking + clear-on-run,
UI application, nearest-survivor, `DetailLoaded` (`SessionReducerTests`); the full save→restore→stale→rerun
round-trip through the real store (`SessionIntegrationTests`); the M1 snapshots (`SessionSnapshotTests`).
**203 tests total (was 174); 202 green** — the 1 "failure" is the pre-existing `/tmp`-only Verify path-scrubber
artifact (`Backend_build_failure_detail`), green on CI's normal FS.

### Measured POC-8 bars (AC5) — generated 10k-test session, in-sandbox `/tmp`

| bar | POC-8 | this phase (measured) | verdict |
|---|---:|---:|---|
| state.json save | 32 ms | **31 ms** (median of 5) | ✅ < 200 ms |
| restore index | — | **47 ms** | ✅ < 500 ms |
| lazy detail fetch | < 1 ms | **6–10 ms** (cold, idx parse incl.) | ✅ < 20 ms |
| footprint | 5.62–8.57 MB | **2.37 MB** † | ✅ < 20 MB |
| save jitter p95 (30/s loop) | — | **5.9 ms** | ✅ ≤ 10 ms |
| kill -9 ×5 atomicity | 5/5 | **5/5** | ✅ |

† The synthetic generator's details are lighter than POC-8's corpus (short messages, 10% failures, no captured
stdout), so the footprint is smaller; the bar is what matters and holds with wide margin. No compression, per
POC-8. The perf/crash suites echo the actual numbers to the test log so the PR can quote them; they are
CI-asserted (`SessionPerfTests`, `SessionCrashSafetyTests`).

The kill -9 harness is a faithful port: an env-gated (`TTR_CRASH_SAVE_DIR`) loop-save mode in the real `ttr`
binary that the test spawns, does one full save so state.json exists, prints `ready`, then loop-saves; the test
SIGKILLs it mid-write and asserts state.json still parses. 5/5 survived, every kill landing on an OVERWRITE of an
existing file — the true torn-file test.

### End-to-end (tmux, real matrix fixture)

- **AC1** — ran `XunitV2.Tests`, toggled times+detail, selected `Failing_Assertion`, quit → `--continue`
  restored results, filters, selection, and expansion; two run dirs (run-complete + exit).
- **AC2** — `touch`ed the assembly (simulating a rebuild) → restored results rendered dim `\x1b[90m` Stale;
  pressing `R` reran them and the glyphs returned to bright green (staleness replaced by reality).
- **AC3** — the restored `Failing_Assertion` detail loaded lazily into the pane, and `o` opened `CalcTests.cs`
  scrolled to line 18 — both on a restored result with no rerun.
- **AC4** — targets-changed / corrupt / schema degrade to a fresh session with a specific toast (store units +
  M1 snapshot); no crash.

## Deviations from the brief / plan

1. **Per-test `finishedUtc` = the save time**, not a true per-test finish timestamp. The reducer is pure and
   deterministic (no `DateTime.UtcNow`), so finish times are stamped at save time (in side-effect land). Since
   saves fire on run-completion, this is ≈ the run's finish time and is exactly what staleness needs (assembly
   newer than the result). Faithful per-test finish times would need threading a clock through every
   `TestFinished` — not worth it for v1.
2. **Run-materialised theory rows drop on restore until a rerun.** Rows that only appear via run events
   (xUnit `[MemberData]`, non-serialisable theories) aren't re-materialised by discovery, so their restored
   results have no leaf to attach to and are dropped (counted as "N gone" in the notice). The parent shows
   `NotRun` until the user reruns, which re-materialises the rows. This is honest and matches the brief's
   drop-and-count rule; it's the one visible rough edge of restore.
3. **Test parallelism serialised for the timing suites.** The new perf (continuous 10k saves) and kill -9
   (process-spawning) suites create enough CPU/IO load to flake the millisecond bars and the MTP/inotify
   integration tests when run concurrently. Merged them and the Phase 4/5 integration + watch-fs classes into
   one `[Collection("integration")]` so they serialise (pure suites still parallelise). Suite runtime 13 s → 26 s;
   reliable across repeated runs.
4. **`TTR_CRASH_SAVE_DIR` test hook** in the CLI (env-gated loop-save, references no `Microsoft.Build` type, so
   safe before `MSBuildLocator`) — the only production-code concession to testing, and it is inert unless the
   env var is set.
5. **Restore notice cleared on the first `RunCompleted`** (the results are real again). It otherwise persists
   in the header for the whole session.

## Feed-forward for Phase 7 (packaging)

- **State-dir location is target-relative, not tool-relative.** `.ttr/` sits beside the user's primary target
  (their sln/csproj), computed from the resolved target path — NOT beside the global-tool install — so
  `dotnet tool install -g ttr` + `ttr <their solution> --continue` works with no change. `--state-dir`
  overrides for CI / read-only trees. Packaging need not special-case this.
- **The fingerprint stores absolute target paths + content hashes** (matches the "no cross-machine / portable
  state" non-goal). If a packaged ttr is ever run from a moved checkout, the fingerprint won't match and it
  degrades to a fresh session with a notice — acceptable and safe, but worth a line in the packaging docs.
- The rename note in the plan header still stands: renaming `ttr` → the shipping name is a find/replace on the
  package id AND the `.ttr/` folder name (`SessionStore` hard-codes `.ttr` via the caller-supplied stateDir, so
  the only literal is in `Cli.RunRealAsync`).
