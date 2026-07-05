# ttr — Phase 6 Brief: Sessions (`--continue`)

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is at
`docs/ttr-implementation-plan.md` (§10 is this phase's core reference; Appendix A is the
schema; POC-8's measured bars are the acceptance thresholds). Conflicts → stop and flag.

## Context and goal

ttr now runs and watches. Phase 6 makes sessions durable: state is saved automatically
after every run and on exit, and `ttr --continue` restores the previous session — results,
expansion, selection, filters, pane layout, scroll — with restored results honestly marked
**`Stale`** where the code has since changed. The persistence design was settled by POC-8
(packed JSONL + offset index, atomic renames, no gzip); this phase implements it, it does
not re-explore it.

## Explicit non-goals

- No packaging, no new CLI flags beyond making `--continue` and `--state-dir` functional
  (both already reserved). No new dependencies (System.Text.Json is in-box; default no).
- No cross-machine/portable state, no state-format migration tooling (schema is v1; a
  version mismatch on load degrades to "fresh session" with a notice, nothing cleverer).
- No UX changes beyond the M1 states; all prior snapshots unchanged.
- Do not persist mid-run partial results (save points are run-completion and exit only —
  a crash mid-run loses at most that run, per plan §10/D8).

## Milestone 0 — Reconcile Phase 5 (~30 min)

Read `docs/phase-5-notes.md` + PR, especially the Phase 6 feed-forward on result lifecycle:
watch already rewrites results via re-discovery diffs and auto-reruns, and `--continue`
must compose with it (restore → watch marks restored results `Stale` after the first
rebuild, exactly as live results go stale). Note the `TTR_WATCH_LOG` convention and the
/tmp measurement pattern — the POC-8-style timing tests here should follow the same
sandbox hygiene. Flag contradictions before building.

## Milestone 1 — Fake-first session states (protects the UX contract)

Before any real persistence:
- **`Stale` presentation**: dimmed ✓/✗ glyph variants (plan §11.2 already reserves them);
  a restored-session header notice ("restored N results from <relative time>"; toast on
  degrade cases). These render only in restored/stale situations → prior snapshots stay
  byte-identical.
- `--fake --continue` simulates a restore: tree arrives with restored pass/fail results,
  a few `Stale`, UI state (expansion/selection/filters/pane) pre-applied; then a simulated
  rebuild flips a subtree to `Stale`.
- New snapshots: restored tree with mixed fresh/Stale; the restore notice; a
  targets-changed degrade frame.

## Milestone 2 — Persistence layer (plan §10 / Appendix A / POC-8 verbatim)

- `.ttr/` beside the primary target (first sln/slnx else first csproj); `--state-dir`
  overrides; write `.ttr/.gitignore` containing `*` on first use.
- `state.json` schema v1 exactly per Appendix A: `schema`, `createdUtc`, `targets`
  (path + content hash of each project/solution file), `ui` (filters, expanded id-set,
  selected id, scroll), `tests` (id, status, durationMs, finishedUtc, hasDetail),
  `runs { keep: 3, latest }`.
- Details: `results/<runId>/details.jsonl` (one record per test with detail) +
  `details.idx` (test-id → byte offset/length). **Two atomic renames per run** (jsonl,
  idx) + one for state.json; every write is temp-file + `File.Move(overwrite: true)`.
- No compression; plain `System.Text.Json` (POC-8: source-gen made no difference — don't
  add it for this).
- Pruning: keep the 3 most recent run directories.

## Milestone 3 — Save triggers (and the jitter bar)

- Save on every `RunCompleted` (including watch auto-runs) and on clean exit (`q`/Ctrl+C
  after cancellation completes). Saves run on background tasks — the reducer/render loop
  must not stall: port POC-8's jitter test (main loop ticking at 30/s during a full save
  of a 10k-test session; p95 disturbance ≤ 10 ms).
- Crash safety: port POC-8's kill -9 harness — 5/5 interrupted saves must leave a
  parseable state.json (old or new, never torn).

