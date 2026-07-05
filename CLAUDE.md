# CLAUDE.md — ttr (Terminal Test Runner)

ttr is a cross-platform .NET terminal (TUI) unit-test runner and visualiser: it discovers
and runs tests via platform APIs (never by scraping `dotnet test`), shows them in a live
navigable tree, and supports watch mode, session resume, and an in-app source viewer.

**Authoritative documents (read the section a task cites before coding it):**
- `docs/ttr-implementation-plan.md` — the architecture plan. §-references in briefs point here.
- `docs/ttr-poc-prompts.md` — historical POC briefs (context only).
- The current phase brief (provided per session). **One phase = one PR. Do not build ahead.**

## Repo layout

```
src/ttr.Cli       entry point, System.CommandLine, composition root
src/ttr.Ui        rendering + input (direct ANSI; behind IUiShell)
src/ttr.Core      domain model, AppState, reducers, orchestration (no UI deps)
src/ttr.Runners   ITestSessionAdapter implementations: Fake, VsTest, Mtp
src/ttr.Build     MSBuildLocator, evaluation, ProjectGraph, build execution
src/TtrParser     file-reference parser (implemented to plan §11.5/POC-9 spec in Phase 2; treat as stable)
tests/            unit + snapshot tests; tests/corpus/ is the parser baseline
docs/             plan, POC prompts, phase briefs
fixtures/         sample test projects (added from Phase 3 on)
```

Dependency arrows point downward only. `ttr.Core` must never reference `ttr.Ui` or any
rendering/terminal type.

## Architecture invariants (violations are PR-blockers)

1. **Message-driven MVU.** All inputs (adapter events, build events, watcher events,
   keystrokes, resize) are typed `AppEvent`s on ONE `Channel<AppEvent>`. A single consumer
   applies pure reducers producing new `AppState` snapshots. No state mutation anywhere else.
   No locks. Side effects are launched by the orchestrator in reaction to state, each with a
   `CancellationToken`.
2. **Rendering never blocks event processing.** Render loop ≤ 30 fps, dirty-flag gated,
   reads a state snapshot. Input runs on a dedicated thread (`Console.ReadKey(true)`).
3. **The UI is virtualised.** Flatten expanded+filtered nodes to a list; materialise ONLY
   the viewport slice. Never render or allocate per-node for the whole tree (10k+ nodes).
4. **Direct ANSI rendering — NOT Spectre `Live`** (POC-4 finding). Alternate screen
   `\x1b[?1049h`, cursor-home + line-by-line frame writes. Spectre.Console is used ONLY as
   a measuring library: `Segment.CellCount()` for cell widths (CJK/emoji are 2 cells).
5. **No line ever exceeds its pane width.** Every rendered string is cell-aware truncated
   with `…` before writing. Poll `Console.WindowWidth/Height` every frame; below ~40×10
   render a "terminal too small" placeholder.
6. **Adapters are interchangeable.** Everything test-platform-specific lives behind
   `ITestSessionAdapter` (Fake / VsTest / Mtp). UI and Core never know which one is running.
7. **Identity = derived `TestCaseId`** (adapter kind + project path + TFM + FQN + param
   display hash). Theory FQNs are NOT unique; platform-native ids are session-scoped and
   kept only for execution. All diffing, rerun selection, and persistence key on the
   derived id.

## Binding technical rules (each one was proven the hard way in Phase 0)

**VSTest adapter (plan §6.3):**
- Cancel with `AbortTestRun()` (~25 ms). NEVER `CancelTestRun()` (18 s with xUnit v2).
- After an abort, the reducer MUST sweep any still-`Running` tests to `NotRun`/`Stale` —
  no phantom spinners.
- Reuse one `VsTestConsoleWrapper` for the process lifetime (recreating costs 300–500 ms).
- Locate vstest.console from the SDK directory (Strategy A); `Microsoft.TestPlatform.Portable`
  payload is the fallback (its host is net8.0).
- One discovered `TestCase` can yield MULTIPLE run results (non-serialisable theory data);
  the reducer owns the 1→N mapping, keyed by result display name under the parent id.

**MTP adapter (plan §6.4):**
- ttr LISTENS first (TcpListener port 0); launch host with
  `--server --client-host localhost --client-port <port>` — the host connects outward.
