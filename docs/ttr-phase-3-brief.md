# ttr — Phase 3 Brief: Real Targets & Discovery (Read-Only)

**One phase = one PR.** This brief is the task; `CLAUDE.md` is standing law; the plan is at
`docs/ttr-implementation-plan.md` (§-references point there). Where this brief and CLAUDE.md
conflict, stop and flag it.

## Context and goal

Phases 1–2 delivered the complete, owner-approved UX against the Fake adapter, frozen behind
the Verify snapshot suite (15 snapshots, 85 tests). Phase 3 makes the tree REAL: given
`.csproj`/`.sln`/`.slnx` targets (or the CWD scan), ttr evaluates projects, detects each one's
test platform, builds what's stale, and **streams discovered tests into the same tree — with
no test execution**. Pressing `r`/`R` on a real target politely toasts "runs arrive in
Phase 4". Fake mode continues to work unchanged.

**The UX contract governs:** existing snapshots must not change except where this brief
explicitly adds NEW states (build spinner on project nodes, error/warning nodes, TFM level).
Every new visual state is added to the Fake adapter FIRST and snapshotted there, then the
real backend feeds the same events. The backend must be invisible behind `ITestSessionAdapter`
and the event stream.

## Explicit non-goals

- No test execution: no `RunAsync` implementation in the real adapters (interface method may
  throw NotSupported-with-toast this phase). No abort/cancel of runs. (Phase 4.)
- No `--watch`, no `--continue`, no persistence. (Phases 5–6.)
- No re-discovery diffing loop (that's watch); discovery happens once per launch (plus per
  explicit project error retry if trivially cheap — otherwise skip).
- New dependencies pre-approved: `Microsoft.VisualStudio.SolutionPersistence` **1.0.52**,
  `Microsoft.Build` + `Microsoft.Build.Framework` + `Microsoft.Build.Locator` (ALL with
  `ExcludeAssets="runtime"` on the Build packages — CLAUDE.md rule), `StreamJsonRpc`,
  `Microsoft.TestPlatform.TranslationLayer` + `Microsoft.TestPlatform.ObjectModel`.
  Pin exact versions; record them in the notes. Anything else: default no.

## Milestone 0 — Reconcile Phase 2 (~30 min)

Read `docs/phase-2-notes.md` + the Phase 2 PR. Note in your notes: the Orchestrator effect
seam introduced in M4 (build on it — discovery/build are effects launched from state, same
pattern), the reduce-time-I/O boundary decision, and the NuGet restore pinning quirk
(`NuGet.config` / `sbx/build-test.sh`) so fixture restores behave in the sandbox. Flag any
contradiction with this brief before building.

## Milestone 1 — New UI states, fake-first (protects the UX contract)

Extend the domain + Fake adapter + snapshots BEFORE any real backend exists:
- Project-node **build phase**: `BuildStarted/Succeeded/Failed` events → build spinner on the
  project node; on failure, a red project node whose detail pane shows parsed diagnostics
  (file(line,col), code, message) + raw build output below them.
- **Warning/error nodes** (plan §4/§6.2): phantom project ("file not found"), unparseable
  solution entry, "unknown runner" project, dead-MTP-opt-in warning, smoke-validation failure
  ("MTP host exited without handshake" / "test project discovered zero tests").
- **TFM level**: a multi-targeting project inserts child TFM nodes (plan D9/§8); single-TFM
  projects do not.
- New fake scenario `backend` exercising all of the above; new snapshots for each state.
  Existing snapshots must remain byte-identical.

## Milestone 2 — ttr.Build: evaluation + detection

Per plan §6.2/§7 and CLAUDE.md MSBuild rules (registration/method-boundary, per-TFM
evaluation, `ExcludeAssets`):
- Evaluation service: per-(project,TFM) → output path, `Compile` globs (stored for Phase 5),
  `IsTestProject`, detection signals; cached per project.
- Detection = the §6.2 V2 rule verbatim: `IsTestingPlatformApplication` authoritative;
  dual-mode → MTP + informational note; dead opt-in flags → VSTest + "incomplete MTP
  migration?" warning; else Unknown/NotATest. Per-project override honoured from
  `.ttr/config.json` if present (read-only config; no writer this phase).
- Unit tests over minimal fixture csprojs for every rule branch.

## Milestone 3 — Fixture matrix (plan §12)

Create `fixtures/matrix/` with pinned versions (POC-verified: `xunit.runner.visualstudio`
≥ 3.0.0; NUnit 4 MTP via the docs.nunit.org `EnableNUnitRunner` path): xUnit v2 (VSTest),
xUnit v3 (MTP), NUnit + NUnit3TestAdapter (VSTest), NUnit 4 MTP, MSTest.Sdk (MTP), TUnit —
each with passing/failing/skipped/theory tests; one multi-TFM project (net10.0;net8.0 —
note: only net10.0 need be *runnable* in the sandbox); a `.sln` AND a `.slnx` of the same
matrix plus bare-csproj usage; one solution-entry phantom; **one deliberately broken
NUnit4 + `EnableMicrosoftTestingPlatformRunner` + NUnit3TestAdapter project** (the POC-9
silent no-op) as the permanent smoke-validation regression fixture.

