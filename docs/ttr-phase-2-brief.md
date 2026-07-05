# ttr — Phase 2 Brief: Complete Interaction Set (Fake-Only) — UX Sign-Off Phase

> **AMENDMENT (owner feedback from fake-mode review — binding):** Branch rollup counts
> (`12✓ 2✗`) must be **right-aligned in a fixed gutter at the right edge of the tree
> pane**, NOT rendered immediately after the node name. With 't' active, the right side
> is two aligned columns: `[glyph name………] [counts] [duration]`; the name's ellipsis
> budget shrinks accordingly; leaf rows leave the counts column empty. Plan §11.2 has
> been updated to match. If Phase 1 shipped counts-beside-name, migrating it is part of
> this phase; the Milestone 7 snapshots must capture the right-aligned layout. Owner
> also confirmed performance and overall layout are good — treat the rest of the Phase 1
> presentation as approved baseline, and do not restyle anything not listed here.

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is
at `docs/ttr-implementation-plan.md` (§-references point there). Where this brief and
CLAUDE.md conflict, stop and flag it.

## Context and goal

Phase 1 delivered the fake-driven MVU core, direct-ANSI renderer, virtualised tree,
navigation, `e`/`E`, spinners/glyphs, and `q`/exit codes. Phase 2 completes **every
remaining user interaction in the keymap (plan §11.3)** — still driven exclusively by
the Fake adapter. This is the **UX sign-off phase**: when it merges, the entire user
experience is final and protected by a snapshot suite; later (backend) phases may not
change UX behaviour without returning to this suite.

## Milestone 0 — Reconcile Phase 1 (do this first, ~30 min)

Read `docs/phase-1-notes.md` and the Phase 1 PR description. List in your notes: any
deviations Phase 1 recorded, the measured AC2 numbers (fps / input-latency p95 on
`--fake big`), and anything that contradicts this brief. If a Phase 1 deviation makes a
milestone below wrong as written, flag it in `docs/phase-2-notes.md` and implement the
reconciled version — do not silently follow either document. Keep the header fps/p95
instrumentation; every AC below reuses it.

## Explicit non-goals (do not build, even partially)

- Anything real: no MSBuild, no solution parsing, no VSTest/MTP adapters (Phase 3–4).
- No `--continue`, no `--watch`, no persistence of any kind (Phase 5–6).
- No packaging changes, no new CLI flags beyond what exists.
- No mouse support, no external editor handoff.
- New dependencies: **ColorCode (pin exact version) is pre-approved for the modal**;
  anything else requires a PR-description justification and default is no.

## Milestone 1 — Fake adapter completion (`files` scenario + result details)

Per plan §6.5:
- Commit a small set of real source files under `fixtures/fake-sources/` (3–5 short
  `.cs` files including one ~300 lines, one `.json`, one `.txt`, one file with an
  unrecognised extension). These exist so the 'o' modal has genuine files to open.
- `--fake files`: failures whose `TestResultDetail` messages and stack traces embed
  absolute paths (with `:line N`) into those committed files — multiple refs per failure,
  including one reference to a non-existent path (to exercise the dim/unresolved state)
  and one frame under an `obj/`-style path (must be filtered, per CLAUDE.md).
- All failing results in ALL scenarios now carry realistic `TestResultDetail`:
  message, exception chain, multi-frame stack trace, some stdout; durations on every
  result (needed for 't').
- Wire `TtrParser` (src/TtrParser) over incoming failure details to produce each
  result's ordered `FileRef` list at reduce time. Do not reimplement parsing.
- `flaky` scenario: reruns of a failed test pass with probability ~0.6 under the seeded
  RNG — this is what makes the 'f' vanish-on-pass loop demonstrable.

## Milestone 2 — Detail pane (plan §11.2)

- `s` toggles the pane; `b` toggles right (40% width) ↔ beneath (40% height).
- Content for the selected node: message, exception chain, stack trace, stdout; branch
  nodes show an aggregated failure summary (count + first N failing FQNs).
- `Tab` cycles focus tree ↔ detail (visible focus indicator); detail pane scrolls
  independently (↑/↓/PgUp/PgDn while focused).
- `w` toggles word wrap in the detail pane; wrap OFF = horizontal cell-aware truncation
  with `…` (reuse the Phase 1 helper — the pane must never bleed).
- Stack-trace lines whose `FileRef` resolved render underlined (the "'o' will work" cue).

## Milestone 3 — Failed-only filter + durations

- `f` toggles failed-only: flatten filters to failed leaves (+ their ancestor chain);
  selection survives by id or moves to nearest survivor; status bar keeps GLOBAL totals
  visible so the shrunken view isn't disorienting (plan §11.4).
