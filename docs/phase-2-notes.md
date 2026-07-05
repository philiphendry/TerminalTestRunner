# Phase 2 notes — Complete Interaction Set (Fake-Only) — UX Sign-Off

This phase completes every remaining key in the §11.3 keymap against the Fake adapter and
locks the UX behind a Verify snapshot suite. Live updates keep the Phase 1 MVU / direct-ANSI
architecture; every new render-thread feature reads published snapshots only (never the live
tree — the Phase 1 hazard).

## Milestone 0 — Phase 1 reconciliation

Read `docs/phase-1-notes.md` and the Phase 1 commit (`134696c`). Findings:

- **Measured AC2 baseline (kept):** `--fake big` (10k), 120×40, seed 42 — **fps 29**, **input
  p95 33 ms** during the result storm. The header fps/p95 instrumentation
  (`RenderMetrics`) is retained; Phase 2 ACs reuse it.
- **No contradictions** between the Phase 1 build and this brief's presentation baseline. The
  right-aligned counts gutter (brief AMENDMENT / plan §11.2) was **not** shipped in Phase 1 —
  Phase 1 rendered `counts` immediately after the node name. Migrating to the fixed
  right-edge gutter is done here (M3), as the amendment requires.
- Phase 1 deviation #3 (assertion-based frame tests, Verify not added) is **superseded**: the
  brief M7 and plan §12 mandate Verify snapshots as the UX contract, so Verify is added this
  phase (see Deviations).

## Deviations from the brief (with reasons)

1. **TtrParser implemented to spec, not lifted (brief M1 conflict — flagged per CLAUDE.md).**
   Brief M1 says "Wire TtrParser … Do not reimplement parsing." But the POC-9 parser + corpus
   were never in the seed (`src/TtrParser` is an empty placeholder; confirmed absent again this
   phase — no corpus dir, no stash, nothing to lift). The 'o' modal cannot exist without a
   parser, so `TtrParser` is implemented **faithfully to the authoritative spec** — plan §11.5
   and the POC-9 prompt (`docs/ttr-poc-prompts_1.md`, "Prompt — POC-9"): the
   `Parse(message, stackTrace, projectDir) → IReadOnlyList<FileRef>` signature, the
   ` in <path>:line <N>` and `path(line,col):` and bare-path forms, trace-order-then-message
   ordering, `(path,line)` dedup, `/obj/` + `.g.cs` filtering, and existence resolution. When
   the real POC-9 artifacts surface, this should be replaced verbatim and its corpus swapped in.
   The parser ships with its own test suite built from the committed `fixtures/fake-sources/`.

2. **Verify added as a test dependency (brief allows with justification).** Non-goals say new
   deps need PR justification; M7/§12 explicitly call for Verify snapshots, which is the
   justification. `Verify.Xunit` is pinned. `.verified.txt` frames are committed; machine
   paths in `files`-scenario frames are scrubbed to `{FIXTURES}` for determinism.

3. **ColorCode.Core (pre-approved) with a hand-written ANSI formatter.** The pre-approved
   ColorCode is used only for tokenisation (`CodeColorizerBase`); an ANSI formatter maps its
   scopes to terminal colours (ColorCode's shipped formatters emit HTML).

4. **Reduce-time file existence check.** Brief M1 requires FileRef resolution "at reduce time";
   resolution needs `File.Exists`, so the reducer performs a filesystem read on failed results
   carrying detail. It stays deterministic w.r.t. committed fixtures and is bounded (only
   failed leaves, only when detail is present). Documented so the purity relaxation is explicit.

5. **Repo-local NuGet package folder + restore pins (build infra).** This sandbox intermittently
   leaks an absolute host drive path (`p:\packages`, `C:\Program Files\…`) into NuGet restore —
   parallel restore workers race on config resolution and write a wrong package folder into some
   projects' `project.assets.json`, causing `NETSDK1064`/"Unable to find fallback package folder".
   `NuGet.config` pins a repo-local `.packages` folder (gitignored) and clears fallback folders;
   `Directory.Build.props` sets `RestoreConfigFile`/`DisableImplicitNuGetFallbackFolder`/
   `RestoreDisableParallel`. On a clean machine/CI these are no-ops. `sbx/build-test.sh` captures the
   full deterministic sequence for the sandbox (clean serial restore → lock outputs read-only →
   `--no-restore` build/test). Flagged as environment-specific, not a product concern.

## Acceptance-criteria verdict table

| AC | Result | Evidence |
|----|--------|----------|
| **AC1** every §11.3 key does what the table says; no dead/undocumented keys | ✅ PASS | Review script driven in tmux; every key exercised (nav, e/E, f/s/b/w/t, r/R, o, Tab, ?, q/Ctrl+C, modal c/n/p/w/scroll). The reducer handles exactly the documented keys — unhandled keys are no-ops (Phase 1 `Revision_bumps_on_change_only` still green). |
| **AC2** `--fake big`, detail pane open, 't' on — ≥25 fps, input p95 < 50 ms | ✅ PASS | 120×40, `big --fake-seed 42`, `s`+`t`, hammering Down/PgDn through the 10k result storm: header read **fps 29–30**, **p95 31–32 ms**. |
| **AC3** zero over-width at 80×24/200×50/100×30/60×15 with detail pane AND modal open (incl. wrapped-off long stack lines and CJK) | ✅ PASS | Cell-width checker (East-Asian W/F = 2): **0 over-width** at all four sizes with detail-right/beneath+times (incl. `big` CJK/emoji rows) and with the 'o' modal open. |
| **AC4** vanish-on-pass end-to-end: `flaky` → f → r → passers vanish; global totals stay visible | ✅ PASS | tmux: `flaky`+`f`+`R` dropped 20✗→10✗; passed leaves and emptied branches vanished from the filtered view; header kept global totals (62✓ 10✗). Also snapshot `Filter_after_vanish`. |
| **AC5** modal opens to referenced line highlighted; n/p traverse incl. the unresolved ref (dim); `/obj/` frame never a target | ✅ PASS | `Modal_open` snapshot opens scrolled to line 17 (reverse-highlighted); `Modal_next_ref` + `Modal_n_p_traverse_all_refs_including_unresolved` step to the dim `[unresolved]` frame; the `obj/…​.g.cs` frame is dropped by the parser (`TtrParserTests`, only 3 refs surface). |
| **AC6** snapshot + interaction suite green; TtrParser suite green; a 1-char UI change breaks ≥1 snapshot | ✅ PASS | **85 tests green** (15 Verify snapshots + interaction + reducer + parser). A one-char footer edit (`q quit`→`q Quit`) broke `Resize_mid_run` (proven, then reverted). |
| **AC7** Milestone 0 reconciliation recorded | ✅ PASS | See §"Milestone 0" above. |

## Something for the Phase 3 brief

- The reducer now performs bounded filesystem reads at reduce time (parser existence checks; modal
  file reads). When real adapters arrive, per-test `projectDir` should be threaded through so
  relative stack-frame paths resolve (fake uses absolute paths). Keep these reads off any hot path.
- The Orchestrator effect-launch seam (reruns / highlight / toast expiry, launched from the reducer
  loop's post-reduce hook) is where real build+discovery+run side effects will attach in Phase 3.
- Wall-clock is tracked in the render loop (reducer is clock-free); real run timing should keep that
  split. `RunGeneration`/`RunSubset`/`QueuedRerun` model coalescing generically for watch mode too.
