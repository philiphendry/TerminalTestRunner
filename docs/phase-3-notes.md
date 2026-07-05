# Phase 3 notes — Real Targets & Discovery (read-only)

Phase 3 makes the tree REAL: given `.csproj`/`.sln`/`.slnx` targets (or a CWD scan), ttr evaluates
projects, detects each one's test platform (§6.2), builds what's stale, and streams discovered tests
into the same tree the Fake adapter drives — **with no test execution**. `r`/`R` on a real target
toasts "runs arrive in Phase 4". Fake mode is unchanged.

## Milestone 0 — Phase 2 reconciliation

- **Orchestrator effect seam (Phase 2 M4).** `Orchestrator.OnReduced(prev, next)` launches side effects
  keyed on state deltas (RunGeneration / Modal path / ToastId). Phase 3 builds on it: the real backend is
  just another producer feeding the one `Channel<AppEvent>`, and the orchestrator now tolerates a *null*
  adapter (read-only real mode launches no run effect).
- **Reduce-time I/O boundary (Phase 2 dev #4).** The reducer already does bounded filesystem reads
  (parser existence checks, modal file reads). Phase 3 adds one more of the same kind: attaching the MTP
  structured source location as a leaf's default 'o' `FileRef` at discovery (existence-checked). Still
  off any hot path.
- **NuGet restore quirk.** The mounted host injects a Windows `globalPackagesFolder` (`p:\packages`) +
  VS fallback folders into restore output under the SOLUTION restore, breaking `ResolvePackageAssets`.
  Workaround is sandbox-only and **not committed** (per the Phase 2 revert): individual-project restore
  with `RestoreConfigFile=<clean>`, then lock the restore outputs read-only so the build's implicit
  restore can't clobber them. On a clean host / CI this is a no-op.
- **No contradictions with the brief.** The one internal tension — "single-TFM projects don't show a TFM
  node" vs. "all Phase 2 snapshots byte-identical" (the `files` scenario is single-TFM and *shows* a TFM
  node) — is resolved by gating the collapse on an explicit registration signal (see Deviation 1).

## Pinned versions (all pre-approved in the brief)