- Vanish-on-pass: a test passing while 'f' is active disappears on next flatten, along
  with any branch left empty. Demonstrable end-to-end with `--fake flaky` + 'r'.
- `t` toggles durations: per-leaf right-aligned dim; branch = sum of descendant
  durations; status bar shows run wall-clock separately (plan §11.2 'Times').

## Milestone 4 — Rerun semantics on the Fake adapter (plan §11.4)

- `r` = rerun leaves under the selected node; under 'f', only currently-failed leaves.
  `R` (Shift) = same rooted at the top.
- Affected leaves → `Queued` → `Running` → terminal, streaming from the Fake adapter;
  the tree stays fully navigable throughout.
- A second `r`/`R` during an active run queues and coalesces (one consolidated follow-up
  run; plan §9 policy). Show a footer/toast hint when a rerun is queued.

## Milestone 5 — The 'o' modal (plan §11.5)

- `o` on a node/detail with resolved `FileRef`s opens an overlay ~90% of the terminal:
  title = filename + (index/total); line numbers; opened scrolled to the referenced
  line, which is highlighted.
- Syntax highlighting via **ColorCode** keyed on extension; unrecognised extensions
  render plain. Plain-text first paint, highlighting applied off the render thread.
- Keys inside the modal (which captures ALL input): `c`/Esc close, `n`/`p` next/prev
  file reference, `w` wrap toggle, ↑/↓/PgUp/PgDn/Home/End scroll.
- Closing restores the tree view exactly (selection, scroll, pane states intact).
- `o` with no resolved refs → toast "no file references", no modal. Large-file reads
  are lazy (line blocks).

## Milestone 6 — Help overlay + toasts

- `?` opens a modal-style overlay rendering the full §11.3 keymap table; any key closes.
- One-line transient toast channel (e.g. "rerun queued", "no file references"),
  auto-clearing after ~3 s, never overlapping the footer hints.

## Milestone 7 — Snapshot & interaction test suite (plan §12)

- A test-only `IUiShell` that renders frames to strings (in-memory console).
- Scripted tests = (fake scenario + seed) + (timed key script) → Verify snapshots of
  frames at defined checkpoints. Minimum coverage: default layout; detail right vs
  beneath; wrap on/off; times on/off; 'f' before/after a vanish-on-pass; modal open /
  n / scrolled / closed; help overlay; 40×10 placeholder; a resize mid-run.
- Interaction (non-snapshot) tests: focus cycling, modal input capture, queued-rerun
  coalescing, filter selection-survival.
- This suite is the **UX contract** — say so in a README note in the tests folder.

## Acceptance criteria (verdict table in PR description)

- AC1: Every key in plan §11.3 does what the table says (demonstrate via the review
  script; no dead keys, no undocumented keys).
- AC2: Perf bars hold with the new UI: `--fake big`, detail pane open, 't' on —
  ≥ 25 fps, input p95 < 50 ms (report header numbers).
- AC3: tmux capture at 80×24 / 200×50 / 100×30 / 60×15 with detail pane open AND with
  the modal open: zero over-width lines (incl. wrapped-off long stack lines and CJK).
- AC4: Vanish-on-pass loop works end-to-end: `--fake flaky` → 'f' → 'r' → passed tests
  disappear; global totals remain visible.
- AC5: Modal opens to the referenced line highlighted; `n`/`p` traverse refs incl. the
  unresolved ref rendered dim/skipped; `/obj/` frame never appears as a target.
- AC6: Snapshot + interaction suite green in CI; TtrParser suite still green; a
  deliberate one-character UI change breaks at least one snapshot (prove the contract
  bites, then revert).
- AC7: Reconciliation performed (Milestone 0) and recorded in `docs/phase-2-notes.md`.

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- --fake files --fake-seed 7
#  s, Tab, scroll detail, b, w, t, o, n, p, w, c, ?, q
dotnet run --project src/ttr.Cli -- --fake flaky --fake-seed 7
#  f, select a failed branch, r — watch passes vanish; R; queue a second R mid-run
dotnet run --project src/ttr.Cli -- --fake big
#  s, t, navigate hard — read fps / p95 from header
tmux capture-pane -p | awk -v w=$(tput cols) 'length > w {print "OVERWIDE", NR}'
```

## Deliverables

One PR: code + tests + `docs/phase-2-notes.md` (measured AC2/AC3 evidence, Milestone 0
reconciliation, deviations with reasons, and anything that should feed the Phase 3
brief). After this merges, the UX is frozen: subsequent phases change behaviour behind
`ITestSessionAdapter`, not in front of it.
