# ttr — Phase 7 Brief: Packaging & Polish (FINAL v1 phase)

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is at
`docs/ttr-implementation-plan.md`. Conflicts → stop and flag. This phase ends with an
installable v1; there is no Phase 8 delivery work (Phase 8 is the future list).

## Owner inputs required (blockers for specific milestones, not for starting)

1. **Product name** (plan §16.1) — required before M4 (rename). If not provided by the time
   M4 is reached, complete every other milestone, leave M4 as a mechanical checklist in the
   notes, and flag it.
2. **Manual Windows Terminal + conhost smoke check** (owed since Phase 4) — the sandbox
   cannot perform it; M7 defines exactly what to ask the owner to run.

## Explicit non-goals

- No new features: no mouse, no `$EDITOR`, no `--filter`, no impact analysis, no themes
  (all Phase 8). No behaviour changes to anything snapshot-frozen.
- No new dependencies (default no; if signing tooling genuinely requires one, justify).

## Milestone 0 — Reconcile Phase 6 (~30 min)

Read `docs/phase-6-notes.md` + PR. Load-bearing items: the state-dir-under-global-tool
feed-forward (the `.ttr/` folder resolves against the TARGET's directory, never the tool
install dir — verify this holds when running from an installed tool, M3); the
`TTR_CRASH_SAVE_DIR` hook (keep env-gated, document as internal in M6); the serialised
integration collection (installer tests join it, don't parallelise against it).

## Milestone 1 — Flag completion & CLI truth

- `--tfm <moniker>` becomes functional: filters evaluation/discovery/run to the one TFM;
  unknown moniker for the target set → exit 2 with the valid list. Composes with
  `--watch`/`--continue`.
- `--log <path>` becomes the single diagnostics umbrella: adapter traffic (the Phase 3 MTP
  message trace + VSTest diag), build invocations + raw output, watch cycle timing
  (fold `TTR_WATCH_LOG` in as a category; keep the env var as an alias, documented),
  session store operations. One file, timestamped, category-prefixed lines.
- Sweep `--help` output and exit codes (0/1/2/3/130) against plan §3; fix drift in
  whichever direction is correct (flag if the plan is what's wrong).

## Milestone 2 — Capability detection + ASCII fallback (plan §11.2)

- Detect at startup: UTF-8 locale (glyphs), color depth (`NO_COLOR`, `TERM`), VT enable
  result on Windows (Phase 4's path). Degrade tiers: full → ASCII glyphs
  (`*` spinner frames, `+`/`x`/`o`/`s` statuses, `.` stale-dim via color or `~` prefix
  when colorless) → `TERM=dumb`/no-VT: refuse with a clear one-line message and exit 2
  (a broken TUI is worse than none).
- `TTR_ASCII=1` env override forces the fallback (testability + user escape hatch).
- New snapshot lane rendering the main screens in ASCII mode; existing snapshots unchanged.

## Milestone 3 — Global tool packaging

- `PackAsTool`, `ToolCommandName` (= the final name, or `ttr` pending M4), version 1.0.0
  (+ CI `--version-suffix` for prerelease builds), LICENSE, icon-less is fine, README
  packed. CI publishes the `.nupkg` as a build artifact and a `release` job packs on tag.
- **Clean-machine install test (scripted, CI):** in a fresh container/user with only the
  .NET 10 SDK: `dotnet tool install --add-source <local feed> -g <name>` → run against a
  copy of the fixture matrix → discovery AND a run must work from the installed location.
  This specifically re-proves POC-1's Strategy A (SDK-resolved vstest.console) from a
  global-tool install path, which is the scenario it was designed for.
- **Decision to record (not re-litigate):** the `Microsoft.TestPlatform.Portable` payload
  is NOT shipped — D2 already requires the .NET 10 SDK, and the SDK carries
  vstest.console; the fallback remains a documented code hook. State this in the README's
  requirements section.

## Milestone 4 — The rename (owner input 1)

Mechanical but complete: package id, `ToolCommandName`, repo/solution/project names or
just user-facing strings (owner's call — propose the cheap option: keep `ttr.*` project
names internally, rename all user-facing surfaces), the state folder (`.ttr/` → `.<name>/`),
env vars (`TTR_*` → `<NAME>_*`, keep old names as read-only aliases for one release),
README/help text. No data migration needed (no installed users). Grep-verify: no stale
user-facing occurrences of the old name outside `docs/` history.

## Milestone 5 — Docs & demo

- README: what it is (one paragraph + screenshot), requirements (.NET 10 SDK), install,
  quick start, the full keymap table (§11.3), flags, watch modes, `--continue`, state
  folder + gitignore note, supported frameworks table **including the honest rows** (NUnit4
  MTP upstream regression → warning node + link to the filed issue; theory-row restore
  note), troubleshooting (`--log`, `TTR_ASCII`, VT/conhost message).
- `asciinema` cast of the core loop (open → run → f → fix → vanish → watch) linked from
  the README (the `asciinema` tool is in the sandbox tier list, Appendix C).
- Final pass over the `?` help overlay for parity with the README keymap.

## Milestone 6 — Cleanup & hardening sweep

- Fix or environment-gate the `/tmp`-only Verify path-scrubber flake (`Backend_build_
  failure_detail`) — the suite must be green in BOTH sandbox and CI, even if that means a
  scrubber fix rather than a skip.
- TODO/HACK sweep with each item resolved or moved to a `docs/phase-8-backlog.md` entry;
  internal hooks (`TTR_CRASH_SAVE_DIR`) documented in a short CONTRIBUTING/dev-notes doc.
- Dependency audit: `dotnet list package --vulnerable --include-transitive` clean (the
  Build packages advisory is already neutralised by `ExcludeAssets` — restate in dev notes).

## Milestone 7 — Windows/macOS closure (owner input 2)

- Ensure Win/macOS CI lanes run the full suite + the install test (macOS) and at least
  build+unit (Windows if the fixture matrix restore is problematic there — state exactly
  what runs where).
- Write the owner's manual smoke script for Windows Terminal AND legacy conhost:
  install from the local feed, run `--fake files`, exercise resize/modal/unicode, confirm
  the conhost no-VT path shows the M2 message rather than garbage. The PR is not "done"
  until the owner's result is pasted into the notes (PASS or issues filed).

## Acceptance criteria (verdict table in PR description)

- AC1: Clean-machine scripted install test passes: install → discover → run → correct
  exit codes, from the installed tool, SDK-resolved vstest confirmed from that context.
- AC2: `--tfm` and unified `--log` work per M1; help/exit codes match the plan.
- AC3: ASCII fallback renders every main screen legibly (snapshots); `TTR_ASCII=1` and a
  non-UTF-8 locale both trigger it; no-VT Windows path produces the refusal message.
- AC4: Rename complete per M4 grep-verify (or M4 checklist delivered if the name is
  still pending — flagged, not silent).
- AC5: A newcomer can follow the README cold: the reviewer executes the README's own
  quick-start verbatim in a fresh environment and it works (this is the doc test).
- AC6: Full suite green in sandbox AND CI (the path-scrubber flake resolved); vulnerable-
  package audit clean; 203+ tests.
- AC7: Owner's Windows Terminal + conhost smoke result recorded (input 2).
- AC8: Tagged release candidate produced by CI with the `.nupkg` artifact; installing
  THAT artifact is what AC1 tests.

## Review script (paste into PR)

```bash
# the product test IS the install test:
docker run --rm -it mcr.microsoft.com/dotnet/sdk:10.0 bash   # or a clean user
dotnet tool install -g <name> --add-source /path/to/local-feed
<name> /path/to/fixtures/matrix/Matrix.slnx        # R · f · o · q — from the installed tool
TTR_ASCII=1 <name> --fake files                    # AC3 fallback
<name> --fake big --tfm net10.0 --log /tmp/t.log   # AC2; inspect the log categories
# then: follow README.md quick-start literally, as AC5
```

## Deliverables

One PR: code + packaging + docs + `docs/phase-7-notes.md` (AC verdicts incl. both owner
inputs; the final supported-frameworks statement; anything deferred → `docs/phase-8-backlog.md`).
On merge + owner sign-off: tag v1.0.0. The project's delivery phases are then complete.
