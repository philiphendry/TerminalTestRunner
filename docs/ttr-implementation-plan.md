# `ttr` — Terminal Test Runner & Visualiser: Implementation Plan

**Working name:** `ttr` (rename is a find/replace on the package id and the `.ttr/` state folder — do it before Phase 7 (packaging) ships).
**Distribution:** .NET global tool (`dotnet tool install -g ttr`), .NET 10 SDK assumed on the host.
**Status of this document: Phases 0–4 COMPLETE — ttr is a working test runner (real runs, cancellation, exit codes, dogfooding CI). Phase 5 (watch) is next; it closes POC-6's parallel-build conditional and carries two Phase 4 items: the NUnit4-MTP handshake investigation and the sandbox rebuild flakiness. PHASE 0 detail — all nine POCs done, all decision gates closed; the plan is build-ready.** UI framework decided: direct-ANSI renderer (Spectre as measurement library), RazorConsole not adopted (gate outcome + recorded dissent in §13). One conditional is carried into Phase 5 (watch): measuring the real parallel-build watch cycle < 4 s (POC-6 AC2). Delivery phases restructured in §14 into eight small reviewable increments — the complete UI ships against the fake adapter in Phases 1–2, before any real test infrastructure. **POC results incorporated: POC-1 (PASS — `AbortTestRun` cancellation rule), POC-2 (ALL PASS — MTP server-mode contract in §6.4, console fallback dropped), POC-3 (PASS — detection rule corrected to `IsTestingPlatformApplication` per-TFM), POC-4 (ALL 7 PASS — direct-ANSI baseline), POC-5 (measurable bars PASS, AC8 gaps → gate applied against adoption), POC-6 (PASS w/ conditional — parallel builds mandatory, source-watch confirmed as default, quiescence/coalescing rules validated), POC-7 (ALL PASS — SolutionPersistence 1.0.52 adopted, error/phantom/TypeId contract in §4), POC-8 (ALL PASS — packed-JSONL persistence), POC-9 (ALL PASS — 100% parser recall, `/obj/`+`.g.cs` filtering and locale mitigation added to §11.5, runner smoke validation added to §6.2).**

---

## 1. Summary

`ttr` is a cross-platform terminal application that discovers, runs, and visualises .NET unit tests in real time. It is launched against one or more `.csproj`/`.sln`/`.slnx` files (or picks them up from the working directory), presents the tests as a navigable tree that always fits the terminal, streams results live (spinners → ticks/crosses) while remaining fully navigable, and supports failed-only filtering, a details pane, watch mode, session resume, and an in-app syntax-highlighted source viewer.

It deliberately does **not** shell out to `dotnet test` and scrape output. Test discovery and execution go through the two real platform APIs — the VSTest translation layer and the Microsoft.Testing.Platform (MTP) server protocol — selected automatically per project.

## 2. Locked decisions (from requirements interrogation)

| # | Decision | Value |
|---|---|---|
| D1 | Test platforms supported | **Both** VSTest-era (xUnit v2, older NUnit/MSTest) and MTP-native (xUnit v3, MSTest 3.2+, NUnit 4 MTP mode, TUnit), auto-detected per project |
| D2 | Host requirement | .NET 10 SDK; shipped as a dotnet global tool |
| D3 | UI framework | **DECIDED (gate applied, §13): direct-ANSI renderer with Spectre.Console as cell-measurement library** — RazorConsole not adopted (2 High-severity gaps vs core spec; alpha churn); its syntax-highlighting and scroll techniques ported. UI core remains framework-agnostic behind `IUiShell` |
| D4 | Watch trigger | Both modes, selectable: `--watch build` (ttr watches sources and builds) and `--watch external` (user builds; ttr watches output DLLs) |
| D5 | "Affected tests" granularity | Per-project: changed project + transitive dependents rerun. Per-test impact analysis is out of scope (future, §14 Phase 8) |
| D6 | Scale target | Smooth at **10,000 tests** (tree virtualisation mandatory) |
| D7 | Build responsibility | ttr auto-builds stale projects before discovery and before reruns; build errors attach to the project node in the results pane |
| D8 | `--continue` scope | Results + expansion state + filters + selection + pane layout; JSON under `.ttr/` next to the primary target; auto-saved after every run |
| D9 | Multi-TFM | Run all TFMs; a TFM tree level is inserted only for multi-targeting projects |
| D10 | 'o' key | In-app modal viewer only for v1; file paths parsed from stack traces and messages; external-editor handoff is a future item |
| D11 | Input | Keyboard only for v1 |
| D12 | Tree shape | Solution → Project → (TFM) → Namespace → Class → Method → parameterised cases as method children |

## 3. CLI specification

```
ttr [<targets>...] [options]

<targets>            Zero or more .csproj / .sln / .slnx paths.
                     None given → scan CWD (top level) for candidates:
                       exactly one  → auto-select
                       several      → interactive multi-select picker
                       none         → exit 2 with message

--fake [<scenario>]  Run against the fake adapter instead of real tests.
                     Scenarios: default | big (10k) | flaky | slow | files
                     (results embed real file paths so 'o' is testable).
--fake-seed <int>    Deterministic RNG seed for fake mode.
--continue           Restore state of the previous session for these targets
                     (results shown as Stale where assemblies are newer).
--watch [<mode>]     Watch mode. <mode> = build (default) | external. See §9.
--no-build           Never build; discover/run against existing binaries.
--tfm <moniker>      Restrict to one target framework.
--log <path>         Diagnostic log (adapter traffic, MSBuild output).
--state-dir <path>   Override the default .ttr/ location.
```

Exit codes: `0` clean exit, `1` exited with failing tests, `2` usage/target error, `3` build failure preventing any run, `130` Ctrl+C. (Codes matter because people will script it despite it being a TUI.)

Parsing via **System.CommandLine**. `--fake` combines with `--watch`/`--continue` (the fake adapter simulates both) — this is the primary way UI features get developed and tested.

## 4. Target selection & solution parsing

