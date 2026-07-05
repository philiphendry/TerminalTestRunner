# Phase 5 — Watch Mode

Adds `--watch`: react to source changes (mode A, default) or externally-produced builds (mode B) by
rebuilding affected projects in parallel, re-discovering, **diffing the tree by `TestCaseId`**, and
auto-rerunning the affected set — while the user keeps navigating. Closes POC-6's parallel-build conditional
(measured) and resolves both Phase 4 carry-ins (NUnit4-MTP handshake; sandbox rebuild flake). Fake mode and
every prior snapshot are unchanged — the watch header segment appears ONLY in watch mode.

Full detail, the AC5 STOP-and-report analysis, the M8 verdict, and Phase 6 feed-forward are in
`docs/phase-5-notes.md`.

## New dependencies

**None.** `Microsoft.Build.Graph.ProjectGraph` ships in the already-referenced `Microsoft.Build` package.

## Acceptance criteria

| AC | Verdict | Evidence |
|----|---------|----------|
| **AC1** Edit LibA → TestsCore AND TestsApp rebuild (parallel) + rerun; TestsX untouched. Edit TestsApp's own source → only TestsApp cycles | ✅ PASS | tmux on `fixtures/watch/Watch.sln`: touching `LibA/Calc.cs` cycles `building TestsCore`→`building TestsApp`, TestsX stays `3○`; editing TestsCore's own source rebuilds only TestsCore (`running (0/6)`). `ProjectGraphTests` (diamond closure, unrelated untouched, test-only self) + real-graph run. |
| **AC2** Add a `[Fact]` → appears (`NotRun`/`Queued`) + runs same cycle; delete → vanishes; expansion + selection survive; selecting the to-be-deleted test moves selection to nearest survivor | ✅ PASS | tmux: add `Brand_New` → 12→**13 tests**, `running (0/6)`, `✓ Brand_New`; delete → 13→**12**, gone. `WatchReducerTests` (add/keep/remove, selection-survives-both-ways, empty-branch prune, theory-across-rediscovery). |
| **AC3** All three editor-storm patterns coalesce to exactly one cycle | ✅ PASS | `WatchDebouncerTests` (deterministic accumulate/flush) + `WatchIntegrationTests` (live `FileSystemWatcher`: rename-save, in-place multi-write, 3-files-across-2-projects each → one batch) + `--fake --watch` demo. |
| **AC4** Mode B fires once per real external build, zero for a no-op build; cycle behaves identically | ✅ PASS | `WatchIntegrationTests.Mode_b_fires_once_on_an_assembly_rewrite_and_zero_on_a_noop` (500 ms quiescence). |
| **AC5** Measured T0→T4 median < 4 s warm | ⚠️ **STOP-and-report** | Measured **6.26 s** median on a normal FS (**38 s** on the sandbox's host-mounted FS). Pipeline is on-budget (debounce 0.30 + discover 1.30 + diff 0 = 1.6 s); the overage is `dotnet build` of the diamond chain at ~2.7× POC-6's real-hardware estimate. The `watch-latency` CI job asserts `< 4 s` on real Linux hardware and is authoritative. **Not a relaxed threshold** — see notes §M7. Owner must confirm the bar on CI. |
| **AC6** A change during an active run queues (header shows it) and yields exactly one consolidated follow-up cycle | ✅ PASS | `--fake --watch` shows `watch: queued (change during run)`; `WatchReducerTests.Watch_rerun_request_during_a_run_coalesces_into_one_followup`; coordinator drains all batches during the wait into one cycle. |
| **AC7** `f` active narrows watch auto-reruns to failed tests | ✅ PASS | `WatchReducerTests.Watch_rerun_request_under_failed_filter_reruns_only_failures`. |
| **AC8** All prior snapshots unchanged; new watch snapshots green; fps ≥ 25 / input p95 < 50 ms while a cycle runs | ✅ PASS (qualified) | Watch header only renders when `Watch != Off`, so every earlier snapshot is byte-identical; 7 new `WatchSnapshotTests`. Input **p95 0 ms** during cycles (tmux header). fps is change-gated (Phase 4: 29–30 fps on a 10k-node tree mid-run); the tiny watch fixture idles lower because there's nothing to redraw — not a stall (same reasoning as Phase 4 AC6). |
| **AC9** Clean shutdown from watch mid-cycle (exit codes, terminal restored, no orphans, no post-dispose watcher fire) | ✅ PASS | tmux: quit during a build → terminal restored, no `testhost`/`vstest.console` orphans. Shutdown cancels first, then disposes watchers; `WatchIntegrationTests.Disposed_source_never_fires_again`. |
| **AC10** M8 resolved to outcome (a)/(b)/(c) with evidence | ✅ PASS | **Outcome (b)** — version regression. NUnit 4.6.1 + `Microsoft.Testing.Platform.MSBuild` builds an MTP host that crashes on startup (exit 134, "test framework adapter has not been registered") before any handshake. §6.2 smoke validation catches it. Full table + upstream issue text in notes §M8. |

## Review script

```bash
# AC1/AC2/AC6 — interactive watch on the fixture (edit files in another pane):
dotnet run --project src/ttr.Cli -- fixtures/watch/Watch.sln --watch &
sed -i 's/return a + b;/return a + b; \/\/ touch/' fixtures/watch/LibA/Calc.cs   # AC1: TestsCore + TestsApp cycle, TestsX untouched
cat >> fixtures/watch/TestsCore/CalcTests.cs <<'EOF'
// AC2 (paste inside the class): [Fact] public void Brand_New() => Assert.True(true);
EOF
# observe +1 appear and run; revert; observe -1.
git checkout -- fixtures/watch            # restore the (committed) fixture sources

scripts/watch-latency.sh          # AC5: prints the T0→T4 segment table (STOP-and-report; also the CI job)
dotnet run --project src/ttr.Cli -- --fake --watch            # M1 states, fake-first (idle/detected/building/running/queued)
```

## What CI proves

- `test` (Linux/Windows/macOS): the full unit + snapshot + watch-integration suite. New: `WatchReducerTests`,
  `WatchDebouncerTests`, `ProjectGraphTests`, `WatchSnapshotTests`, `WatchIntegrationTests`.
- `watch-latency` (Linux/tmux): the **authoritative** AC5 measurement — asserts T0→T4 median < 4 s. This job
  is the durable STOP signal: if real CI hardware also exceeds 4 s, the plan §9 budget needs an owner
  decision (it is not silently relaxed).

## Notable deviations (full list in notes)

1. Watch-cycle timing uses a dedicated `TTR_WATCH_LOG` file, not `--log` (which is VSTest's trace).
2. The latency harness measures on a `/tmp` copy to strip the sandbox's host-mounted-FS artifact (a no-op on CI).
3. `--no-restore` on watch source-edit rebuilds (target set restored at startup; a project-file change re-enables restore).
