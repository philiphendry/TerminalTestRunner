# Phase 8 backlog (post-v1)

v1's delivery phases (0–7) are complete. Phase 8 is the explicitly out-of-v1-scope future list. This file
collects it in one place; the authoritative rationale is `docs/ttr-implementation-plan.md` §14 (Phase 8) and
§16 (open items).

## Deferred features (plan §14 — Phase 8)

- **Per-test impact analysis** (coverage-map based rerun selection).
- **Mouse support.**
- **External `$EDITOR` handoff** from the 'o' modal.
- **Roslyn "instant skeleton"** pre-discovery (show the tree before the platform discovery completes).
- **Per-user global config file** (deferred from §16 item 4).
- **Themes / colour customisation** (§16 item 2 — defaults chosen in Phase 1, not made configurable).
- **Non-interactive CI mode.**
- **`--filter` expressions** (test-name / trait filtering beyond `--tfm`).
- **PDB-based file/line resolution** for 'o' instead of stack-trace string parsing (removes the
  `DOTNET_CLI_UI_LANGUAGE=en` localisation mitigation, plan §11.5).

## Tracked upstream / external (plan §16 items 7–8)

- **NUnit 4 via MTP is broken upstream** (NUnit 4.6.1 MTP host exits 134 "adapter not registered" before any
  handshake). ttr's per-project smoke validation surfaces it as a warning node by design. File / track the
  prepared upstream issue (text in `docs/phase-5-notes.md`) and re-test when NUnit / the adapter ship a fix.
- **Solution parsing at enterprise scale** (100+ projects, Unicode project/folder names, cross-folder
  duplicate names) not yet exercised (§16 item 8) — re-validate with a large fixture.
- **Lighter-weight evaluation path** for the §6.2 detection signals (design-time build caches) if the
  changed-project-only re-evaluation ever proves too slow on very large single projects (§16 item 5).

## Known v1 rough edges (documented, not bugs)

- **Restored run-materialised theory rows** drop on `--continue` until a rerun re-materialises them (counted
  in the restore notice). See `README.md` "Supported frameworks" and `docs/phase-6-notes.md` deviation 2.
- **Per-test `finishedUtc` = the run's save time**, not a true per-test finish timestamp (reducer purity;
  sufficient for staleness). `docs/phase-6-notes.md` deviation 1.
