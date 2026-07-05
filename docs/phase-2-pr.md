# Phase 2 — Complete Interaction Set (Fake-Only) — UX Sign-Off

Branch: `phase-2-interaction-set` (no remote configured; local commits, as in Phase 1).

Completes every remaining key in the §11.3 keymap against the Fake adapter and freezes the UX
behind a Verify snapshot suite. One PR = one phase.

## What shipped

- **M1 — fake `files` scenario + result details + parser.** `fixtures/fake-sources/` (Calculator,
  StringUtilities, PaymentProcessor ~300 lines, config.json, notes.txt, layout.tmpl) copied to
  build output. All scenarios' failures carry `TestResultDetail`; `files` embeds real absolute
  fixture refs (multi-ref, one unresolved, one `obj/…​.g.cs` that must be filtered).
  `TtrParser` implemented to the §11.5/POC-9 spec (the artifacts were never seeded — see deviation)
  and wired at reduce time. `flaky` reruns re-roll to pass ~0.6 (the vanish-on-pass driver).
- **M2 — detail pane.** `s` toggle, `b` right↔beneath, `Tab` focus, `w` wrap, independent scroll,
  branch failure-summary aggregation, resolved stack frames underlined.
- **M3 — failed-only filter + durations.** `f` filters to failed leaves (+ in-flight) with
  vanish-on-pass and selection-survival; global totals stay in the header. `t` shows a
  right-aligned duration column (the amendment's fixed gutter: `[glyph name…] [counts] [duration]`),
  branch sums, and separate run wall-clock.
- **M4 — rerun.** `r`/`R` via an Orchestrator effect seam (Queued→Running→terminal); a second
  request mid-run coalesces into one consolidated follow-up; a "rerun queued" toast.
- **M5 — 'o' modal.** ~90% overlay, title = file + (i/total), line-number gutter, jump-to-line
  highlight, `c`/Esc/`n`/`p`/`w`/scroll, ColorCode.Core tokenisation → colour spans applied off the
  render thread (plain first paint); unresolved refs traversable + dim; captures all input;
  restores the tree exactly on close; "no file references" toast when nothing resolves.
- **M6 — help overlay + toasts.** `?` renders the full keymap; any key closes. Transient toast
  channel auto-clears ~3 s, never overlapping the footer.
- **M7 — the UX contract.** In-memory `StringUiShell`, 15 Verify frame snapshots + interaction
  tests. 85 tests green.

## Acceptance criteria

See the verdict table in `docs/phase-2-notes.md` — **AC1–AC7 all PASS** (fps 29–30 / p95 31–32 ms
on `--fake big` with detail+times; 0 over-width at four sizes with detail and modal open;
vanish-on-pass demonstrated; modal jump-to-line + n/p incl. unresolved; obj/.g.cs never a target;
a one-char change breaks a snapshot).

## New dependencies

- **ColorCode.Core 2.0.15** — pre-approved in the brief; used only for tokenising the 'o' modal
  (a hand-written span→ANSI mapper does the colouring).
- **Verify.Xunit 31.12.5** — the snapshot suite is the UX contract (brief M7 / plan §12).

## Deviations (full detail in docs/phase-2-notes.md)

1. **TtrParser implemented to spec, not lifted** — the POC-9 artifacts were never in the seed
   (`src/TtrParser` was an empty placeholder). Built faithfully to plan §11.5 + the POC-9 prompt,
   with a fixture-driven test suite; replace verbatim + swap the corpus when the artifacts arrive.
2. Verify added (justified above). Reduce-time filesystem reads (parser existence checks, modal
   file reads) — bounded, documented.
3. **Build infra** — a sandbox/host quirk intermittently leaks an absolute drive path into NuGet
   restore; `NuGet.config` + `Directory.Build.props` pin restore deterministically (no-op on clean
   CI), and `sbx/build-test.sh` documents the sandbox build sequence.

## Review script

```bash
dotnet run --project src/ttr.Cli -- --fake files --fake-seed 7   # s, Tab, scroll, b, w, t, o, n, p, w, c, ?, q
dotnet run --project src/ttr.Cli -- --fake flaky --fake-seed 7   # f, select failed branch, r (watch vanish), R, queue R mid-run
dotnet run --project src/ttr.Cli -- --fake big                   # s, t, navigate — read fps/p95 from header
```
