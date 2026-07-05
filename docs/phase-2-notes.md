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

(Further deviations appended as they arise.)
