# ttr — Phase 5 Brief: Watch Mode

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is at
`docs/ttr-implementation-plan.md` (§9 is this phase's core reference). Conflicts → stop and flag.

## Context and goal

ttr is a working runner (Phases 0–4). Phase 5 adds `--watch`: react to source changes (mode A,
default) or externally-produced builds (mode B) by rebuilding affected projects in parallel,
re-discovering, **diffing the tree by `TestCaseId`**, and auto-rerunning the affected set —
while the user keeps navigating. This phase also **closes the last Phase 0 conditional**:
POC-6's < 4 s cycle bar was met only by arithmetic; here it gets measured for real.

## Explicit non-goals

- No `--continue`/persistence (Phase 6). No packaging (Phase 7). No new dependencies (default no).
- No per-test impact analysis — affected = changed project + transitive dependents, per D5.
- No cancel-and-restart on change: cycles QUEUE behind an active run and coalesce (plan §9).
- No UX changes outside the new watch states defined in M1; all existing snapshots unchanged.

## Milestone 0 — Reconcile Phase 4 + sandbox strategy (~45 min)

Read `docs/phase-4-notes.md` + PR. Two items are load-bearing this phase:
1. **The sandbox NuGet mount leak makes in-app rebuild flaky** — and watch mode IS in-app
   rebuild in a loop. Before building anything: reproduce the flake, spend a bounded ~60 min
   attempting a root fix (env-scoped `NuGet.config` handed to every spawned `dotnet build`,
   `MSBUILDDISABLENODEREUSE` if node reuse is implicated, explicit `--packages` dir). If not
   fixable, codify the mitigation in `sbx/` and design the watch integration tests to be
   CI-authoritative with a documented local-sandbox caveat. Record the outcome either way.
2. The Phase 4 orchestrator's run queue/coalesce is the substrate for watch cycles — reuse it;
   do not build a second queue.

## Milestone 1 — Fake-first watch states (protects the UX contract)

Per the established pattern, before any real watcher exists:
- Header gains a watch segment shown ONLY in watch mode: `watch: idle` / `change detected` /
  `building <proj>` / `running (n/m)` / `queued (change during run)` (plan §9). Non-watch
  snapshots therefore stay byte-identical.
- `--fake --watch` simulates full cycles on a timer: file-change → build spinner on a project →
  re-discovery diff that ADDS one new test and REMOVES one (exercising diff rendering) →
  auto-rerun of the affected subtree. Include one simulated cycle arriving mid-run to show
  the queued state.
- New snapshots: each watch header state; the tree before/after an add/remove diff;
  a queued-cycle frame.

## Milestone 2 — ProjectGraph service (`ttr.Build`)

Plan §7/§9: load `Microsoft.Build.Graph.ProjectGraph` over the target set at startup (it's
already a referenced package family — no new deps); hold in memory; reverse-dependents
closure API; reload only when `.csproj`/solution files change. Unit tests replicate POC-6's
three correctness cases (chained/diamond deps; unrelated project untouched; test-project-only
change implicates only itself).

## Milestone 3 — Re-discovery diffing (plan §8)

Pure reducer work + adapter re-discovery:
- Re-discover ONLY changed projects (changed-project-only re-evaluation too — CLAUDE.md rule);
  diff old vs new node sets by derived `TestCaseId` → add/remove/keep events.
- Expansion and selection survive by id; a removed selected node moves selection to the
  nearest surviving sibling/parent; new tests appear `NotRun` (or `Queued` if the cycle's
  auto-rerun includes them); results of KEPT tests are preserved (they'll be rerun anyway if
  affected, but never blanked by the diff itself).
- Unit tests: add/remove/keep matrices, selection-survival cases, theory-case placeholder
  behaviour across a re-discovery.

## Milestone 4 — Watch mode A (source; the bare `--watch` default)

Plan §9 verbatim: `FileSystemWatcher` per project directory filtered by evaluated `Compile`/
content globs + `*.csproj/*.props/*.targets` (project-file change additionally invalidates
that project's evaluation cache + re-runs detection); 300 ms trailing-edge debounce;
editor-storm coalescing — port POC-6's three patterns (temp-write+move rename save, in-place
write, 3-files-across-2-projects burst) as integration tests, each must yield exactly ONE
cycle; changed project(s) → dependents closure → **parallel build of affected test projects**
→ on success re-discover → diff → auto-rerun affected (narrowed to failed-only when 'f' is
active); build failure surfaces the Phase 3 error node and the cycle stops before discovery.
Changes during an active run/build queue and coalesce into one consolidated cycle (M0.2).

## Milestone 5 — Watch mode B (external builds)

Plan §9: watch the per-TFM **primary output assemblies only**; 500 ms size/lastWrite
stability quiescence (exclusive-open as belt-and-braces only — Linux has no mandatory
locking); a real `dotnet build` you run in another pane fires exactly one cycle; a no-op
incremental build fires zero (assemblies aren't rewritten — POC-6). Then re-discover → diff
→ auto-rerun as mode A.

## Milestone 6 — CLI + lifecycle

`--watch [build|external]`, bare `--watch` = build/source mode (§16 resolution). Watch mode
with a target containing zero test projects stays alive with a notice instead of exit 2
(plan §4). `q`/Ctrl+C during watch: dispose watchers, cancel any active cycle via the
Phase 4 cancellation paths, terminal restored, exit codes unchanged. Ensure watcher events
can never fire into a disposed pipeline (shutdown ordering test).

## Milestone 7 — Close POC-6 AC2: the real latency measurement

- Port/recreate the POC-6 10-project fixture (`fixtures/watch/`: LibA..F diamond, TestsCore,
  TestsApp, LibX/LibY + TestsX) — it doubles as the dependents-correctness integration bed.
- Scripted harness (CI job + runnable locally): touch one `.cs` in LibA; record T0 save →
  T1 debounce → T2 parallel builds done → T3 re-discovery done → T4 diff done → T5 rerun
  complete for ≥ 5 warm cycles; report min/median/p95 per segment.
- **Exit criterion: T0→T4 median < 4 s warm with parallel builds** (the plan's bar; T5
  reported for information). If the bar fails, that is a STOP-and-report, not a
  quietly-relaxed threshold — the plan's §9 budget says ~3 s should be achievable.

## Milestone 8 — NUnit4-MTP investigation (timeboxed 90 min)

Phase 4 could not complete the NUnit4 MTP server handshake, **contradicting POC-3's
empirical pass** of `dotnet exec <dll> --server --client-port <port>` on the
`EnableNUnitRunner` fixture. Bounded investigation: rebuild a minimal fixture per
docs.nunit.org with current package versions; compare the exact package set/versions POC-3
pinned (its FINDINGS list NUnit 4.6.1 + NUnit3TestAdapter 6.2.0) vs Phase 4's; capture the
host's stderr/exit behaviour under `--server`. Outcomes (any is acceptable): (a) working
fixture → add NUnit4-MTP to the matrix and run it; (b) a version-specific regression →
document versions affected, keep the fixture with the smoke-validation warning node as the
*designed* behaviour, add a §16 open item + upstream issue text in the notes; (c) timebox
expires → (b)'s documentation path. What is NOT acceptable: silence.

## Acceptance criteria (verdict table in PR description)

- AC1: Edit a file in LibA → TestsCore AND TestsApp rebuild (parallel) and rerun; TestsX
  untouched. Edit TestsApp's own source → only TestsApp cycles.
- AC2: Add a `[Fact]` → it appears in the tree (`NotRun`/`Queued`) and runs in the same
  cycle; delete it → it vanishes; expansion + selection survive both; selecting the
  to-be-deleted test first moves selection to the nearest survivor.
- AC3: All three editor-storm patterns coalesce to exactly one cycle each (integration
  tests + tmux demonstration).
- AC4: Mode B fires once per real external build, zero for a no-op build, and its cycle
  behaves identically downstream.
- AC5: **Measured T0→T4 median < 4 s warm** on the watch fixture with the per-segment
  table in the PR — POC-6's conditional formally closed (or STOP per M7).
- AC6: A change landing during an active run queues (header shows it) and yields exactly
  one consolidated follow-up cycle.
- AC7: 'f' active narrows watch auto-reruns to failed tests; a watched fix vanishes
  per the Phase 2 loop.
- AC8: All prior snapshots unchanged; new watch-state snapshots green; fps ≥ 25 / input
  p95 < 50 ms while a cycle runs (header numbers).
- AC9: Clean shutdown from watch mode mid-cycle (exit codes, terminal restored, no
  orphans, no post-dispose watcher fire).
- AC10: M8 resolved to outcome (a), (b), or (c) with evidence.

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- fixtures/watch/Watch.sln --watch &
sed -i 's/return a + b;/return a + b; \/\/ touch/' fixtures/watch/LibA/Calc.cs   # AC1: watch both test projects cycle
cat >> fixtures/watch/TestsCore/CalcTests.cs <<'EOF'
// AC2 (paste inside the class): [Fact] public void Brand_New() => Assert.True(true);
EOF
# observe +1 appear and run; revert; observe -1. Then R mid-edit for AC6.
git checkout -- fixtures/
scripts/watch-latency.sh          # AC5: prints the T0→T4 segment table
dotnet run --project src/ttr.Cli -- --fake --watch   # M1 states, fake-first
```

## Deliverables

One PR: code + fixtures + tests + `docs/phase-5-notes.md` (the AC5 segment table; the M0
sandbox-flake outcome; the M8 NUnit4-MTP verdict with evidence; deviations; feed-forward
for Phase 6 — especially anything watch teaches about result lifecycle that `--continue`'s
`Stale` marking should respect).
