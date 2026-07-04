# ttr — Phase 1 Brief: Repo Scaffold + Fake-First UI Core

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the full plan
is at `docs/ttr-implementation-plan.md` (§-references below point there). Where this brief
and CLAUDE.md conflict, stop and flag it.

## Goal

Stand up the production repository and deliver a **fully navigable, live-updating test
tree driven entirely by the Fake adapter** — reviewable by running `ttr --fake`. At the
end of this phase a reviewer can watch a simulated test run stream into a virtualised
tree, navigate it while it runs, expand/collapse nodes, and quit cleanly. **No MSBuild,
no VSTest, no MTP, no persistence, no watch mode exists after this phase.**

## Explicit non-goals (do not build these, even partially)

- Detail pane, filters ('f'), pane toggles ('b'), wrap ('w'), durations ('t'),
  rerun ('r'/'R'), the 'o' modal, toasts — all Phase 2.
- Real target selection, solution parsing, MSBuild anything — Phase 3.
- `--continue`, `--watch`, `--no-build`, `--tfm`, `--state-dir` flags — later phases
  (the CLI parser may *reserve* them as defined-but-rejecting with "not yet implemented").
- Any new NuGet dependency beyond: Spectre.Console (measurement only),
  System.CommandLine, xUnit + Verify for tests. Pin exact versions.

## Milestone 0 — Repository scaffold

1. Solution `ttr.sln` with the six projects from CLAUDE.md's layout (`ttr.Cli`, `ttr.Ui`,
   `ttr.Core`, `ttr.Runners`, `ttr.Build` (empty placeholder), `TtrParser`) + `tests/`.
2. Commit `docs/ttr-implementation-plan.md`, `docs/ttr-poc-prompts.md`, `CLAUDE.md`
   (all provided in the repo seed).
3. **TtrParser lift:** if the POC-9 artifacts (`TtrParser/` library + `corpus/` +
   its test project) are present in the seed, lift them verbatim into `src/TtrParser`
   and `tests/` and get their 108 tests green. If absent, create the empty project,
   note it in the PR, and continue — do not reimplement the parser.
4. CI workflow: `dotnet build` + `dotnet test` on Linux (Win/macOS lanes are Phase 4).
5. `Directory.Build.props`: net10.0, nullable enable, treat warnings as errors,
   pinned central package versions.

## Milestone 1 — Domain model + MVU core (`ttr.Core`)

Per plan §5/§5.1:
- `TestCaseId` (derived identity incl. param display hash), `TestNode` tree with kinds
  (Solution/Project/Tfm/Namespace/Class/Method/Case), `TestStatus`
  (NotRun/Queued/Running/Passed/Failed/Skipped/Stale), rollup counters + rollup duration.
- `AppEvent` union (start from Appendix B; implement only what Phase 1 emits:
  `TestsDiscovered`, `TestStarted`, `TestFinished`, `RunCompleted`, `KeyPressed`,
  `Resized`, `FatalError`).
- Single `Channel<AppEvent>` + reducer loop producing `AppState` snapshots
  (tree, expansion set, selection id, scroll offset, run totals).
- Pure-function reducers with xUnit tests: discovery inserts (incl. children appearing
  mid-run — the 1→N theory shape), status transitions, rollups, selection survival on
  tree change, expansion toggle single (`e`) and recursive (`E`).

## Milestone 2 — Fake adapter (`ttr.Runners`)

Per plan §6.5 — this is a first-class feature, not a stub:
- Implements `ITestSessionAdapter` (define the interface per plan §6.1 now; Fake is its
  only implementation this phase).
- Scenarios: `default` (~300 tests, 3 namespaces, mixed pass/fail/skip, realistic name
  lengths incl. some >120 chars and some CJK/emoji names), `slow` (a handful of 5–15 s
  tests running concurrently — the spinner/navigation showcase), `big` (10,000 tests for
  perf verification), `flaky` (nondeterministic failures — used properly in Phase 2 but
  cheap to include now). `files` scenario may be deferred to Phase 2 with a TODO.
