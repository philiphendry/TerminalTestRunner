# Phase 1 notes — Repo Scaffold + Fake-First UI Core

Delivered: a fully navigable, live-updating test tree driven entirely by the Fake adapter,
reviewable with `ttr --fake`. No MSBuild / VSTest / MTP / persistence / watch exists yet.

## Acceptance-criteria verdict table

| AC | Result | Evidence |
|----|--------|----------|
| **AC1** streaming + navigable; `slow` concurrent spinners, responsive nav | ✅ PASS | `--fake default` streams discovery→results into a live tree; `--fake slow` shows 6 concurrent braille spinners on 5–15 s tests while arrow keys stay responsive (p95 26 ms during the run). |
| **AC2** `big` (10k): ≥25 fps, input p95 < 50 ms during the storm | ✅ PASS | 120×40, `--fake big --fake-seed 42`, sustained during the result storm while hammering nav keys: **fps 29**, **p95 33 ms** (read from header instrumentation). Also clean at 200×50. |
| **AC3** zero over-width at 4 sizes; 40×10 placeholder; resize mid-run clean; terminal restored on quit AND forced crash | ✅ PASS | Cell-width checker (`unicodedata`-based) reports **0 over-width lines** at 80×24, 200×50, 100×30, 60×15 — including the CJK/emoji/140-char rows. 40×10 → "terminal too small" placeholder. Mid-run `resize-window` across 80×24↔200×50↔60×15 stayed clean. `TTR_CRASH=1` (injected exception after alt-screen entry) leaves the terminal fully usable: normal screen restored, follow-up command runs, `cursor_flag=1`. |
| **AC4** `e` vs `Shift+E`; expansion/selection survive events | ✅ PASS | Unit tests (`Toggle_e_…`, `Shift_E_…`, `Selection_survives_tree_changes`) + visual: `E` on root collapses the whole subtree, `e` expands one level. Selection keys on the stable derived id so it survives incoming discovery. |
| **AC5** reducer + truncation tests green; TtrParser suite; CI green on Linux | ✅ / N-A | 55 tests green (reducer, truncation, frame, fake adapter). TtrParser suite **N/A** — POC-9 artifacts absent from the seed (see deviations). CI workflow builds+tests on Linux; local Release build is warnings-as-errors clean. |
| **AC6** exit codes: `q`→0, Ctrl+C→130, no `--fake`→2 | ✅ PASS | Measured in tmux via a `$?`-capturing wrapper: `q → RC=0`, `C-c → RC=130`. `--fake nope`, `--continue`, bare invocation all → exit 2. |

### AC2 measured numbers (report)

- Terminal 120×40, scenario `big` (10,000 tests), seed 42, during the interleaved result storm
  while continuously sending `Down`/`Up`/`PgDn`:
  - **fps: 29** (bar ≥ 25; capped at 30 by the render loop)
  - **input-latency p95: 33 ms** (bar < 50 ms)
- 200×50 `big`: 0 over-width lines throughout.
- `default` idle-ish fps ~28; `slow` shows ~12 fps — expected: when only the spinner animates,
  the dirty signal is the 80 ms spinner tick (~12.5 fps), which is correct, not a regression.

## Architecture as built

- **MVU on one channel.** `Channel<AppEvent>` → single `ReducerLoop` consumer → pure `Reducer`
  producing `AppState` snapshots. Render + input are separate threads; no locks.
- **Virtualised tree.** The reducer (the single writer) flattens the expanded tree and publishes
  the row list on `AppState.Rows`; the renderer materialises only the viewport slice and reads
  only that snapshot + atomic scalar node fields. Rollups are maintained by O(depth) delta
  propagation, never O(tree).
- **Direct-ANSI renderer.** Alt-screen on entry, cursor-positioned per-row frame writes, ≤30 fps
  dirty-gated, per-frame `Console.WindowWidth/Height` poll → relayout, `<40×10` placeholder.
  Spectre is used only for cell measurement.
- **Fake adapter** with `default` / `big` / `flaky` / `slow`, deterministic under `--fake-seed`,
  staggered discovery then interleaved run events, incl. a theory whose Case children appear
  **only** via run events (the 1→N shape).

## Deviations from the brief (with reasons)

1. **TtrParser + corpus not lifted — POC-9 artifacts absent from the seed.** Per brief M0.3, the
   empty `src/TtrParser` project is created and the parser is **not** reimplemented; its 108-test
   suite is therefore not present this phase. It should be lifted verbatim when the artifacts
   arrive. `docs/ttr-poc-prompts.md` was likewise not in the seed (only the implementation plan).
2. **Cell measurement API.** The plan/CLAUDE.md cite `Segment.CellCount()`. In Spectre.Console
   0.57 the convenient forms are Spectre-internal (`Cell.GetCellLength`) or ambiguous across two
   shipped assemblies (`StringExtensions.GetCellWidth`). We use the public
   `new Segment(text).CellCount()` — same measurement, exported surface. (`Cells.cs`.)
3. **Snapshot tests use assertions, not Verify.** The brief permits Verify; AC5 mandates only
   reducer + truncation tests. To keep CI green on first run without snapshot-approval friction,
   `FrameBuilderTests` strip ANSI and assert per-row cell width / placeholder / header-footer
   content directly. **Verify was not added as a dependency.** Revisit if richer golden-frame
   coverage is wanted in Phase 2.
4. **Solution format.** The .NET 10 SDK now defaults `dotnet new sln` to `.slnx`; the brief asks
   for `ttr.sln`, so it was created with `--format sln`.
5. **`--log` reserved too.** The brief's reserved list is `--continue/--watch/--no-build/--tfm/
   --state-dir`; `--log` is also defined-but-rejecting for consistency (it is not a Phase 1
   feature either).

## Something learned that should feed the Phase 2 brief

- **The shared mutable tree is the real hazard, and it bites under load.** The first `big` run
  crashed with `InvalidOperationException: Collection was modified` — the render thread was
  enumerating a node's `Children` while the reducer appended to it. Fix: the reducer publishes
  the flattened `Rows` snapshot; the render thread never enumerates child lists. **Phase 2 must
  hold this line** — the detail pane, filters, and the 'o' modal will all be tempted to walk the
  live tree from the render thread. Any new render-thread tree access should read published
  snapshots / atomic fields only. A cheap guard would be a debug assertion that `Flatten` is only
  ever called on the reducer thread.
- Spinner cadence currently drives idle fps; if Phase 2 wants a livelier idle look, decouple the
  spinner tick from the fps readout so the number reflects capacity, not animation rate.
- `flaky` is wired but only lightly used this phase; its rerun/failed-filter value lands in Phase 2.