- StreamJsonRpc with LSP `Content-Length` framing (`HeaderDelimitedMessageHandler`).
- NEVER send `"tests": null` — omit the field for run/discover-all; non-null array for subsets.
- Params are named objects: `InvokeWithParameterObjectAsync`, never positional.
- ONE request in flight per session (`experimental_multiRequestSupport: false`).
- Completion = the sentinel notification `testing/testUpdates/tests` with `changes: null`,
  which arrives BEFORE the JSON-RPC response. Sentinel is primary.
- `exit` may hang: 5 s timeout then `Process.Kill(entireProcessTree: true)`.
- Tolerate and log unknown notifications (`telemetry/update`, `client/log`).
- Results batch on a 200 ms platform timer — fast suites arrive as one burst; spinners are
  driven by `in-progress` notifications. The coalescing render loop absorbs this.
- Read `serverInfo.version` at handshake; warn on major-version jump (tested 1.9.1–2.2.3).

**MSBuild / build (plan §6.2, §7):**
- `MSBuildLocator.RegisterDefaults()` must run in a method that references ZERO
  `Microsoft.Build` types; all MSBuild use goes behind a separate method boundary
  (JIT resolves types before first-line execution — this crashes otherwise).
- All `Microsoft.Build.*` package references carry `ExcludeAssets="runtime"`.
- Evaluate per-TFM (pass `TargetFramework` as a global property per value).
- MTP detection signal is `IsTestingPlatformApplication == true` — NEVER the
  framework-specific `Enable*Runner` properties (dead flags exist; classify those
  VSTest + "incomplete MTP migration?" warning).
- Watch mode re-evaluates ONLY the changed project (full-solution evaluation ≈ 1 s even warm).
- Builds run out-of-process (`dotnet build --nologo -v:quiet -tl:off
  -consoleLoggerParameters:ErrorsOnly;NoSummary`); affected TEST projects build in
  PARALLEL (`Task.WhenAll`) — sequential builds fail the watch latency bar.
- Discovery is gated on build success; stale DLLs are never queried.
- First discovery per project doubles as a smoke check: an MTP-classified host exiting
  without a handshake, or zero tests from an `IsTestProject` project, becomes an explicit
  warning node — never a silently empty subtree.

**Solutions (plan §4):** `Microsoft.VisualStudio.SolutionPersistence` pinned 1.0.52. Catch
`SolutionException`, raw `XmlException` (slnx is NOT wrapped), `FileNotFoundException`, and
handle a null serializer as "unsupported extension". Loaded ≠ intact (truncated .sln parses).
Classify by `TypeId` GUID, never the `Type` string. Run `File.Exists` on every resolved
project path; phantoms are non-fatal warning nodes.

**Parser / 'o' feature (plan §11.5):** `src/TtrParser` (built to the POC-9 spec in Phase 2) and
its fixture suite are the regression baseline — extend fixtures rather than editing expectations;
swap in the original POC-9 corpus verbatim if those artifacts surface.
Filter frames whose path contains `/obj/` or ends `.g.cs`. Set `DOTNET_CLI_UI_LANGUAGE=en`
on every spawned test host (UI-culture only; `CurrentCulture` untouched). MTP nodes carry
structured `location.file`/`location.line-start` — use as the default 'o' target.

**Persistence (plan §10):** `state.json` index + packed `results/<runId>/details.jsonl`
with `details.idx` offset index (NOT per-test files, NOT gzip). ALL writes atomic:
temp file + `File.Move(overwrite: true)`. Keep last 3 runs.

## Conventions

- net10.0 everywhere; every NuGet package pinned to an exact version.
- New dependencies require explicit approval in the PR description — default is no.
- Tests: xUnit; UI verification via fake-adapter event scripts + Verify snapshots of
  rendered frames (in-memory console). Reducers/diffing/parsers are pure — test them
  without terminals or processes.
- Manual TUI verification happens in tmux: `tmux send-keys` to drive,
  `capture-pane -p` to assert (no captured line may exceed the pane width),
  `resize-window` to exercise fit-to-terminal.
- Sandbox: network allowlist covers nuget.org/github/learn.microsoft.com etc. only —
  do not probe other hosts. Telemetry env vars are already set.
- Keys arrive as `ConsoleKeyInfo`: Shift+E is `KeyChar 'E'` + Shift modifier. Avoid
  Ctrl+letter bindings (flow-control collisions).
- If a brief conflicts with this file or the plan, STOP and flag it in the PR/notes
  rather than guessing. Record every deliberate deviation in the PR description.
