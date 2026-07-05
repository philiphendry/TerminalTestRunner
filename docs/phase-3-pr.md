# Phase 3 — Real Targets & Discovery (read-only)

One phase = one PR. Makes the tree REAL: target args + CWD scan + picker; `.sln`/`.slnx` parsing with
the §4 error/phantom contract; MSBuild evaluation + §6.2 detection + runner smoke validation; an
out-of-proc parallel build service; and streamed discovery via BOTH real adapters (VSTest §6.3, MTP
§6.4) populating the same tree the Fake adapter drives. **No test execution** — `r`/`R` toasts
"runs arrive in Phase 4". Fake mode is unchanged. Full detail + AC verdict table + deviations:
`docs/phase-3-notes.md`.

## What's here

- **M1 (fake-first UI states):** `ProjectRegistered` / `NoticeRaised` / `BuildStarted|Succeeded|Failed`
  / `DiscoveryFailed` events; build spinner + red build-failure node (parsed diagnostics + raw output,
  'o'-openable); the warning/error notice family; the TFM level (multi-TFM inserts nodes, single-TFM
  collapses); `--fake backend` scenario + 6 new Verify snapshots. All 15 Phase 2 snapshots unchanged.
- **M2 (evaluate + detect):** `EvaluationService` (in-proc MSBuild, per-TFM, no target execution);
  `Detection` — the §6.2 V2 rule as a pure function (10 unit tests); `.ttr/config.json` override.
- **M4 (targets + solutions):** `SolutionParser` (SolutionPersistence 1.0.52, four-way error contract,
  phantom pass, backslash-tolerant); `TargetResolver` (scan/auto-select/exit-2); `Picker`.
- **M5 (build):** `BuildService` — out-of-proc `dotnet build`, staleness check, parallel over test
  projects, canonical-diagnostic regex + raw-output fallback; `--no-build` functional.
- **M6 (VSTest):** `VsTestDiscoverer` — SDK-located console, persistent wrapper, streaming discovery,
  derived `TestCaseId` (theory param hash), native `TestCase` cache for Phase 4.
- **M7 (MTP):** `MtpDiscoverer` — listener-first, StreamJsonRpc LSP framing, `initialize` +
  serverInfo.version, `discoverTests` (`tests` omitted), sentinel completion, exit+5s+kill-tree,
  structured location → 'o', smoke validation.
- **M8:** real integration tests (VSTest tree/counts, MTP JSON-RPC + location + orphan check, sln≡slnx);
  CI builds the fixture matrix on Linux. **125 tests green.**

## Review script

```bash
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.slnx   # mixed VSTest+MTP; navigate; expand Add_Theory; o on an MTP test; R toasts Phase 4
dotnet run --project src/ttr.Cli -- fixtures/matrix/Matrix.sln    # identical project set
dotnet run --project src/ttr.Cli -- --fake backend                # the new states, fake-first
```
(In the sandbox, prebuild the fixtures and run ttr via `dotnet <path>/ttr.dll` with the clean NuGet
config — see docs/phase-3-notes.md §M0.)

## New dependencies (all pre-approved in the brief)

Microsoft.Build 17.14.28 · Microsoft.Build.Framework 17.14.28 · Microsoft.Build.Locator 1.11.2 ·
Microsoft.VisualStudio.SolutionPersistence 1.0.52 · Microsoft.TestPlatform.TranslationLayer 18.7.0 ·
Microsoft.TestPlatform.ObjectModel 18.7.0 · StreamJsonRpc 2.25.29 (patched MessagePack) ·
Microsoft.NET.StringTools 18.4.0 (transitive). Build packages carry `ExcludeAssets="runtime"`.

## Deviations (see notes for full reasons)

TFM-collapse gated on registration (protects AC6); MtpDiscoverer corrected against the live protocol
(single-object params, flat dotted keys); `DisableMSBuildAssemblyCopyCheck` for the transitive
StringTools MSBL001 false-positive; Case leaves key on the derived id; simple pre-UI picker. The full
six-framework matrix + real multi-TFM build + Win/macOS lanes are scoped as follow-ups — representative
VSTest (xUnit v2) and MTP (xUnit v3) are proven end-to-end and the rest reuse the same two adapters.