- `.sln` **and** `.slnx` are parsed with **`Microsoft.VisualStudio.SolutionPersistence`, pinned to 1.0.52** — the official library used by the SDK itself; one model for both formats. **POC-7 ✅ validated all five criteria**: identical project sets across `.sln` and its `dotnet sln migrate`d `.slnx` twin; solution folders (including item-only and nested folders) cleanly disjoint from buildable projects; path normalisation correct on Linux including backslash-authored and `../`-escaping paths (`Path.GetFullPath(Path.Combine(solutionDir, project.FilePath))` is all that's needed); round-trip preserves the set; cost negligible (a one-time ~50 ms JIT/type-init tax at first use, then **sub-millisecond** warm reloads — solution-model reloading is never a watch-mode performance concern at normal scale).
- **Error-handling contract (from POC-7 — the exceptions are not uniform):** malformed `.sln` throws the library's `SolutionException` (with `ErrorType`/`File`/`Line`), but malformed `.slnx` throws a **raw, unwrapped `System.Xml.XmlException`**; a missing file throws `FileNotFoundException`; an unrecognised extension yields a **`null` serializer** from `GetSerializerByMoniker`. ttr catches all four as distinct, user-facing error categories. Two subtleties: **"loaded" ≠ "intact"** — the `.sln` parser silently accepts truncated files as plausible partial models — and project types are classified by **`TypeId` GUID, never the `Type` string** (blank by design for all standard project kinds).
- **Phantom projects:** the library never touches the filesystem, so a solution referencing a non-existent project loads silently. ttr runs an explicit `File.Exists` pass over resolved paths and surfaces phantoms as a distinct, non-fatal warning node in the tree rather than omitting or failing.
- From the surviving project set, **test projects** are identified by MSBuild evaluation (§7): `IsTestProject == true`, or a package reference to any of `Microsoft.NET.Test.Sdk`, `xunit`/`xunit.v3`, `NUnit`, `MSTest.*`, `TUnit`. Non-test projects stay in the dependency graph (for watch) but never appear in the tree.
- The interactive picker (multiple candidates found in CWD) is a simple pre-UI multi-select list — reuses the same input loop and list-rendering primitives as the main tree so it costs little.
- A solution given explicitly with **zero** test projects → exit 2 with a clear message; in watch mode we instead stay alive and say so, since a test project may appear.
- *(Deliberately ignored for v1, noted for v2:)* per-project resolved `Platform` strings differ between formats for the same solution (`"Any CPU"` from `.sln`, `"AnyCPU"` from `.slnx`) — irrelevant while ttr doesn't do config-aware filtering, a trap if it ever does.

## 5. Architecture overview

Five assemblies; the dependency arrows only point downward:

```
ttr.Cli        entry point, System.CommandLine, composition root
ttr.Ui         rendering + input (Spectre or RazorConsole behind IUiShell)
ttr.Core       domain model, AppState, reducers, orchestration services
ttr.Runners    ITestSessionAdapter implementations: VSTest, Mtp, Fake
ttr.Build      MSBuildLocator, evaluation, ProjectGraph, build execution
```

The runtime shape is **message-driven MVU**:

1. Adapters, the build service, the watcher, and the input thread all publish typed events onto a single `Channel<AppEvent>` (e.g. `TestsDiscovered`, `TestStarted`, `TestFinished`, `BuildFailed`, `FileChanged`, `KeyPressed`, `Resized`).
2. A single consumer applies **reducers** producing a new `AppState` snapshot. All state mutation lives here — no locks anywhere else, and every state transition is unit-testable without a terminal.
3. A render loop (capped ~30 fps, dirty-flag gated) projects `AppState` through view-models into the UI framework. Rendering never blocks event processing.
4. Side effects (start a run, start a build, save state) are issued by an orchestrator reacting to state transitions, each on its own task with a `CancellationToken` tied to the session.

This is what makes "navigate while tests run" free: input events and result events are just interleaved messages.

### 5.1 Domain model

- **`TestCaseId`** — the stable identity used for tree diffing, rerun selection, and `--continue` matching. Composed of: adapter kind + project path + TFM + fully-qualified name + parameter display hash. POC-1 confirmed why the derived id must exist: **theory FQNs are not unique** (5 `InlineData` rows share one FQN), and VSTest's own `TestCase.Id` GUID is only guaranteed stable *within a session on the same binary* — so the derived id (with param display hash disambiguating rows) is the persistence/diffing key, while the platform-native id is kept alongside strictly for execution within the current session. (POC-2 adds nuance on the MTP side: UIDs there are *more* durable — e.g. xUnit v3 emits content-based SHA-256 UIDs stable across runs for the same method signature, though opaque to humans — but ttr keys on the derived id uniformly rather than special-casing per platform.)
- **`TestNode`** — tree node (Solution/Project/Tfm/Namespace/Class/Method/Case kinds), children, rollup counters (passed/failed/skipped/running/notRun), rollup duration.
- **`TestStatus`** — `NotRun | Queued | Running | Passed | Failed | Skipped | Stale`. `Stale` marks restored (`--continue`) or pre-rebuild results whose assembly has since changed.
- **`TestResultDetail`** — message, exception chain, stack trace, stdout/stderr, duration, attachments, and the **parsed ordered list of source file references** (path + optional line) that powers 'o'. Details are stored out-of-band (§10) and lazy-loaded; the tree holds only status + duration to keep 10k tests cheap.

## 6. Runner subsystem

### 6.1 `ITestSessionAdapter`

```csharp
interface ITestSessionAdapter : IAsyncDisposable
{
    // Streams DiscoveredTest events onto the channel as they arrive.
    Task DiscoverAsync(TestTarget target, ChannelWriter<AppEvent> events, CancellationToken ct);
    // Runs the given subset (empty = all); streams TestStarted/TestFinished.
    Task RunAsync(TestTarget target, IReadOnlyList<TestCaseId> subset,
                  ChannelWriter<AppEvent> events, CancellationToken ct);
}
```

One adapter instance per (project, TFM) pair; the orchestrator fans out and merges. Cancellation must map to a real platform cancel (VSTest **`AbortTestRun`** — see §6.3, POC-1 showed `CancelTestRun` takes ~18 s with xUnit v2; MTP **`$/cancelRequest`** — POC-2 measured ~2 s with the server process surviving for reuse), not just abandoning the task.

### 6.2 Per-project platform detection — **corrected & settled by POC-3 ✅**

POC-3 did exactly what it was for: the paper heuristic classified the fixture matrix "correctly" as specified, then empirical verification (actually connecting a TCP listener to each binary's `--server` handshake) proved one of the spec's own rules wrong. The production rule, in priority order:

1. **Evaluate per-TargetFramework** (pass each `TargetFrameworks` value as a global property) — classification is only answerable per-TFM, never at the outer cross-targeting level.
2. **`IsTestingPlatformApplication == true` → MTP.** This is the single authoritative signal: POC-3 unzipped the adapter packages and confirmed every framework-specific opt-in (`EnableMSTestRunner`, `EnableNUnitRunner`, MSTest.Sdk, `xunit.v3`/`TUnit` package targets) funnels into this one shared property, which is what `Microsoft.Testing.Platform.MSBuild` actually branches on. The underlying trigger is recorded for diagnostics only, never for the decision.
   - If a classic VSTest adapter is *also* present (genuine dual-mode, e.g. MSTest with `EnableMSTestRunner` + `TestingPlatformDotnetTestSupport` — both paths really work) → still MTP, with an **informational** "dual-mode; VSTest available" note, not an alarm.
3. Else `Microsoft.NET.Test.Sdk` + one of `xunit.runner.visualstudio`/`NUnit3TestAdapter`/`MSTest.TestAdapter` → **VSTest**.
   - If a raw opt-in property (`UseMicrosoftTestingPlatformRunner`, `EnableMSTestRunner`, `EnableNUnitRunner`, `TestingPlatformDotnetTestSupport`) is `true` here despite `IsTestingPlatformApplication` being false, that's a **dead MTP opt-in flag** — POC-3 proved (e.g. `UseMicrosoftTestingPlatformRunner` on classic xUnit v2) that no referenced package even reads it; the project runs pure VSTest end to end. Classify **VSTest + a visible "incomplete MTP migration?" warning**. (This reverses the plan's original "both signals → prefer MTP" rule, which would have misclassified real migration-in-progress projects onto a protocol their binaries don't speak.)
4. Else `IsTestProject == true` → **Unknown**, warn (test-shaped, no recognised runner).
5. Else → not a test project.

**`global.json` is not a detection input** — POC-3 showed its `test.runner` section changes zero evaluated properties. It *is*, however, a live grenade for anyone shelling out: on the .NET 10 SDK it hard-fails `dotnet test` in one direction or the other on any mixed VSTest/MTP solution (each state breaks one side, no state runs both). That's direct empirical validation of ttr's founding premise — drive the VSTest/MTP protocols natively, never via `dotnet test`.

A per-project override remains available in `.ttr/config.json` for pathological cases.

**Runner smoke validation (added after POC-9):** POC-9 hit a live specimen of a *plausible-looking dead configuration* — NUnit 4 + NUnit3TestAdapter 6.2.0 with `EnableMicrosoftTestingPlatformRunner=true` builds and exits 0 while running **zero tests** (the adapter generates no MTP entry point; the project is silently a no-op console app). Static classification, however correct, can't catch a runner that lies by exiting cleanly. So the first discovery per project doubles as a sanity check: an MTP-classified host that exits without ever completing the server handshake, or any project whose discovery returns zero tests while `IsTestProject` is true, is surfaced as an explicit per-project error/warning node — never rendered as a quietly empty subtree the user might mistake for "no tests here".

### 6.3 VSTest adapter (`Microsoft.TestPlatform.TranslationLayer`)

- Uses `VsTestConsoleWrapper` in design mode with a persistent session per ttr run (session startup is the expensive part; keep it warm).
- **Locating the host (settled by POC-1 ✅):** as a global tool we cannot assume a relative path to `vstest.console.dll`. Both strategies proved workable; **Strategy A (SDK-resolved) is preferred**: resolve the active SDK directory (`dotnet --info` / `DOTNET_ROOT` + `sdk/<ver>/`) and use its `vstest.console.dll`. Fallback: ship the `Microsoft.TestPlatform.Portable` payload inside the tool package — it works, with the caveat that the nupkg ships a **net8.0** host (`tools/net8.0/vstest.console.dll`); it tests net10.0 code fine, but the host runtime mismatch is one more thing to age, reinforcing SDK-resolved as primary.
- **Discovery:** `DiscoverTests(sources, runSettings, handler)` — `ITestDiscoveryEventsHandler2` delivers `TestCase` batches as found → streamed straight into the tree. No tests execute.
- **Run:** `RunTests(IEnumerable<TestCase>, runSettings, handler)` — running a *subset* means passing the concrete `TestCase` objects, so the adapter caches the last-discovered `TestCase` per `TestCaseId`. `ITestRunEventsHandler2.HandleTestRunStatsChange` streams completed `TestResult`s **and** the currently-`ActiveTests` — the latter drives per-test spinners. POC-1 measured streaming latency at **p95 = 27 ms** (bar was 250 ms) with `ActiveTests` populated on every callback; cold discovery ~0.7–0.8 s for a 217-case assembly, streamed in 22 batches with zero test code executing.
- **Cancellation (POC-1 finding, binding):** use **`AbortTestRun()`** (~25 ms), **not** `CancelTestRun()` — with xUnit v2 the latter only honours cancellation at assembly boundaries and took 18.4 s. Abort is hard-kill semantics: in-flight test results are lost, so the reducer must transition any still-`Running` tests to `NotRun`/`Stale` (not leave phantom spinners) when a run ends via abort.
- **RunSettings:** generated per run — TFM/platform, `DesignMode=true`, parallelism left to framework defaults in v1.

### 6.4 MTP adapter (server mode, JSON-RPC) — **validated by POC-2 ✅ (all three frameworks PASS)**

MTP test projects are self-contained executables. POC-2 proved xUnit v3 (3.2.2 / MTP 1.9.1), MSTest.Sdk (4.2.3 / MTP 2.2.3), and TUnit (1.58.0 / MTP 2.2.3) all speak an **identical** protocol — same method names, framing, and capability set despite the MTP version gap. The adapter contract, now settled:

1. Ensures the project is built, resolves the test host (`bin/.../<name>.exe` or `dotnet exec <name>.dll`).
2. **Client listens first** on an ephemeral port (`TcpListener` port 0), then launches the host with `--server --client-host localhost --client-port <port>` — the host connects *outward* to ttr.
3. Speaks JSON-RPC via **StreamJsonRpc** with LSP `Content-Length` framing (`HeaderDelimitedMessageHandler`; the server adds a `Content-Type: application/testingplatform` header, which the handler tolerates). Sequence: `initialize` (capture `serverInfo.version`) → `testing/discoverTests` → `testing/runTests`; the host streams `testing/testUpdates/tests` notifications (`execution-state`: `discovered|in-progress|passed|failed|skipped|error|canceled`) mapping onto `TestStarted`/`TestFinished` events. Test nodes carry **`location.file` / `location.line-start`** — structured source locations for free (see §11.5). Subset runs pass `{uid, display-name}` pairs from discovery.
4. Keeps the host alive across reruns of the same build (POC-2: warm discovery 5–57 ms, warm run ~43–49 ms — this is what makes watch mode snappy); restarts it after a rebuild. Cancellation via `$/cancelRequest` completes in ~2 s with the server surviving.

**Binding protocol rules from POC-2 (violating these crashes or wedges the server):**
- **Never send `"tests": null`** — omit the field entirely for run/discover-all; include it only as a non-null array for subsets (MTP's bespoke deserializer crashes on null).
- Params are **named objects**, not positional — use `InvokeWithParameterObjectAsync`.
- **One request in flight at a time** (`experimental_multiRequestSupport: false`); serialise requests per session, and wait for cancel confirmation before the next request.
- Completion is signalled by a **sentinel notification** (`testing/testUpdates/tests` with `changes: null`) that arrives *before* the JSON-RPC response — treat the sentinel as primary completion, the response as secondary.
- `exit` doesn't guarantee process exit — guard with a 5 s timeout then `Process.Kill(entireProcessTree: true)`.
- Tolerate and log unknown notifications (`telemetry/update`, `client/log`).
- **Result batching is architectural:** MTP flushes update notifications on a hardcoded 200 ms idle timer, so fast suites deliver *all* in-progress + terminal states in one burst; only tests running > 200 ms yield genuinely incremental updates. Spinners are driven by `in-progress` notifications; the UI's coalescing render loop (§5, §11.2) absorbs bursts by design.
- Read `serverInfo.version` at handshake and warn on major-version jumps (tested range: MTP 1.9.1–2.2.3, identical capabilities).

**Console-mode fallback: dropped (POC-2 recommendation adopted).** Console mode proved batch-only for all three frameworks — no per-test streaming, no inline passing tests, no incremental file report; MSTest/TUnit can't even emit UIDs in console mode, and xUnit v3 rejects the standard MTP flags in favour of its own CLI (`-list full`, xunit query filters). Maintaining a second, worse code path isn't justified; if a framework's server mode misbehaves, that's a bug to report upstream, surfaced to the user as a clear per-project error.

### 6.5 Fake adapter (`--fake`)

Same interface, zero external dependencies — this is a first-class feature, not a stub:

- Scenario scripts: `default` (~300 tests, mixed outcomes), `big` (10k for perf work), `flaky` (nondeterministic failures for failed-filter/rerun flows), `slow` (long-running tests to exercise spinners, cancellation, navigation-during-run), `files` (failures whose stack traces reference real files shipped in the tool's fixture folder, so the 'o' modal works end-to-end).
- Deterministic under `--fake-seed`; emits the same event stream shapes as real adapters including theory-case children appearing mid-run.
- Simulates watch (`--fake --watch` fires synthetic file-change → rebuild → re-discover diffs including added/removed tests) and continue.
- Powers the snapshot/interaction test suite (§12), and — per the §14 restructure — is deliberately the **first adapter built**, so the whole UI (Phases 1–2) is developed, reviewed, and signed off against it before any real platform code exists.

## 7. Build subsystem

- **`MSBuildLocator.RegisterDefaults()`** binds the tool to the host's .NET 10 SDK MSBuild at startup (mandatory before any `Microsoft.Build` type loads). POC-3 reproduced the failure mode precisely: the JIT resolves every `Microsoft.Build` type referenced by a method *before executing its first line*, so registration and MSBuild-type usage in the same method (including top-level statements, which compile to one `Main`) throws `FileNotFoundException: Microsoft.Build, Version=15.1.0.0`. Enforce the split structurally: registration in the entry point, all MSBuild use behind a separate method boundary, plus a code comment explaining why.
- **Evaluation in-process** (no target execution): TFM list, output paths per TFM, `Compile`/`AdditionalFiles` globs (for the watcher), `IsTestProject`, and the §6.2 detection signals — always **per-TFM**. POC-3 measured the cost honestly: ~1.1 s cold for a 12-combination solution, and warm reuse of the `ProjectCollection` saves only ~20% (evaluation itself — SDK imports, conditions, restore assets — is paid every time). Consequence, binding for watch mode: **re-evaluate only the changed project** (plus direct dependents if a project file changed), never the whole solution per file-watch event; the full-solution pass happens once at startup (budget ~1 s). Evaluations are cached and invalidated per project on `.csproj`/`.props`/`.targets` change. Prefer the MSBuild API over shelling to `dotnet msbuild -getProperty` — POC-3 observed the latter silently dropping a property from multi-property JSON queries.
- **`ProjectGraph`** over the full target set gives the dependency closure; its reverse edges give the *dependents* set used by watch mode (D5: changed project + transitive dependents).
- **Build execution out-of-process** via `dotnet build <proj> --nologo -v:quiet -consoleLoggerParameters:ErrorsOnly;NoSummary -tl:off`, parsing the canonical diagnostic format (`path(line,col): error CODE: message` — POC-6 confirmed the regex on induced CS1003/CS1001 errors; multi-line non-canonical messages are silently dropped, so the raw build output is kept attached to the project node as the fallback the user can open). Rationale for not using in-proc `BuildManager`: assembly-version binding pain inside a global tool, MSBuild node reuse gives us speed anyway, and a crashed build can't take the TUI down. If canonical-format parsing proves lossy, the upgrade path is `-bl` + `MSBuild.StructuredLogger` — noted, not planned.
- **Affected test projects build in parallel (`Task.WhenAll`) — mandatory, per POC-6.** Sequential builds *failed* the watch latency bar (median 4.30 s total, build alone 3.09 s = 73% of the cycle); the parallel estimate (max of the two project builds instead of their sum) lands ~3.05 s. Building the referenced closure is left to each `dotnet build` invocation (MSBuild handles shared dependencies via node reuse and file locks), but the fan-out over *test* projects is ours.
- **Packaging note (POC-6):** the `Microsoft.Build.*` NuGet packages lag the SDK's actual MSBuild (17.14.x package vs 18.x in-SDK) and 17.14.5 carries a vulnerability advisory (NU1903). Reference all `Microsoft.Build.*` packages with **`ExcludeAssets="runtime"`** — they're compile-time type surface only; MSBuildLocator loads the SDK's real (current, patched) MSBuild at runtime. This silences the advisory legitimately and avoids version-mismatch loads.
- **Staleness check** before discover/run: newest input timestamp (sources + project files + referenced project outputs) vs output assembly; only stale projects build. `--no-build` bypasses entirely.
- Build progress and failures are events like everything else: the project node shows a spinner while building, and `BuildFailed` attaches parsed diagnostics to the project node's detail view (D7). Diagnostics carry file paths → the 'o' modal works on build errors too, for free.

## 8. Discovery lifecycle & keeping the tree fresh

**There is no build-free way to enumerate the true test list** — discovery is a metadata/reflection scan of the compiled assembly by the framework's own discoverer. It is fast (no user code runs, sub-second to a few seconds for large assemblies) but only as current as the last build. ttr therefore treats the tree as a *projection of the latest build*, kept fresh by the watch pipeline:

1. **Initial population:** ensure build (§7) → run `DiscoverAsync` per (project, TFM) in parallel (capped at logical-CPU/2 sessions) → `TestsDiscovered` events stream nodes into the tree while discovery is still running elsewhere. The user can navigate immediately.
2. **Re-discovery diffing:** after any rebuild, re-discover only the changed projects; compute add/remove/keep sets by `TestCaseId`. Expansion and selection survive by id; if the selected node vanished, selection moves to the nearest surviving sibling/parent. Removed tests disappear; new tests appear as `NotRun` (or go straight to `Queued` if a watch-triggered rerun is starting).
3. **Runtime-enumerated cases (confirmed by POC-1):** some frameworks (xUnit with non-serialisable theory data) cannot enumerate parameterised cases at discovery time — the non-serialisable `MemberData` fixture discovered as **1 `TestCase` but produced 5 run results**. So the tree model must handle one discovery-time id fanning out into multiple arriving results: the method node exists from discovery, case children materialise from run events (keyed by result display name under the parent's `TestCaseId`). The tree treats "children appeared during run" as a normal `TestsDiscovered` event, so this needs no special path — but the reducer explicitly owns the 1→N mapping.

A Roslyn source-scan pre-pass (guessing tests from syntax before the first build finishes) was considered and rejected for v1: it lies about theory expansion and inherited tests, and the build+discover path is fast enough. Recorded as a possible future "instant skeleton" optimisation.

## 9. Watch subsystem

Two modes behind one `IWatchSource` abstraction (D4):

**`--watch build` (ttr drives):**
- `FileSystemWatcher` per project directory, filtered by the evaluated `Compile`/content globs plus `*.csproj/*.props/*.targets` (a project-file change additionally invalidates that project's MSBuild evaluation cache and re-runs detection §6.2 — **for the changed project only**, per POC-3's finding that full-solution re-evaluation costs ~1 s even warm).
- Debounce 300 ms with trailing-edge coalescing; editor temp-file/rename storms (`.tmp`, `~`, `4913`) filtered.
- Changed files → owning project(s) → + transitive dependents via `ProjectGraph` (held in memory from startup; reloaded only when `.csproj`/solution files change) → build affected test projects **in parallel** → on success: re-discover changed projects, diff tree, **auto-rerun** the affected test set. "Affected test set" = all tests in affected test projects, further narrowed to failed-only if the 'f' filter is active. Discovery is gated on build success (POC-6 AC6: a compile error stops the cycle before discovery — stale DLLs are never queried).
- **Measured latency budget (POC-6, 10-project fixture, warm):** debounce 0.35 s → parallel builds ~1.75 s (max, not sum) → discovery ~0.75 s per 200-test VSTest project (reusing the long-lived `VsTestConsoleWrapper` — recreating it per cycle wastes 300–500 ms) → diff ~0 ms; ≈ **3 s total** against the 4 s bar. Note: sequential builds *missed* the bar at 4.3 s, which is why parallelism is a §7 requirement, and the ~3 s figure is a computed estimate pending the Phase 5 implementation measuring it for real. MTP projects will cycle faster still (POC-2: warm discovery 5–57 ms vs VSTest's ~750 ms) — VSTest discovery is the scaling watch-item for very large projects.

**`--watch external` (user drives builds):**
- Watches the per-TFM **primary output assemblies only** (paths from evaluation) — POC-6 showed watching all of `bin/` means quiescing over ~170 transitive DLLs for no benefit. A write event starts a quiescence timer (file size/lastWrite stable for 500 ms) before treating the assembly as "new", then: re-discover → diff → auto-rerun as above. POC-6 refinements: on Linux the exclusive-open check is belt-and-braces only (no mandatory locking — stability window is the real signal), and **no-op incremental builds don't rewrite assemblies**, so they correctly produce zero cycles with no special-casing. The 500 ms quiescence is also why source-watching is the better default (§16 item resolved): mode B pays it on every cycle.

**Concurrency policy (v1):** changes arriving during an active run or build are queued and coalesced; the current run finishes, then one consolidated cycle executes. Cancel-and-restart is a config option candidate for Phase 7, not v1 — it interacts badly with frameworks whose fixtures hold external resources.

The status bar shows watch state: idle / change detected / building (project) / running (n of m). Everything remains navigable throughout.

## 10. State persistence (`--continue`)

- Location: `.ttr/` beside the primary target (first sln/slnx, else first csproj); overridable via `--state-dir`. ttr writes a `.ttr/.gitignore` containing `*` on first use.
- `state.json` (schema-versioned): target set + a fingerprint (paths + project-file content hashes) so `--continue` against a changed target set degrades gracefully; UI state (expansion id-set, selection id, f/s/b/w/t flags, pane orientation, scroll offsets); per-test summary (status, duration, finish time).
- **Result details sidecar (design settled by POC-8 ✅):** at 10k tests, full messages/stack traces/stdout don't belong in one JSON blob — but they don't belong in 1,000 tiny files either. POC-8 measured both and the **packed JSONL** shape won: `.ttr/results/<run-id>/details.jsonl` (one detail record per line) plus `details.idx` (test-id → byte offset/length). It saved 1.8× faster (32 ms vs 58 ms), lazy-read equally fast (< 1 ms), came out slightly smaller (5.62 MB vs 6.02 MB per run), and crash-safety needs 2 atomic renames per run instead of 1,001. Details lazy-load when a node's detail pane opens; `state.json` holds only summaries. Old runs pruned (keep last 3). **No transparent gzip** — the 10k-test footprint measured 8.57 MB raw, well inside the 20 MB budget, so compression isn't worth the CPU. Plain `System.Text.Json` is fine; source-generated contexts made no measurable difference (the cost is filesystem I/O, not serialisation).
- Written atomically (temp file + rename) after every completed run and on clean exit; a crash mid-run loses at most that run's in-flight results (D8).
- On `--continue`: restored results whose assembly has since been rebuilt are shown as **`Stale`** (dim tick/cross variant), making "these were the results, but the code changed" honest. A run replaces staleness with reality.

## 11. UI subsystem

### 11.1 Framework decision — evaluation against requirements

| Requirement | Spectre.Console (direct) | RazorConsole |
|---|---|---|
| Interactive navigable tree | Not built in — `Tree` is render-only. Custom `IRenderable` + own input loop | Not built in either — custom component over its VDOM; focus system helps |
| Fit-to-terminal, resize | `Live` + manual relayout; full control | Layout components exist; resize behaviour **unverified at our complexity** → POC |
| Ellipsis everywhere | Manual, cell-width-aware truncation (Spectre's segment measurement helps) | Has an ellipsis overflow translator built in |
| Syntax-highlighted modal | ColorCode integrated by hand | `SyntaxHighlighter` component (ColorCode) built in |
| Scrollable panes | Hand-rolled viewport | `ViewHeightScrollable` built in |
| Live updates during run @10k nodes | **Proven by POC-4 ✅: 29–30 fps at 500 events/s, ~4.3% CPU — but via direct ANSI writes, *not* `Live`** (see finding below) | VDOM diff cost at 10k-node trees **unknown** → POC |
| Key handling incl. Shift+letter | Raw `Console.ReadKey` — full control | Its input/focus pipeline; modifier fidelity **unverified** → POC |
| Maturity / risk | Stable, huge community | **0.0.3-alpha**; API churn and abandonment risk are real |

Reading of the matrix: RazorConsole's built-ins map almost one-to-one onto this spec (ellipsis, scrollable, syntax highlighting, focus) — it would save real work *if* it holds up at 10k nodes with our key handling and resize needs. Spectre-direct means hand-rolling all of that but with zero framework risk. Since RazorConsole renders **to** Spectre renderables anyway, the fallback direction is natural, and per D3 the call is made by POC-4/POC-5 scorecard, not taste. **Insurance either way:** everything above the `IUiShell` seam (state, reducers, view-models, flattening, ellipsis text preparation, keymap) is framework-agnostic, so a mid-project framework swap costs the rendering layer only.

**POC-4 outcome ✅ (baseline complete — all 7 bars passed):** 29–30 fps and 30–34 ms input latency p95 under the 500 events/s storm at ~4.3% CPU; zero over-width lines at all four terminal sizes; Shift+E cleanly distinguishable (`KeyChar 'E'` + `Shift` modifier vs `'e'`); clean resize storm; filter re-flatten at 10k nodes measured at **0.4 ms** (bar was 50 ms — flattening is effectively free, so no caching cleverness is warranted). One binding architectural finding: **do not use Spectre's `Live` for the main render loop.** The passing implementation writes direct ANSI — alternate screen (`\x1b[?1049h`), cursor-home then line-by-line frame writes — which gives full control over cursor-positioned modal overlays and hits 30 fps easily; Spectre's role in the baseline shrinks to **cell-width measurement** (`Segment.CellCount()`, verified correct for CJK — 8 cells for 4 CJK chars — and emoji). The Spectre-direct option should therefore be read as "direct ANSI renderer + Spectre as a measuring library". The decision gate now reduces to: RazorConsole must match a fully-green baseline to be chosen.

**POC-5 outcome ✅ / gate closed:** RazorConsole (0.6.0-alpha) matched the measurable bars — ~50 fps median under the storm, clean truncation at five sizes, Shift+E discrimination, working `SyntaxHighlighter` + `ViewHeightScrollable` — but the evaluation dismantled the matrix's premise: the ellipsis translator is a documentation example rather than a built-in, cell-width arithmetic and virtualisation and resize events don't exist (all hand-rolled in the POC, exactly as under direct ANSI), `ModalWindow` is in the README but absent from the package (only a full view swap is possible — ttr's spec requires a true overlay), and fast keypresses bypass `@onkeydown` via an undocumented batched-input path with unresolved cross-terminal behaviour. **Per the §13 gate rule, the direct-ANSI design from POC-4 is adopted**; RazorConsole's syntax-highlighting (ColorCode usage) and scroll-window approaches are carried over as techniques, not as a dependency. This section's comparison matrix is retained above as the historical record of the decision inputs.

### 11.2 Rendering architecture

- **View-model projection per frame:** `AppState` → flatten the tree honouring expansion + the failed-only filter → clamp selection → take the viewport slice `[scrollOffset, scrollOffset + visibleRows)` → produce row VMs (indent, status glyph or spinner frame, name **pre-ellipsised to available cells**, optional right-aligned duration, rollup counts on branch nodes). **Only viewport rows are materialised** — this is the D6 virtualisation and makes 10k (or 100k) nodes irrelevant to render cost. Flattening is O(visible nodes) and cached until tree/filter/expansion changes.
- **Layout:** status bar (top: targets, run totals, watch state, key hints) — tree pane — detail pane at right (default, 40%) or beneath (40% height) toggled by 'b' — one-line transient toast (e.g. "state saved", "2 tests removed") — modal overlay above everything.
- **Ellipsis is cell-aware:** widths measured in terminal cells (CJK/emoji are double-width) via Spectre's **`Segment.CellCount()`** (POC-4 verified it correct for CJK and emoji), truncated with `…`; applied to tree rows, detail pane lines (when wrap is off), and modal lines. No view ever writes past its box.
- **Resize:** react per frame to `Console.WindowWidth/Height` change (plus SIGWINCH where the framework surfaces it) → full relayout; below a minimum (roughly 40×10) render a "terminal too small" placeholder rather than corrupt output.
- **Status glyphs:** braille spinner (running), green `✓`, red `✗`, `○` not run, `⊘` skipped, dimmed `✓/✗` for `Stale`; branch nodes roll up (spinner if any descendant runs; cross if any failed). **Rollup counts (e.g. `12✓ 2✗`) are right-aligned in a fixed gutter at the right edge of the tree pane, not placed beside the node name** *(user feedback from Phase 1/2 fake-mode review — signed-off UX decision)*. When 't' is active the right side becomes two aligned columns — `[glyph name………] [counts] [duration]` — with the name's ellipsis budget shrinking to make room; leaf rows leave the counts column empty. ASCII fallback set behind a capability check for dumb terminals.
- **Detail pane ('s'):** message, exception chain, stack trace, output for the selected node (aggregated failure summary for branch nodes); independently scrollable; `Tab` moves focus between tree and detail; 'w' wraps long lines (off = horizontal-truncate with ellipsis). File references in stack frames render underlined as a cue that 'o' will work.
- **Times ('t'):** per-test duration right-aligned dim; branch nodes show aggregate (sum of descendant durations) — the run summary in the status bar separately shows wall-clock, so parallelism confusion ("children sum to more than the run took") is explained rather than hidden.
- **Render loop:** ≤30 fps, only on dirty state; spinner animation ticks mark dirty while anything runs. Batches of `TestFinished` events coalesce into single frames naturally because reduction and rendering are decoupled.

### 11.3 Keymap

| Key | Context | Action |
|---|---|---|
| ↑/↓ (k/j) | tree | Move selection |
| →/← | tree | Expand / collapse (← on leaf jumps to parent) |
| PgUp/PgDn, Home/End | tree/detail | Page / jump |
| `e` | tree | Toggle expand/collapse of selected node |
| `E` (Shift+E) | tree | Toggle expand/collapse of node **and all descendants** |
| `f` | global | Toggle failed-only filter |
| `s` | global | Toggle detail pane |
| `b` | global | Detail pane right ↔ beneath |
| `w` | tree focus: n/a; detail | Toggle word wrap in detail pane |
| `r` | tree | Rerun tests under selected node (failed-only respects 'f', §11.4) |
| `R` (Shift+R) | global | Rerun all (all-failed when 'f' active) |
| `t` | global | Toggle durations |
| `o` | tree/detail | Open first file reference of selected result in modal |
| `Tab` | global | Cycle focus tree ↔ detail |
| `?` | global | Help overlay (this table) |
| `q` / Ctrl+C | global | Quit (state saved) |
| Modal: `c` close, `n`/`p` next/prev file, `w` wrap, ↑/↓/PgUp/PgDn scroll | modal | Modal captures **all** input while open |

Shift+letter arrives as the uppercase `KeyChar` — reliable across Windows Terminal, conhost, and Unix terminals (unlike many Ctrl+letter combos, which collide with flow control and are avoided). Verified per-framework in POC-4/5.

### 11.4 Rerun & failed-filter semantics

- `r` collects the leaf `TestCaseId`s under the selected node; with 'f' active, only currently-failed leaves. `R` is the same rooted at Solution.
- Affected leaves flip to `Queued` (then `Running` as the platform reports activity); tree stays fully navigable; a second `r` elsewhere queues after the current run (same coalescing policy as watch, §9).
- Under 'f', a test that passes on rerun no longer matches the filter → it (and any branch left empty) disappears on the next flatten — exactly the requested "fix and watch them vanish" loop. The status bar keeps global totals visible so the shrinking view isn't disorienting.
- Rerun requires staleness check → possible rebuild first (D7); the node shows a build spinner phase before the run phase.

### 11.5 File-reference parsing & the 'o' modal

- **Parser validated by POC-9 ✅ (100% recall on a 57-sample real corpus, 108/108 tests green). Production note (Phase 2): the POC artifacts were never seeded into the repo, so `src/TtrParser` was re-implemented to this spec with a fixture-driven suite; the POC-9 corpus should be swapped in verbatim as the regression baseline if the artifacts surface.** It extracts ordered, de-duplicated file references from stack traces and messages: the canonical ` in <path>:line <n>` stack-frame form (regex `\sin\s+(.+?):line\s+(\d+)\s*$`), MSBuild diagnostic form `path(line,col)`, and bare absolute/relative paths with known source extensions. Relative paths resolve against the project directory; only files that exist become openable references (missing ones render dim). Rules hardened by the corpus: **frames whose path contains `/obj/` or ends `.g.cs` are filtered** (TUnit's source-generated glue lives in `obj/` and *would* pass the existence check — the user must never be sent to generated code); NUnit 4's duplicated stack traces (an `AggregateException` quirk of NUnit3TestAdapter 6.x) are absorbed by the (path,line) dedup with no special case; MSTest's `Test method … threw exception:` message prefix is harmless since the parser matches paths, not exception shapes; and on net10 async state machines unwind correctly in all six frameworks — user file + line appear directly, no `MoveNext()` handling needed (only relevant if older runtimes are ever targeted). **POC-2 bonus finding:** MTP test nodes carry structured `location.file`/`location.line-start` fields — for MTP projects the test's *own* source location comes free of parsing, so the parser's job there narrows to stack-trace and message extraction; the structured location is used as the first (default) 'o' target for MTP tests.
- **Localisation caveat (POC-9 AC4):** the ` in …:line N` words are runtime resource strings — German runtimes emit `:Zeile N`, Chinese `位于 …:行 N`, and the regex misses both. v1 mitigation: ttr spawns every test host itself, so it sets `DOTNET_CLI_UI_LANGUAGE=en` on child processes — this pins *UI-culture* resource strings without touching `CurrentCulture`, so culture-sensitive test logic is unaffected. The robust long-term option (PDB symbol reading for file/line instead of string parsing) is noted for Phase 8; MTP's structured locations already reduce exposure on that side.
- Modal: overlay ~90% of the terminal, title = file name + (index/total), syntax highlighting via **ColorCode** keyed on extension (plain text when unrecognised), line numbers, opened scrolled to the referenced line (highlighted). `n`/`p` cycle through the reference list, `w` toggles wrap, `c`/`Esc` closes. Large files are read lazily in line blocks; highlighting runs off the render thread with a plain-text first paint.

## 12. Testing strategy for ttr itself

- **Reducer/unit tests:** state transitions, tree diffing, `TestCaseId` stability, filename parser, watch coalescing, state round-trip — all pure, no terminal, no processes. **The parser's regression baseline is its fixture-driven suite from Phase 2; the POC-9 57-sample labelled corpus should replace/augment it verbatim if the POC artifacts surface (they were never seeded).**
- **UI snapshot tests:** drive `AppState` with scripted fake-adapter event streams + scripted key sequences, render frames into an in-memory test console, assert with **Verify** snapshots (covers ellipsis, layout toggles, resize, filter-vanish behaviour deterministically).
- **Integration fixture matrix** (real end-to-end, CI on Windows/Linux/macOS): {xUnit v2 (VSTest), xUnit v3 (MTP), NUnit + NUnit3TestAdapter (VSTest), NUnit 4 MTP (per docs.nunit.org — note POC-9 found the `EnableMicrosoftTestingPlatformRunner`+NUnit3TestAdapter 6.2.0 combination is a silent no-op, which is exactly what the §6.2 smoke validation exists to catch), MSTest.Sdk (MTP), TUnit} × {net10.0, one multi-TFM project} × {.sln, .slnx, bare csproj}, each fixture containing passing/failing/skipped/theory/slow tests and a failure whose stack references a source file.
- **Dogfooding:** ttr runs its own test suite from Phase 4 (first phase with real execution) onward; Phases 1–3 are covered by the fake-driven snapshot suite.

## 13. Phase 0 — proofs of concept

Each POC is a small throwaway repo with written findings; ~0.5–2 days each. Two are **decision gates**.

| POC | Question | Acceptance criteria | Fallback if failed |
|---|---|---|---|
| POC-1 ✅ **DONE — PASS (AC5 partial, resolved)** | VSTest translation layer: streamed discovery + streamed results + subset run + cancel, host located from a global-tool install | Streaming p95 = 27 ms (bar 250 ms) ✓; discovery streamed in 22 batches, no execution ✓; `ActiveTests` populated every callback ✓; subset exact ✓; **cancel: `CancelTestRun` FAILED (18.4 s) → adopt `AbortTestRun` (~25 ms)** ✓; both host strategies work, SDK-resolved preferred (portable nupkg host is net8.0) | Not needed — see binding cancellation rule in §6.3 |
| POC-2 ✅ **DONE — ALL PASS (AC7 partial by design)** | MTP server mode: JSON-RPC client (StreamJsonRpc) does initialize/discover/run/updates/cancel against xUnit v3, MSTest.Sdk, TUnit | Streaming p95 45–228 ms ✓ (200 ms platform batching noted); in-progress notifications ✓ all three; subset by UID exact ✓; cancel ~2 s, server survives ✓; process reuse: warm discovery 5–57 ms ✓; identical protocol across MTP 1.9.1–2.2.3, full traces captured; binding rules in §6.4 | **Fallback investigated and rejected:** console mode is batch-only for all frameworks (xUnit v3 doesn't even accept MTP flags) — server mode is the sole MTP path |
| POC-3 ✅ **DONE — PASS, and it corrected the rule** | Detection heuristics (§6.2) classify the full fixture matrix correctly | 11/11 classified and **empirically verified** (byte-level `--server` handshake per MTP binary); found the paper rule's flaw: `UseMicrosoftTestingPlatformRunner` on classic packages is a dead flag (no package reads it) → V2 rule keys on **`IsTestingPlatformApplication`** per-TFM, dead flags → VSTest + warning; global.json ruled out as a signal; evaluation timing: ~1.1 s cold / warm only ~20% better → changed-project-only re-evaluation in watch | Not needed — corrected rule adopted verbatim in §6.2 |
| POC-4 ✅ **DONE — ALL 7 PASS (gate baseline set)** | Spectre-direct: virtualised interactive tree @10k nodes, input during simulated result storm (500 events/s), resize, ellipsis, modal overlay, Shift+E | 29–30 fps ✓ (bar 25); input p95 30–34 ms ✓ (bar 50); zero over-width lines at 4 sizes ✓; Shift+E clean ✓; modal ✓; resize storm clean ✓; filter re-flatten 0.4 ms ✓ (bar 50); ~4.3% CPU. **Finding: bypass Spectre `Live` — direct ANSI (alt-screen + cursor-home line writes); keep Spectre for `Segment.CellCount()`** (§11.1). Linux verified; Win/macOS terminal pass deferred to Phase 4 CI | — (baseline; POC-5 must match every green bar to win) |
| POC-5 ✅ **DONE — AC1–7 PASS, AC8 reveals material gaps** | RazorConsole (0.6.0-alpha tested): identical scenario + `SyntaxHighlighter`, `ViewHeightScrollable`, ellipsis translator | Measurable bars all green: ~50 fps median under storm ✓; over-width 0 at 5 sizes ✓; Shift+E ✓; re-flatten p95 ≤ 4.5 ms ✓ (latency figure 0.4–0.6 ms is *not* comparable to POC-4's — it measures `StateHasChanged` round-trip only, excluding the 20 ms tick, where POC-4 measured key-to-frame). **AC8: 2 High findings — `ModalWindow` doesn't exist (README lists it; only a full view swap is possible, no true overlay) and fast typing bypasses `@onkeydown` (batched `@oninput` path, terminal-emulator portability an open question). Plus: ellipsis translator is a docs example not a built-in, no cell-width awareness, no resize events, no virtualisation — the matrix's claimed built-ins largely evaporated. GC pauses dip fps to ~39. Alpha churned 0.0.3→0.6.0 during Phase 0** | Gate applied — see gate outcome below |
| POC-6 ✅ **DONE — PASS with one conditional** | Watch pipeline: change → ProjectGraph dependents → incremental build → re-discover diff latency on a 10-project fixture | Dependents-sets exactly correct (3 cases) ✓; storm coalescing: rename-save/in-place/3-file burst each = exactly 1 cycle ✓; diff +1/−1 with correct FQN ✓; external mode 1 cycle per real build, 0 for no-op ✓; compile error parsed, discovery gated ✓. **AC2: sequential builds FAILED (4.30 s median) — parallel builds land ~3.05 s (computed, not yet measured)**; build = 73% of cycle cost; debounce 300 ms absorbs Linux inotify multi-events | Parallelism promoted to a §7 requirement; real parallel measurement is a Phase 5 exit criterion |
| POC-7 ✅ **DONE — ALL 5 PASS** | `Microsoft.VisualStudio.SolutionPersistence` on gnarly .sln and .slnx (solution folders, shared projects) | Identical project sets both formats ✓ (via `dotnet sln migrate` twin); item-only + nested solution folders disjoint from projects ✓; malformed inputs all catchable ✓ (but exception types differ per format — contract in §4); phantom projects load silently → explicit `File.Exists` pass ✓; round-trip preserves set (byte-identical for `.slnx`) ✓; ~50 ms one-time init then sub-ms warm reloads; **pin 1.0.52** | Not needed |
| POC-8 ✅ **DONE — ALL PASS** | Continue: save/restore of 10k-test state + sidecar details | Save < 200 ms (measured: median 55–108 ms, p95 ≤ 146 ms); restore < 500 ms (measured: median 44 ms); lazy detail < 20 ms (measured: p95 1 ms); footprint < 20 MB (measured: 8.57 MB); 5/5 kill-9 atomicity; graceful hash-mismatch; pruning correct | Not needed — adopt packed JSONL sidecar (§10) |
| POC-9 ✅ **DONE — ALL 5 PASS** | File-reference parsing across all six frameworks' real failure text | **100% recall** (bar 95%) on a 57-sample platform-API-captured corpus + 5 MSBuild diagnostics; 108/108 corpus tests green; async top-frames correct on net10 in all six frameworks; relative-path + existence filtering proven; localisation risk documented (English resource strings — mitigation §11.5); parser is a liftable zero-dependency library adopted directly, corpus becomes the production regression suite | Not needed |

**Gate rule:** RazorConsole is chosen only if POC-5 meets every bar POC-4 meets; ties go to Spectre-direct on maturity (alpha status is a standing liability). If RazorConsole wins, pin the exact version and vendor-fork tolerance is budgeted (it is MIT-licensed).

**GATE OUTCOME (both POCs complete): direct-ANSI renderer selected (the POC-4 design).** Applying the rule as written: POC-5 passed the seven measurable bars, but its acceptance criteria also required "no blocking alpha defects", and AC8 surfaced two High-severity findings that touch ttr's *core spec*, not its periphery — (1) no true modal overlay exists (the 'o' requirement is literally "an overlaying modal"; a full view swap is the only workaround), and (2) the batched-input path that swallows fast `@onkeydown` events sits directly on ttr's primary interaction (sustained keyboard navigation), with its cross-terminal behaviour an open question even in the POC's own findings. At best this is a tie on measurables — and the tie-breaker is maturity, underlined by the package churning 0.0.3 → 0.6.0-alpha within Phase 0. Decisive context: POC-5 also showed the advantages that originally motivated RazorConsole (built-in ellipsis, cell-width handling, modal, resize, virtualisation) mostly don't exist — everything ttr would hand-roll under direct ANSI, it hand-rolls under RazorConsole too, minus overlay capability and plus an alpha dependency. **Recorded dissent:** the POC-5 agent recommended adopting RazorConsole, valuing its VDOM/focus/key-routing and judging the workarounds production-ready — a defensible read, rejected here because the gate rule was agreed precisely to prevent relitigating on taste. **Salvage:** RazorConsole's two genuine wins are ported as *techniques*: the modal's syntax view uses ColorCode exactly as `SyntaxHighlighter` does, and its scroll-window logic informs the detail-pane viewport. Per-frame `Console.WindowWidth/Height` polling (both POCs converged on it) is confirmed as the resize mechanism.

## 14. Delivery phases

Restructured (post-Phase 0) into small, **individually reviewable increments**. Ordering principle: the fake adapter ships first so the *entire UI is reviewable and sign-off-able before any real test infrastructure exists*; each phase ends with a concrete "how you review it" action, and no phase depends on an unreviewed predecessor. The fake adapter is also what makes the UI phases testable in CI from day one.

- **Phase 0 — POCs & decisions ✅ COMPLETE** (§13): direct-ANSI renderer decided; MTP contract codified (§6.4); detection rule corrected (§6.2); parser library + corpus (POC-9) and persistence design (POC-8) transfer into the production repo.

- **Phase 1 — Fake-first UI core. ✅ COMPLETE** (owner-reviewed; layout and performance approved; one amendment — right-aligned rollup counts — folded into Phase 2). *Scope:* CLI skeleton with `--fake [scenario]`/`--fake-seed` only; MVU core (channel, reducers, `AppState`); direct-ANSI renderer (alt-screen, 30 fps loop, `Segment.CellCount()` ellipsis); virtualised tree fed by the Fake adapter; navigation (↑/↓/PgUp/PgDn/Home/End), `e`/`E`, status glyphs + spinners streaming live from the `default` and `slow` scenarios; `q`, `?` stub; resize handling incl. too-small placeholder. **Review:** run `ttr --fake` and `ttr --fake slow`, navigate during a live "run". *No MSBuild, no test platforms, no persistence.*

- **Phase 2 — Complete interaction set (still fake-only). ✅ COMPLETE** (all AC PASS; 29–30 fps / p95 31–32 ms on `--fake big` with detail+times; UX frozen behind a 15-snapshot Verify suite, 85 tests green; ColorCode.Core 2.0.15 + Verify.Xunit 31.12.5 added; **deviation: `TtrParser` was implemented to the §11.5/POC-9 spec rather than lifted — the POC-9 artifacts were never seeded; swap in the original corpus verbatim if it surfaces**). *Scope:* detail pane (`s`, `b`, `w`, Tab focus, independent scroll); `f` failed-filter with the vanish-on-pass flatten; `r`/`R` rerun semantics driven by the Fake adapter (incl. failed-only narrowing); `t` durations + rollups; toasts; the **'o' modal** (ColorCode syntax highlighting, `c`/`n`/`p`/`w`, scroll, jump-to-line) against the `files` scenario; help overlay for real; snapshot test suite (scripted fake streams + key scripts → Verify frames). **Review:** the *entire UX* is exercisable and can be signed off here — every key in §11.3 works against `ttr --fake files` / `--fake flaky` / `--fake big` (10k perf check). Backend phases that follow cannot change the UX contract without coming back to this suite.

- **Phase 3 — Real targets & discovery (read-only). ✅ COMPLETE** (125 tests green; both adapters proven end-to-end on representative frameworks — xUnit v2/VSTest and xUnit v3/MTP; MTP client corrected against the live protocol — single-object params, flat dotted keys; all 15 Phase 2 snapshots unchanged + 6 new backend-state snapshots. **Carried debt into Phase 4: the remaining four matrix fixtures (NUnit classic, NUnit 4 MTP, MSTest.Sdk, TUnit + the broken-NUnit smoke fixture), real multi-TFM build verification, and the Win/macOS CI lanes.**) *Scope:* target args + CWD scan + auto-select + interactive picker; sln/slnx via SolutionPersistence with the §4 error/phantom contract; MSBuild evaluation, §6.2 detection + runner smoke validation; build service (out-of-proc, **parallel over test projects**, canonical-error parsing → project error nodes); streamed discovery via both adapters (VSTest §6.3, MTP §6.4) populating the tree; the 1→N theory mapping. *No test execution yet.* **Review:** point ttr at the §12 fixture matrix and a real repo; watch the tree populate; break a file and see build errors attach.

- **Phase 4 — Real runs. ✅ COMPLETE** (all AC PASS on Linux; 7 fixture frameworks running live; 1→N theory materialisation proven real; abort sweep covers Queued as well as Running; exit-code dogfood job in CI; Windows VT enablement implemented — Win/macOS lanes authored, manual conhost smoke still owed by owner. **Carried into Phase 5: NUnit4-MTP server handshake failed (contradicts POC-3's empirical pass — timeboxed investigation scheduled); the sandbox NuGet mount leak makes in-app rebuild flaky, which watch mode exercises constantly.** MTP `serverInfo.version` in the wild: xUnit v3 1.9.1, MSTest.Sdk 1.0.0, TUnit 1.0.0 — the §6.4 warn-on-major-jump check must tolerate DOWNWARD-looking values too.) *Scope:* run/rerun (`r`/`R`) through both adapters with live streaming; abort/cancel rules (`AbortTestRun`, `$/cancelRequest`, phantom-spinner sweep); run queueing/coalescing; exit codes; dogfooding starts (ttr runs its own suite); cross-OS CI stands up — including the **Windows Terminal/conhost + macOS verification deferred from POC-4**. **Review:** full interactive runner on real projects; CI green on three OSes.

- **Phase 5 — Watch mode. ✅ COMPLETE (one exit criterion STOP-and-reported).** *Scope:* `--watch` (source mode, default) and `--watch external` per §9, debounce/quiescence/coalescing, changed-project-only re-evaluation, ProjectGraph dependents, auto-rerun with filter narrowing — all implemented and verified (fake-first snapshots + pure reducer/graph/debounce units + real tmux runs; AC1–AC4, AC6–AC9 PASS). *Exit criterion:* **measured** end-to-end single-file-change cycle < 4 s median on the POC-6 fixture with parallel builds — closes POC-6's conditional AC2. **STOP-and-report:** the pipeline meets the budget (non-build overhead ≈ 1.6 s; diff free), but `dotnet build` of the diamond chain in the agent sandbox is ~2.7× POC-6's real-hardware estimate, so the in-sandbox median is 6.3 s (38 s on the host-mounted FS). The `watch-latency` CI job asserts `< 4 s` on real Linux hardware and is the authoritative lane (see `docs/phase-5-notes.md` §M7). The NUnit4-MTP carry-in is resolved (outcome b: version regression); the sandbox rebuild-flake carry-in is resolved (watch's per-project build is unaffected). **Review:** edit-save-watch loop on the fixture; latency numbers + AC verdict in the PR description.

- **Phase 6 — Sessions (`--continue`).** *Scope:* §10 persistence (state.json + packed JSONL sidecars, atomic writes, pruning), `Stale` presentation, target-hash mismatch degradation, save-after-run + on-exit. **Review:** run, quit, `ttr --continue`, verify restored tree/filters/results and Stale marking after a rebuild.

- **Phase 7 — Packaging & polish.** *Scope:* dotnet global tool packaging/signing; ASCII glyph fallback + capability detection; `--log` diagnostics; `--no-build`/`--tfm`/`--state-dir` flags finalised; README + demo cast; final rename from `ttr` if a product name has landed (§16.1). **Review:** `dotnet tool install` from a local feed on a clean machine; follow the README cold.

- **Phase 8 — Future (explicitly out of v1):** per-test impact analysis (coverage-map based), mouse support, external `$EDITOR` handoff, Roslyn "instant skeleton" pre-discovery, per-user global config, themes, non-interactive CI mode, `--filter` expressions, PDB-based file/line resolution (§11.5).

## 15. Risks & mitigations

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| MTP server protocol shifts between versions | Low / High *(downgraded)* | **Validated by POC-2:** identical protocol/capabilities observed across MTP 1.9.1–2.2.3 on three frameworks; binding rules codified in §6.4; `serverInfo.version` checked at handshake with a warning on major-version jumps; unknown notifications tolerated. Console fallback deliberately dropped — a misbehaving server mode is surfaced as a per-project error and reported upstream |
| RazorConsole alpha instability or abandonment | **Retired — not adopted** | Gate outcome (§13): direct-ANSI renderer selected; RazorConsole is no longer a dependency. Residual exposure: none (techniques ported, no package reference) |
| vstest.console resolution breaks on some installs | Low / Med *(downgraded)* | **Validated by POC-1:** SDK-resolved location (Strategy A) works and is primary; `Microsoft.TestPlatform.Portable` payload confirmed as working fallback (note: its host is net8.0) |
| 10k-test memory/GC pressure from result details | ~~Med~~ **Retired** / Med | **Validated by POC-8:** packed JSONL sidecar + lazy load measured at 8.57 MB on disk, 44 ms restore, sub-ms lazy reads, zero main-loop jitter impact beyond OS noise; `big` fake scenario stays in perf CI as regression guard |
| Theory cases unenumerable pre-run | Certain / Low | Dynamic children are a first-class event path (§8.3) |
| FileSystemWatcher unreliability (WSL, network drives, editor rename storms) | Med / Med | Debounce + quiescence checks; polling fallback flag if needed |
| Terminal capability variance (glyphs, colors, Shift keys) | Med / Low | Capability detection + ASCII/basic-color fallbacks; POC-4/5 test all three OSes |
| Localised .NET runtimes break stack-frame parsing (`:line` → `:Zeile`/`:行`) | Low / Low | **Identified by POC-9:** ttr sets `DOTNET_CLI_UI_LANGUAGE=en` on every test host it spawns (UI-culture only — `CurrentCulture` untouched, so culture-sensitive tests behave normally); MTP structured locations bypass parsing entirely; PDB symbol reading is the Phase 8 robust option. Residual gap: exceptions pre-formatted by user code in a foreign locale |
| Mixed-platform solutions (VSTest + MTP in one sln) confuse UX | Med / Low | Per-adapter orchestration is already per-project; detection override in config. **POC-3 confirmed mixed solutions are exactly where `dotnet test` hard-fails on .NET 10 (global.json `test.runner` breaks one side in either state) — ttr's native-protocol approach is the only path that runs such solutions uniformly, so this is a differentiator, not just a risk** |

## 16. Open items (non-blocking)

1. Final product name (replaces `ttr` and `.ttr/`).
2. Colour theme/branding — defaults chosen in Phase 1, revisit in Phase 7.
3. ~~Whether `--watch build` or `--watch external` should be the bare `--watch` default~~ **Resolved by POC-6:** bare `--watch` = source-watch/build mode — it fires on save (before any build exists) and avoids external mode's per-cycle 500 ms quiescence tax; `--watch external` remains for build-outside-ttr workflows. **Follow-up carried into Phase 5:** the < 4 s bar was met only by the *computed* parallel-build figure (~3.05 s); measuring it for real is a Phase 5 exit criterion, and VSTest discovery (~750 ms per 200-test project) is the term to watch on much larger projects.
4. Per-user global config file — deferred to Phase 8 per interrogation.
5. *(From POC-3)* Whether `IsTestingPlatformApplication` and the other §6.2 signals are reliably available from a lighter-weight evaluation path (design-time build caches, as VS/OmniSharp use) rather than full per-TFM evaluation — worth a micro-POC only if the changed-project-only strategy still proves too slow on very large single projects.
6. *(From POC-7)* Solution parsing at enterprise scale (100+ projects), Unicode project/folder names, and cross-folder duplicate project names were not exercised — re-validate with a large fixture before Phase 5 finalises watch-mode reload assumptions (expected fine given sub-ms warm loads at small scale, but unverified).

---

## Appendix A — `state.json` sketch

```jsonc
{
  "schema": 1,
  "createdUtc": "2026-07-04T12:00:00Z",
  "targets": [{ "path": "src/App.sln", "hash": "sha256:…" }],
  "ui": {
    "filters": { "failedOnly": true, "details": true, "detailsBottom": false,
                  "wrap": false, "times": true },
    "expanded": ["node-id…"],
    "selected": "node-id",
    "scroll": { "tree": 120, "detail": 0 }
  },
  "tests": [
    { "id": "…", "status": "Failed", "durationMs": 43,
      "finishedUtc": "…", "hasDetail": true }
  ],
  // details live in results/<runId>/details.jsonl, located via the
  // sibling details.idx (test-id -> byte offset/length). Two atomic
  // renames per run (jsonl + idx). Design validated by POC-8.
  "runs": { "keep": 3, "latest": "run-7" }
}
```

## Appendix B — core `AppEvent` set

`TargetsResolved`, `EvaluationCompleted`, `BuildStarted/Progress/Succeeded/Failed`, `DiscoveryStarted/TestsDiscovered/DiscoveryCompleted`, `RunQueued/TestStarted/TestFinished/RunCompleted/RunCancelled`, `FileChanged`, `WatchCycleStarted/Completed`, `StateSaved/StateRestored`, `KeyPressed`, `FocusChanged`, `Resized`, `Toast`, `FatalError`.

## Appendix C — Agent sandbox: firewall allowlist & tooling dependencies

Assumption: the .NET 10 SDK is preinstalled and NuGet restore is permitted. The lists below are tiered so the sandbox can start minimal and widen only when a phase needs it. Prefer allowing telemetry **opt-out via environment variables** over opening telemetry endpoints: set `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1`, and `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` in the sandbox image so nothing tries to phone home in the first place.

### C.1 Firewall allowlist (domains)

**Tier 1 — required for restore/build (the tool cannot be developed without these):**

| Domain | Purpose |
|---|---|
| `api.nuget.org` | NuGet v3 service index, package download (flat container), search — covers `dotnet restore`, `dotnet tool install`, `dotnet add package` |
| `www.nuget.org`, `nuget.org` | Package pages / metadata the agent will read when choosing versions (Spectre, RazorConsole alpha, `Microsoft.TestPlatform.TranslationLayer`, `Microsoft.Testing.Platform`, StreamJsonRpc, `Microsoft.VisualStudio.SolutionPersistence`, MSBuildLocator, ColorCode, Verify, System.CommandLine) |
| `aka.ms` | Microsoft link shortener embedded throughout dotnet tooling output, MTP/MSBuild diagnostics links, and docs redirects — blocked aka.ms makes many error messages dead-ends for the agent |

**Tier 2 — required for the research/POC work in this plan (documentation and source):**

| Domain | Purpose |
|---|---|
| `learn.microsoft.com` | Primary docs: MTP (incl. server mode & extension architecture), VSTest translation layer, MSBuild API/ProjectGraph, System.CommandLine, dotnet CLI |
| `github.com`, `api.github.com`, `raw.githubusercontent.com`, `codeload.github.com`, `objects.githubusercontent.com` | Source-level reference is unavoidable for this project: `microsoft/testfx` (MTP + server-mode JSON-RPC internals — the protocol is best learned from source/tests), `microsoft/vstest`, `spectreconsole/spectre.console`, `RazorConsole/RazorConsole`, `microsoft/vs-streamjsonrpc`, `microsoft/vs-solutionpersistence`, `dotnet/msbuild`, `dotnet/command-line-api`, `VerifyTests/Verify`, `CommunityToolkit/ColorCode-Universal`. Also covers cloning fixture repos and release-asset downloads |
| `spectreconsole.net` | Spectre.Console documentation (Live display, measurement/segments, test console) |
| `razorconsole.github.io` | RazorConsole docs/tutorial (POC-5) |
| `xunit.net` | xUnit v2 vs v3 differences, MTP mode docs (fixture matrix + adapter behaviour) |
| `nunit.org`, `docs.nunit.org` | NUnit adapter / NUnit 4 MTP docs |
| `tunit.dev` | TUnit docs (MTP-native fixture) |

**Tier 3 — optional, enable on demand:**

| Domain | Purpose |
|---|---|
| `dotnet.microsoft.com`, `builds.dotnet.microsoft.com`, `dot.net` | Only if the sandbox needs to pin/install additional SDK versions via `dotnet-install` scripts (e.g. testing against a newer 10.0.x band) |
| `symbols.nuget.org`, `msdl.microsoft.com` | Symbol servers — only if the agent ends up debugging into platform assemblies during POC-1/POC-2 |
| `globalcdn.nuget.org` | Legacy NuGet CDN host some flows still touch; harmless to include with Tier 1 if restore ever stalls |
| `pkgs.dev.azure.com` | Only if a POC needs nightly/preview `Microsoft.Testing.Platform` builds from the dotnet public feeds; otherwise keep closed |
| `msbuildlog.com` | MSBuild.StructuredLogger docs — only if the §7 canonical-format fallback is exercised |

Everything else stays blocked. In particular there is no need for Stack Overflow, package mirrors, or general web access — the agent's system prompt should state that the Tier 1–2 set is the intended universe so it doesn't burn cycles probing the firewall.

### C.2 Tooling dependencies beyond `dotnet` + NuGet

| Tool | Why it's needed | Tier |
|---|---|---|
| `git` | Version control; cloning reference repos and fixture matrix; Verify snapshot diffing works best in a git repo | Required |
| **`tmux`** | The single most important addition: ttr is a TUI, and an agent cannot smoke-test one from a dumb pipe. `tmux new-session` gives a real PTY at a controlled size, `send-keys` drives the keymap (`e`, `E`, `f`, `r`…), `capture-pane` reads back the rendered frame, and `resize-window` exercises the fit-to-terminal requirement. This turns "run the app and check the tree" into something the agent can actually do deterministically | Required |
| `ncurses-term` / full terminfo database, with `TERM=xterm-256color` | Correct capability detection (colors, alternate screen). A missing terminfo entry silently degrades Spectre/RazorConsole output and will send the agent chasing ghost rendering bugs | Required |
| UTF-8 locale (`locales` pkg, e.g. `en_US.UTF-8`, `LANG`/`LC_ALL` set) | Spinner/glyph set (`✓ ✗ ⊘`, braille frames) and cell-width measurement depend on it; also validates the ASCII-fallback path by *unsetting* it in one test lane | Required |
| `libicu` (distro package) | .NET globalization on Linux; without it dotnet runs invariant-mode, which skews string/width behaviour vs. real user machines | Required |
| `ca-certificates` | TLS for NuGet/GitHub (usually present; listed because minimal images omit it) | Required |
| `unzip` (or `tar`) | `.nupkg` files are zips — the agent will crack open `Microsoft.TestPlatform.Portable` and RazorConsole packages during POC-1/POC-5 payload work | Recommended |
| `procps` (`ps`), `lsof` | Diagnosing orphaned `testhost`/MTP server processes and port usage during POC-1/POC-2 — a near-certain debugging scenario | Recommended |
| `jq`, `ripgrep` | Agent conveniences: inspecting `state.json`/sidecars and searching cloned platform sources quickly | Recommended |
| `asciinema` | Phase 7 only — recording the demo cast for the README | Optional |

Not needed: Node.js, Python, a C/C++ toolchain (the project is pure managed code with no native builds), Docker-in-sandbox, or a display server — everything renders to the PTY.

One sandbox-behaviour note: VSTest and MTP both spawn child processes (testhost / the test executable in server mode) and MTP server mode opens a **localhost TCP port** for its JSON-RPC channel. The sandbox must permit intra-sandbox process spawning and loopback networking — a firewall that blocks localhost connections will make POC-2 fail in a way that looks like a protocol bug.