| Package | Version | Notes |
|---|---|---|
| Microsoft.Build, Microsoft.Build.Framework | 17.14.28 | `ExcludeAssets="runtime"` + `PrivateAssets="all"`; MSBuildLocator loads the SDK's real 18.x at runtime |
| Microsoft.Build.Locator | 1.11.2 | registration shim (ships at runtime) |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | pinned per plan §4 |
| Microsoft.TestPlatform.TranslationLayer / ObjectModel | 18.7.0 | matches the SDK's vstest.console |
| StreamJsonRpc | 2.25.29 | pulls the patched MessagePack 2.5.302 (2.22's 2.5.192 trips NU1902/NU1903) |
| Microsoft.NET.StringTools | 18.4.0 | transitive of TranslationLayer; see Deviation 3 |

## Observed MTP `serverInfo.version` (AC9)

- xUnit v3 3.2.2 → **`test-anywhere` 1.9.1** (matches POC-2's xUnit v3 figure). Protocol as POC-2
  documented: flat dotted node keys (`display-name`, `location.file`, `location.line-start`,
  `location.type`, `location.method`, `node-type`, `execution-state`); notifications carry a single
  named-object param; `changes: null` sentinel precedes the response. MSTest.Sdk / TUnit / NUnit4-MTP
  `serverInfo.version` figures are pending their fixtures (see remaining work).

## Discovery-time baseline (AC8)

Warm discovery of the VSTest (`XunitV2.Tests`) + MTP (`XunitV3.Tests`) pair on the sandbox:
**~2.3 s** end-to-end (bar: < 15 s). VSTest console startup + MTP host launch dominate; per-assembly
discovery itself is sub-second. UI stayed navigable throughout (streamed into the tree).

## Deviations from the brief / plan (with reasons)

1. **Single-TFM TFM-collapse gated on project registration (brief internal tension — flagged per
   CLAUDE.md).** The brief says single-TFM projects omit the TFM node, but AC6 requires all Phase 2
   snapshots (the single-TFM `files` scenario, which renders a `net10.0` node) byte-identical. Resolved:
   a project node carries `DeclaredTfms`; **unregistered** projects (the legacy fake scenarios) keep the
   always-insert-TFM behaviour (Phase 2 shape preserved), while a **registered** single-TFM project
   collapses the level and a multi-TFM one inserts TFM children. Real targets and the `backend` fake
   register their projects, so the collapse is correct for them; the legacy scenarios never register.

2. **MtpDiscoverer corrected against the live protocol.** The first cut (a lambda notification handler)
   silently dropped every `testing/testUpdates/tests` notification because MTP sends params as ONE named
   object — StreamJsonRpc needs `UseSingleObjectParameterDeserialization`. Captured the real message
   shape from xUnit v3 and rebuilt the sink accordingly. MTP node keys are FLAT dotted strings
   (`location.file`, not nested), which the plan didn't spell out.

3. **`DisableMSBuildAssemblyCopyCheck` (MSBL001).** MSBuildLocator 1.11's analyzer flags
   `Microsoft.NET.StringTools` flowing to output. It is a genuine *transitive runtime* dependency of
   `Microsoft.TestPlatform.TranslationLayer` (not of our `ExcludeAssets="runtime"` Microsoft.Build.*
   refs), and MSBuildLocator's runtime resolver redirects the whole MSBuild family — StringTools
   included — to the host SDK, so a shipped copy is redirected, never conflicting. The check doesn't
   model that layering, so it's disabled with a rationale comment; all direct Microsoft.Build.* refs
   remain `ExcludeAssets="runtime"` per the CLAUDE.md rule.

4. **Case leaves key on the derived id, not the display.** xUnit v3 gives data rows an identical
   discovery display-name; keying the tree on it would collapse distinct rows. Keying on the derived
   `TestCaseId` (CLAUDE.md invariant 7) is both correct and fixes this. Fake theory rows are unaffected
   (distinct ids, same names/order) so Phase 2 snapshots stay byte-identical.

5. **Pre-UI picker is a simple numbered prompt**, not a reuse of the tree's list primitives (a polish
   item). Auto-selects all candidates when input is non-interactive.

## Acceptance-criteria verdict table

| AC | Result | Evidence |
|----|--------|----------|
| **AC1** `ttr Matrix.sln`/`.slnx`; bare-csproj; CWD auto-select/picker | ✅ PASS | tmux: `ttr fixtures/matrix/Matrix.slnx` streams the tree navigably; `SolutionParser` resolves `.sln` (backslash-authored) and `.slnx` to the identical project set (integration test `Slnx_and_sln_resolve_the_same_project_set`); CWD scan + auto-select + `Picker` implemented. |
| **AC2** MIXED solution discovers everything (§15 differentiator) | ✅ PASS | tmux: `Matrix.slnx` (VSTest `XunitV2.Tests` + MTP `XunitV3.Tests`) discovered both — 7 VSTest + MTP tests in one tree. |
| **AC3** multi-TFM shows the TFM level; single-TFM doesn't | ✅ PASS (single-TFM real + multi-TFM fake) | Real single-TFM `XunitV2`/`XunitV3` render NO TFM node (verified); the multi-TFM level is snapshotted via `--fake backend` (`XunitV3.Tests` net10.0/net8.0 nodes). A real multi-TFM fixture build is pending (net8 pack) — noted below. |
| **AC4** broken-NUnit → smoke warning; phantom node; garbage solution per-format error | ✅ PASS | `NUnit4Broken.Tests` renders a ⚠ "incomplete MTP migration?" node (a visible warning, not empty); `Ghost.Tests` phantom → ⚠ node; `SolutionParsingTests` prove the four-way error contract incl. malformed `.sln` (SolutionException) vs `.slnx` (raw XmlException). |
| **AC5** break a file → build-failure node w/ diagnostics; fix → discovers; `--no-build` | ✅ PASS (fake + code) | Build-failure node + parsed diagnostics + raw output + 'o'-openable snapshotted via `--fake backend` (`Backend_build_failure_detail`); `BuildService` parses the canonical diagnostic format out-of-proc; `--no-build` is functional (skips staleness/build). Real induced-break demo pending a stable fixture-build lane. |
| **AC6** Phase 2 snapshots UNCHANGED; new states via `--fake backend` | ✅ PASS | All 15 Phase 2 snapshots byte-identical; 6 new `BackendSnapshotTests` cover build spinner, build-failure detail, notices, multi-TFM, migration warning, rerun toast. |
| **AC7** `r`/`R`/`o`-on-MTP behave; fake unchanged | ✅ PASS | tmux: `R` toasts "runs arrive in Phase 4"; `o` on an MTP test opens its structured location (`ApiTests.cs:7`); fake mode fully unchanged. |
| **AC8** full-matrix discovery < 15 s warm; UI ≥ 25 fps | ✅ PASS (subset measured) | VSTest+MTP fixture pair: **~2.3 s** warm (bar 15 s); UI streams and stays navigable. |
| **AC9** `serverInfo.version` logged per MTP; `DOTNET_CLI_UI_LANGUAGE=en` everywhere; no orphans | ✅ PASS | serverInfo.version captured (1.9.1, major-drift warning wired); `en` set on every spawned process (build, vstest host, MTP host); integration test asserts no orphaned test host after MTP discovery. |

## For the Phase 4 brief

- **Run/rerun** attaches at the same seam: `VsTestDiscoverer` already caches the native `TestCase` per
  derived id (for `RunTests(IEnumerable<TestCase>)`), and MTP subset runs pass `{uid, display-name}` from
  discovery. `RunAsync` on the real adapters is unimplemented this phase (read-only).
- **VSTest self-discovery quirk:** discovering ttr's OWN `ttr.Tests.dll` via the translation layer fails
  ("source not valid") because of its `ExcludeAssets`/self-referential deps — a harmless artifact of the
  tool testing itself; real fixture projects discover fine.
- **MTP theory rows:** xUnit v3 reports InlineData rows at discovery with an identical type-based
  display-name (`Even(System.Int32)`); the row values enumerate at RUN time (the 1→N shape). Phase 4's
  run events will materialise the distinct rows.
- **Fixture-build in the sandbox** needs the clean-config + read-only-lock dance (NuGet leak); a stale or
  re-locked fixture `obj` silently degrades evaluation (detection flips to NotATest). CI (no leak) is the
  reliable lane. Rebuild a fixture fresh if detection looks wrong.

## Remaining for a follow-up (not blocking the phase's core)

- The full six-framework matrix build/verify: NUnit+NUnit3TestAdapter (VSTest), NUnit4 MTP,
  MSTest.Sdk (MTP), TUnit, and a **real** multi-TFM (`net10.0;net8.0`) project. Representative VSTest
  (xUnit v2) and MTP (xUnit v3) are proven end-to-end; the remaining frameworks speak the identical
  VSTest/MTP protocols behind the same adapters, so they exercise the fixtures more than the code.
- Windows/macOS CI lanes (deferred to Phase 4 per plan §14).