## Milestone 4 — Restore (`--continue`)

- Load + validate schema and the target fingerprint. Hash mismatch or schema mismatch →
  **graceful degrade**: proceed as a fresh session with a specific notice ("targets
  changed" / "state format changed"), never a crash, never silent.
- Ordering: discovery runs as normal (the tree is always a projection of the latest
  build); restored results attach to discovered nodes **by derived `TestCaseId`**.
  Restored results whose test no longer exists are dropped (counted in the notice);
  discovered tests with no restored result are `NotRun`.
- **`Stale` marking**: a restored result whose project output assembly is newer than the
  result's `finishedUtc` renders as dimmed `Stale`, not as a fresh pass/fail. Any
  subsequent run (manual or watch) replaces staleness with reality.
- UI state applies after the tree populates: expansion set, selection (nearest-survivor
  if gone — reuse the Phase 5 logic), filters/pane/wrap/times flags, scroll offsets
  clamped to the new tree.
- `--continue --watch` composes: restore first, then watch cycles behave normally
  (their rebuilds Stale-then-replace restored results like any others).

## Milestone 5 — Lazy details + 'o' on restored results

- The detail pane and the 'o' modal work on restored results: details fetched on demand
  from the JSONL via the idx (POC-8 bar: single lazy fetch < 20 ms; do not preload).
- File references in restored details re-run existence resolution at display time
  (files may have moved since the previous session — unresolved refs go dim, exactly
  like live results).

## Milestone 6 — Tests + measured bars

- Unit: schema round-trip, fingerprint mismatch paths, Stale computation, restored-id
  attach/drop matrices, prune, `.gitignore` creation, `--state-dir`.
- Measured (POC-8 bars, CI-asserted on a generated 10k-test session): state.json save
  < 200 ms; restore index < 500 ms; lazy detail < 20 ms; jitter p95 ≤ 10 ms during save;
  footprint < 20 MB; kill -9 ×5 atomicity.
- Integration: run the matrix → quit → `--continue` → assert results/UI restored and
  Stale appears after touching a source + rebuilding; the `--fake --continue` snapshots.

## Acceptance criteria (verdict table in PR description)

- AC1: Run matrix, toggle filters/pane, select a deep node, quit; `--continue` restores
  results + expansion + selection + filters + pane + scroll (tmux demonstration).
- AC2: Touch a fixture source, rebuild (or start `--continue --watch` and let it cycle):
  affected restored results render `Stale` (dim); running them replaces staleness.
- AC3: Details and 'o' work on restored results without a run; lazy fetch < 20 ms.
- AC4: Change the target set → "targets changed" degrade notice, fresh session, no crash;
  corrupt/truncate state.json by hand → same graceful path.
- AC5: POC-8 bars all met and CI-asserted (report the numbers); kill -9 5/5.
- AC6: `runs/` prunes to 3; `.ttr/.gitignore` written once; `--state-dir` honoured.
- AC7: Saves fire after every run including watch cycles; a mid-run crash loses only the
  in-flight run (demonstrate).
- AC8: All prior snapshots unchanged; new session snapshots green; header perf numbers
  steady during a background save.

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx    # R · f on · b · select a failed test · q
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx --continue   # AC1: everything back; s and o on a restored failure (AC3)
sed -i 's/;/; \/\/ touch/' fixtures/matrix/XunitV2.Tests/Calc.cs
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx --continue --watch  # AC2: watch cycle flips the subtree Stale, rerun replaces
git checkout -- fixtures/
ls .ttr/results/            # AC6: ≤ 3 runs
dotnet run --project src/ttr.Cli -- --fake --continue              # M1 states, fake-first
```

## Deliverables

One PR: code + tests + `docs/phase-6-notes.md` (measured AC5 numbers vs POC-8's; deviations;
feed-forward for Phase 7 — anything packaging must know, e.g. state-dir behaviour when the
tool runs from a global-tool install vs the repo).