- Deterministic under `--fake-seed <int>`; emits events with realistic pacing
  (staggered discovery batches, then interleaved started/finished).
- Include at least one "theory" whose case children appear only via run events.

## Milestone 3 — Direct-ANSI renderer (`ttr.Ui`)

Per plan §11.2 and CLAUDE.md invariants 2–5:
- `IUiShell` seam; ANSI implementation: alternate screen on entry (`\x1b[?1049h`),
  restore + cursor show on ANY exit path (including unhandled exceptions — wrap in
  try/finally so a crash never wrecks the user's terminal).
- Frame writer: cursor-home, write visible rows, clear-to-end-of-line per row; ≤30 fps,
  dirty-flag gated; spinner tick marks dirty while anything is Running.
- Cell-aware truncation helper built on `Segment.CellCount()` with `…`; unit-tested
  against ASCII / CJK / emoji strings at odd widths.
- Per-frame `Console.WindowWidth/Height` poll → relayout; <40×10 → placeholder screen.
- Layout this phase: 1-line header (scenario name, run totals, live fps + input-latency
  p95 readouts — keep these; they make every future review quantitative), tree pane
  (full remaining height), 1-line footer with key hints.

## Milestone 4 — Virtualised tree view + input

- Flatten(expansion) → viewport slice `[scroll, scroll+rows)`; selection clamped and
  kept in view; ↑/↓ (and k/j), PgUp/PgDn, Home/End.
- `e` toggle expand/collapse of selected; `E` (Shift — `KeyChar 'E'`) recursive toggle.
- Status glyphs per plan §11.2: braille spinner, green ✓, red ✗, ○ not-run, ⊘ skipped;
  branch rollups (spinner if any descendant running; counts like `12✓ 2✗`).
- Input thread → `KeyPressed` events; `q` and Ctrl+C exit cleanly (exit code 0;
  130 for Ctrl+C), `?` shows a one-line "help coming in Phase 2" toast-in-footer.

## Milestone 5 — CLI wiring (`ttr.Cli`)

- System.CommandLine: `ttr --fake [scenario] [--fake-seed <int>]`. Running with no
  `--fake` prints "real targets arrive in Phase 3" and exits 2. Reserved flags from the
  non-goals list are defined but reply "not yet implemented" (exit 2).

## Acceptance criteria (verdict table in PR description, POC-style)

- AC1: `ttr --fake` streams discovery then results live; tree navigable throughout;
  `ttr --fake slow` shows concurrent spinners while arrow keys stay responsive.
- AC2: `ttr --fake big` (10k nodes): sustained ≥ 25 fps and input-latency p95 < 50 ms
  during the result storm (read from the header instrumentation; report numbers).
- AC3: tmux `capture-pane` at 80×24, 200×50, 100×30, 60×15: **zero lines exceed the
  terminal width**, including CJK/emoji rows; 40×10 shows the placeholder; resizing
  mid-run never corrupts the screen; terminal state restored on quit AND on a forced
  crash (test with an injected exception).
- AC4: `e` vs Shift+`E` behave per spec; expansion/selection survive incoming events.
- AC5: reducer + truncation unit tests green; TtrParser suite green (if lifted);
  CI green on Linux.
- AC6: exit codes — `q`→0, Ctrl+C→130, no `--fake`→2.

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- --fake              # watch, navigate, e/E, q
dotnet run --project src/ttr.Cli -- --fake slow         # spinners + nav during run
dotnet run --project src/ttr.Cli -- --fake big --fake-seed 42   # note fps / p95 in header
# in tmux: resize the pane repeatedly during 'slow'; then:
tmux capture-pane -p | awk -v w=$(tput cols) 'length > w {print "OVERWIDE", NR}'
```

## Deliverables

One PR: scaffold + code + tests + a short `docs/phase-1-notes.md` recording measured
AC2 numbers, any deviations from this brief (with reasons), and anything learned that
should feed the Phase 2 brief.
