# ttr.Tests — the UX contract

This project is the **UX contract** for ttr (plan §12, Phase 2 brief M7). After Phase 2, the entire
user experience is frozen: later (backend) phases change behaviour *behind* `ITestSessionAdapter`,
not in front of it, and **must not** alter these snapshots without deliberately returning to this
suite and re-reviewing every change.

## What's here

- **`SnapshotTests`** — drive a deterministic fake scenario + a timed key script through the pure
  reducer, render a frame via the in-memory `StringUiShell`, and assert it against a committed
  `*.verified.txt` snapshot (Verify). Covers: default layout, times on/off, detail right/beneath,
  wrap on/off, the failed-only filter before/after a vanish-on-pass, the 'o' modal
  (open / next-ref / scrolled / closed), the help overlay, the 40×10 placeholder, and a resize
  mid-run. Frames are ANSI-stripped and machine paths scrubbed to `{BASE}` so snapshots are
  deterministic across machines.
- **`InteractionTests`** — behaviours a frame can't fully pin: Tab focus cycling, detail-focus
  scroll isolation, modal input capture, `n`/`p` ref traversal (incl. the unresolved ref),
  queued-rerun coalescing, filter selection-survival, and the syntax highlighter's colour mapping.
- **`ReducerTests` / `FakeAdapterTests` / `CellsTests` / `TtrParserTests`** — pure logic
  (reducers, diffing, truncation, the file-reference parser + its fixture-driven corpus).

## Running / updating snapshots

```
dotnet test tests/ttr.Tests
```

A snapshot change writes `*.received.txt` next to the `*.verified.txt`. Review the diff; if the new
frame is correct, accept it (replace `.verified.txt` with the `.received.txt` content). A *wrong*
one-character UI change will break at least one snapshot — that is the contract doing its job.

> Build note: some sandbox/host environments leak an absolute drive path into NuGet restore. The
> repo `NuGet.config` + `Directory.Build.props` pin restore to a repo-local `.packages` folder to
> keep restore deterministic. On a clean machine / CI this is a no-op.