## Milestone 4 — Target selection + solution parsing

Per plan §3/§4: positional targets; CWD top-level scan; exactly-one auto-select; several →
the pre-UI multi-select picker (reuse list primitives); none → exit 2. SolutionPersistence
1.0.52 with the four-way error contract (`SolutionException` / raw `XmlException` /
`FileNotFoundException` / null serializer), `TypeId`-based classification, `File.Exists`
phantom pass → warning nodes. Zero test projects in an explicit target → exit 2 with message.

## Milestone 5 — Build service

Per plan §7: staleness check (inputs vs output assembly); out-of-proc
`dotnet build --nologo -v:quiet -tl:off -consoleLoggerParameters:ErrorsOnly;NoSummary`;
**parallel `Task.WhenAll` across test projects**; canonical-diagnostic regex + raw-output
attachment; build events drive the M1 states. `--no-build` becomes functional. Discovery is
gated on build success per project (failed projects show the error node; others proceed).

## Milestone 6 — VSTest discovery adapter

Per plan §6.3: host located via SDK directory (Strategy A; implement the
`Microsoft.TestPlatform.Portable` fallback only as a located-path hook, not a shipped
payload yet); ONE persistent `VsTestConsoleWrapper`; `DiscoverTests` streaming batches →
`TestsDiscovered` events; derived `TestCaseId` (param display hash — theory FQNs collide);
cache native `TestCase` per id for Phase 4. Set `DOTNET_CLI_UI_LANGUAGE=en` on everything
ttr spawns (hosts included) — the CLAUDE.md locale rule starts now.

## Milestone 7 — MTP discovery adapter

Per plan §6.4 — every binding rule applies from the first line: listener-first (port 0),
`--server --client-host localhost --client-port <port>`, `HeaderDelimitedMessageHandler`
framing, named params via `InvokeWithParameterObjectAsync`, `initialize` (capture + log
`serverInfo.version`, warn on major drift from 1.9.1–2.2.3), `testing/discoverTests` with the
`tests` field OMITTED, sentinel (`changes: null`) as primary completion, one request in
flight, `exit` + 5 s timeout + `Process.Kill(entireProcessTree: true)`, tolerate unknown
notifications. Keep the host process alive (Phase 4 reuses it). **Smoke validation** (plan
§6.2): host exits without handshake, or zero tests from an `IsTestProject` project → the M1
warning node, never a silent empty subtree. Capture structured `location.file`/`line-start`
onto test nodes (default 'o' target — wire it into the existing modal path).

## Milestone 8 — Integration tests + CI

Per-fixture integration tests asserting discovered-test counts and tree shape (project →
(TFM) → namespace → class → method → cases) for all six frameworks, both solution formats,
the phantom, and the broken-NUnit smoke fixture. CI runs them on Linux (Win/macOS lanes
remain Phase 4).

## Acceptance criteria (verdict table in PR description)

- AC1: `ttr fixtures/matrix/Matrix.sln` — tree streams in while navigable; identical results
  from `Matrix.slnx`; bare-csproj target works; CWD auto-select and picker work.
- AC2: The MIXED solution (VSTest + MTP projects together) discovers everything — the plan's
  differentiator (§15) demonstrated.
- AC3: Multi-TFM project shows the TFM level; single-TFM projects don't.
- AC4: Broken-NUnit fixture yields the smoke-validation warning node (not an empty subtree);
  phantom project yields its warning node; a solution with garbage bytes produces the correct
  per-format error message (test both formats).
- AC5: Break a fixture source file → build-failure node with parsed diagnostics in the detail
  pane; fix it → relaunch discovers normally. `--no-build` against stale sources discovers
  from the old assembly.
- AC6: All Phase 2 snapshots pass UNCHANGED; new M1 states snapshotted via `--fake backend`.
- AC7: `r`/`R`/`o`-on-MTP-location behave per this brief (runs toast "Phase 4"; 'o' opens the
  MTP structured location); fake mode fully unchanged.
- AC8: Discovery of the full matrix completes < 15 s warm on the sandbox (report the number;
  this is a baseline, not a hard bar); UI stays ≥ 25 fps while streaming.
- AC9: `serverInfo.version` logged per MTP project; every spawned process carries
  `DOTNET_CLI_UI_LANGUAGE=en`; no orphaned processes after quit (`ps` check in an
  integration test).

## Review script (paste into PR)

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.sln     # watch it stream; navigate; s on a failing project; o on an MTP test
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx    # same tree
dotnet run --project src/ttr.Cli -- --fake backend                 # the new states, fake-first
echo "break a file:"; sed -i 's/return/retrun/' fixtures/matrix/XunitV2.Tests/Calc.cs
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.sln     # build-error node + diagnostics
git checkout -- fixtures/                                          # restore
```

## Deliverables

One PR: code + fixtures + tests + `docs/phase-3-notes.md` (pinned versions incl. per-framework
MTP `serverInfo.version` observed, the discovery-time baseline from AC8, deviations with
reasons, and anything that should feed the Phase 4 brief — especially any adapter behaviour
that differed from the plan's POC-derived expectations).
