# `ttr` — Phase 0 POC Agent Prompts

Companion to `ttr-implementation-plan.md` (§13). Each section below is a **complete, self-contained prompt** to hand to a coding agent (Sonnet-class). The prompts assume the agent has **no access to the implementation plan**, so every needed fact is embedded.

**How to use this document**

1. Prepend the **Common Preamble** block to whichever POC prompt you dispatch (it defines environment, working rules, and the deliverable format once, so the individual prompts stay focused).
2. Dispatch one POC per agent session. POC-1..3 and POC-6..9 are independent. POC-4 and POC-5 should be run by *separate* sessions so neither result biases the other.
3. Each POC's deliverable is a `FINDINGS.md` with a PASS/FAIL verdict per acceptance criterion — the plan's decision gates (UI framework, MTP transport) are read directly off those verdicts.

---

## Common Preamble (prepend to every POC prompt)

```text
CONTEXT
You are implementing a throwaway proof-of-concept for "ttr", a planned cross-platform
.NET terminal (TUI) unit-test runner. ttr will discover and run tests via platform APIs
(never by scraping `dotnet test` output), show them in a live navigable tree, and
support watch mode and session resume. Your POC answers one specific feasibility
question for that project. Code quality bar: clear and correct, not production-grade.
Do NOT build features beyond the stated tasks.

ENVIRONMENT
- Linux sandbox, .NET 10 SDK preinstalled, NuGet restore allowed (api.nuget.org).
- Network allowlist: nuget.org, api.nuget.org, aka.ms, learn.microsoft.com,
  github.com (+ raw.githubusercontent.com, codeload.github.com, api.github.com),
  spectreconsole.net, razorconsole.github.io, xunit.net, nunit.org, docs.nunit.org,
  tunit.dev. Nothing else resolves — do not probe for other hosts.
- Available tools: git, tmux, unzip, jq, ripgrep, ps/lsof. TERM=xterm-256color,
  UTF-8 locale, libicu present.
- Child processes and localhost TCP are permitted.
- Telemetry is disabled via env vars already (DOTNET_CLI_TELEMETRY_OPTOUT,
  TESTINGPLATFORM_TELEMETRY_OPTOUT).

WORKING RULES
1. Create a git repo for the POC; commit at each milestone with a message stating
   what was proven or disproven.
2. Pin every NuGet package to an exact version and record it in FINDINGS.md.
3. Prefer reading official docs (learn.microsoft.com) and upstream source on GitHub
   over guessing APIs. When you derive behaviour from source, cite the file path.
4. If an approach fails, record WHY (error text, minimal repro) before trying the
   next — negative results are a deliverable, not a failure.
5. Timebox: if a single blocking issue eats more than ~90 minutes with no progress,
   stop, write it up in FINDINGS.md under "Blockers", and move to remaining tasks.
6. All timing measurements: use Stopwatch/DateTime.UtcNow timestamps written to a
   CSV or log file, then summarise as min/median/p95 in FINDINGS.md. Run timed
   scenarios at least 3 times; report warm and cold separately where relevant.

DELIVERABLE: a repo containing the POC code, fixtures, and FINDINGS.md with sections:
  1. Verdict table — one row per acceptance criterion: PASS / FAIL / PARTIAL + evidence.
  2. Environment — SDK version (`dotnet --version`), OS, pinned package versions.
  3. How to run — exact commands to reproduce every measurement.
  4. Measurements — timing tables (min/median/p95, warm vs cold).
  5. Gotchas — anything surprising a production implementation must know.
  6. Blockers / open questions.
  7. Recommendation — one paragraph: what the ttr production design should do.
```

---

## Prompt — POC-1: VSTest translation layer (streamed discovery, streamed results, subset runs, cancel)

```text
GOAL
Prove that the VSTest "translation layer" API (the same client API IDEs use) gives ttr:
(a) test discovery WITHOUT executing tests, streamed as it happens,
(b) per-test results streamed in near-real-time DURING a run (for live tree updates),
(c) an indication of which tests are CURRENTLY executing (for spinners),
(d) running an arbitrary SUBSET of tests,
(e) cancellation of an in-flight run,
(f) a reliable way to locate the vstest.console host when ttr is installed as a
    dotnet global tool (i.e. no relative path to the SDK can be assumed).

STEP 1 — Fixture test project
Create fixtures/XunitV2Tests: net10.0, packages `xunit` (2.x latest),
`xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` (latest stable, pin versions).
Contents:
- ~200 generated trivial passing tests (write a small T4-free C# source generator
  script or just emit a .cs file from a helper script) spread over 10 classes in
  3 namespaces — enough volume to observe streaming batches.
- 5 deliberately failing tests whose exceptions include file/line info.
- 3 skipped tests.
- 1 [Theory] with 5 inline data rows and 1 [Theory] with MemberData yielding a
  non-serialisable type (to observe how such cases appear at discovery vs run).
- 3 "slow" tests: Task.Delay of 5s, 8s, 10s (used for spinner/cancel tests).
Build it once with `dotnet build`.

STEP 2 — Locate the host (two strategies; implement BOTH)
A console harness project `poc/VsTestPoc` referencing NuGet package
`Microsoft.TestPlatform.TranslationLayer` (pin latest stable).
Strategy A (SDK-resolved): find the active SDK directory (parse `dotnet --list-sdks`
plus DOTNET_ROOT / `which dotnet`) and locate vstest.console.dll under it.
Strategy B (self-shipped): download the `Microsoft.TestPlatform.Portable` nupkg from
nuget.org, unzip it, and use the vstest.console.dll it contains.
Record for each: exact path found, whether it launches, any runtimeconfig issues.

STEP 3 — Harness behaviour
Using VsTestConsoleWrapper (constructor takes the vstest.console path; enable a
diagnostics log file):
1. StartSession, then DiscoverTests(new[]{ fixtureDll }, runSettingsXml, handler)
   with an ITestDiscoveryEventsHandler2 implementation. RunSettings: minimal XML,
   DesignMode true, TargetFrameworkVersion matching the fixture.
   Log a timestamped line per HandleDiscoveredTests batch (count + first FQN).
   Confirm: no test code executes during discovery (the slow tests would make an
   accidental execution obvious — total discovery must be far under 5s).
   Record: total discovery wall time (cold = first call after wrapper start,
   warm = second discovery in same session), number of batches, whether the
   non-serialisable MemberData theory appears as 1 item or 5.
2. RunTests(allDiscoveredTestCases, runSettings, options, handler) with an
   ITestRunEventsHandler2. In HandleTestRunStatsChange log, per newly completed
   TestResult: FQN, outcome, the result's own EndTime, and receive-time — the
   difference is the streaming latency. Also log the contents of
   TestRunChangedEventArgs.ActiveTests each callback (this is the spinner signal).
3. Subset run: RunTests with exactly 3 of the cached TestCase objects (one pass,
   one fail, one slow). Confirm only those 3 execute.
4. Cancel: start a run of just the three slow tests, call CancelTestRun() after
   1s, measure time until the run-complete callback fires and whether testhost
   processes exit (check with ps). Also try AbortTestRun() and note the difference.
5. Session reuse: without disposing the wrapper, repeat discovery+run and compare
   warm timings to cold.

ACCEPTANCE CRITERIA (verdict table rows)
- AC1: Discovery streams in batches and executes no test code.
- AC2: p95 streaming latency (result EndTime -> receive) < 250 ms during the
  200-test run.
- AC3: ActiveTests reliably reflects the currently running slow tests.
- AC4: Subset run executes exactly the requested 3 tests.
- AC5: Cancel completes < 2 s and leaves no orphaned testhost processes.
- AC6: At least one host-location strategy works with NO assumptions about the
  harness's own install location; state which strategy production should use.

GOTCHAS TO WATCH FOR (verify and report, don't assume)
- The wrapper communicates over a local socket; first-session startup cost is
  the dominant latency — measure it separately.
- TestCase objects must be reused from discovery for subset runs; check whether
  their Id values are stable across two discoveries of the same unmodified DLL.
- RunSettings DesignMode and BatchSize can affect callback frequency; if batches
  feel coarse, try setting batch size in runsettings and report the effect.
```

---

## Prompt — POC-2: Microsoft.Testing.Platform (MTP) server mode JSON-RPC client

```text
GOAL
MTP-native test projects (xUnit v3, MSTest.Sdk, TUnit) are self-contained
executables. IDEs drive them via a JSON-RPC "server mode". Prove ttr can act as
that client: streamed discovery, streamed run results, subset runs by test UID,
cancellation, and host-process reuse — against all three frameworks. Also pin the
protocol/version surface so production can guard against drift, and evaluate the
degraded fallback (console mode) in case server mode fails for some framework.

STEP 0 — Learn the protocol from source (do this first, ~30–45 min)
The server-mode protocol is thinly documented. Clone https://github.com/microsoft/testfx
and read, at minimum:
- src/Platform/Microsoft.Testing.Platform/ServerMode/** (JSON-RPC method names,
  message DTOs, handshake/capabilities, cancellation semantics)
- test/**/ServerMode/** integration tests (they are executable documentation of
  a working client)
Also check learn.microsoft.com "Microsoft.Testing.Platform" docs for the --server
and --client-port/--client-host command-line options.
Record in FINDINGS.md: the exact RPC method names for initialize, discovery, run,
the update-notification method, how a run request scopes to specific test UIDs,
how cancellation is expressed, and how the session/process is shut down cleanly.
Cite file paths. Everything in later steps must follow what the source says, not
this prompt's assumptions.

STEP 1 — Fixtures (three projects, net10.0, pin all versions)
- fixtures/XunitV3Tests: package xunit.v3 (+ whatever its docs at xunit.net say a
  bare MTP project needs).
- fixtures/MSTestTests: use the MSTest.Sdk project style (<Project Sdk="MSTest.Sdk/x.y.z">).
- fixtures/TUnitTests: package TUnit per tunit.dev quick start.
Each fixture: ~50 passing tests across a few classes, 3 failing (with file/line
in the exception), 2 skipped, 1 parameterised test, 2 slow tests (5s, 10s).
Confirm each runs standalone first: `dotnet run --project <fixture>` executes tests.

STEP 2 — Client harness `poc/MtpClientPoc`
Console app using the StreamJsonRpc NuGet package (pin latest stable).
- Open a TcpListener on an ephemeral port (port 0, read the assigned port).
- Launch the fixture with server mode flags telling it to connect back to your
  port (per the options confirmed in STEP 0; expected shape:
  `dotnet exec <fixture.dll> --server --client-port <port>` — verify).
- Accept the connection; wire StreamJsonRpc with the framing the server expects
  (expected: LSP-style Content-Length header framing — confirm from source; use
  HeaderDelimitedMessageHandler + the JSON formatter matching the DTO casing).
- Implement: initialize handshake (send client info/capabilities, capture server
  info incl. the platform version it reports), discovery request (collect test
  node UIDs + display names, log timestamped streaming notifications), full run,
  subset run (exactly 3 UIDs), cancellation of a slow-test run (JSON-RPC
  cancellation and/or the protocol's own cancel — try what the source supports),
  clean shutdown.
- Log every inbound/outbound JSON-RPC message to a file (this trace is a key
  deliverable for production work).

STEP 3 — Matrix + measurements
Run the full harness against all three fixtures. For each record:
- discovery wall time; run streaming latency (per-test finished notification
  arrival vs the duration-implied completion moment) min/median/p95;
- whether "test started/in-progress" states are notified (spinner signal) or
  only terminal states;
- subset run correctness; cancel time + orphan check (ps);
- process reuse: after a completed run, can the SAME server process service a
  second discovery+run, or must ttr restart it per request? Measure warm vs cold.
- the Microsoft.Testing.Platform version each framework resolves (from the
  handshake and from `dotnet list package --include-transitive`).

STEP 4 — Fallback evaluation (console mode, timebox 60 min)
Without server mode: run each fixture with `--list-tests` and capture the output
shape; then run a filtered subset using the framework's UID/name filter options.
Assess: is near-real-time result streaming possible at all in this mode (e.g. a
file-based report written incrementally), or is it batch-only? One paragraph per
framework in FINDINGS.md — this defines ttr's degraded mode if server mode breaks.

ACCEPTANCE CRITERIA
- AC1: Server-mode handshake + streamed discovery works for all 3 frameworks.
- AC2: Run results stream with p95 latency < 250 ms; note whether in-progress
  (started) notifications exist per framework.
- AC3: Subset run by UID executes exactly the requested tests (all 3 frameworks).
- AC4: Cancellation < 2 s, no orphaned processes.
- AC5: Documented answer on process reuse across runs, per framework.
- AC6: Full message trace captured; protocol method names + framing + versions
  pinned in FINDINGS.md.
- AC7: Fallback assessment written (STEP 4).

GOTCHAS TO WATCH FOR
- DTO property casing / enum encoding mismatches produce silent deserialisation
  gaps in StreamJsonRpc — if fields arrive null, dump raw JSON and compare.
- The server may send log/telemetry notifications you didn't subscribe to;
  unknown-method handling must be tolerant, not fatal.
- TUnit and xunit.v3 may implement different protocol capability subsets —
  differences ARE the finding; document them per framework.
```

---

## Prompt — POC-3: Per-project test-platform detection heuristics

```text
GOAL
ttr must decide, per test project, whether to drive it via the legacy VSTest API
or the Microsoft.Testing.Platform (MTP) server protocol — automatically, from
MSBuild data alone. Prove a detection function that classifies a realistic
project matrix with 100% accuracy, and find the edge cases the heuristic misses.

STEP 1 — Fixture matrix (all net10.0 unless stated; pin versions; put them all
in one solution fixtures/Detection.sln)
 1. xUnit v2 classic: xunit + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk  -> expect VSTest
 2. xUnit v3: xunit.v3                                                            -> expect MTP
 3. NUnit classic: NUnit + NUnit3TestAdapter + Microsoft.NET.Test.Sdk             -> expect VSTest
 4. NUnit 4 MTP mode: per docs.nunit.org (EnableNUnitRunner=true, OutputType Exe) -> expect MTP
 5. MSTest classic: MSTest.TestFramework + MSTest.TestAdapter + Microsoft.NET.Test.Sdk -> expect VSTest
 6. MSTest.Sdk style: <Project Sdk="MSTest.Sdk/x.y.z">                            -> expect MTP
 7. MSTest with EnableMSTestRunner=true + TestingPlatformDotnetTestSupport=true   -> expect MTP
 8. TUnit                                                                          -> expect MTP
 9. A NON-test class library (no test packages)                                   -> expect NotATestProject
10. Multi-TFM xUnit v2 project (net10.0;net8.0)                                   -> expect VSTest, both TFMs listed
11. Transition project: xUnit v2 packages AND an explicit
    <UseMicrosoftTestingPlatformRunner>true (contrived but occurs in migrations)  -> expect MTP-with-ambiguity-warning
Also drop a global.json at the solution root WITHOUT the "test" section first;
you'll flip it in STEP 3.

STEP 2 — Detector implementation `poc/DetectPoc`
Console app referencing Microsoft.Build + Microsoft.Build.Locator (pin versions).
- Call MSBuildLocator.RegisterDefaults() BEFORE any Microsoft.Build type is
  touched (put evaluation code in a separate method so JIT doesn't load types
  early; this is a classic crash if done wrong — verify and note it).
- For each project, evaluate (Project/ProjectInstance, handling multi-TFM by
  evaluating once per TargetFramework value) and extract:
  properties: IsTestProject, EnableMSTestRunner, EnableNUnitRunner,
  UseMicrosoftTestingPlatformRunner, TestingPlatformDotnetTestSupport,
  OutputType, TargetFramework(s), UsingMicrosoftTestingPlatform (check if such
  a property exists after SDK evaluation);
  items: PackageReference identities (and note that MSTest.Sdk projects may
  express dependencies differently — inspect what evaluation actually shows).
- Classification rules (implement, then refine against reality):
  MTP if any of: UseMicrosoftTestingPlatformRunner/EnableMSTestRunner/
  EnableNUnitRunner true; PackageReference to xunit.v3* or TUnit*; MSTest.Sdk
  project; TestingPlatformDotnetTestSupport true.
  Else VSTest if Microsoft.NET.Test.Sdk present AND any of
  xunit.runner.visualstudio / NUnit3TestAdapter / MSTest.TestAdapter present.
  Else if IsTestProject true -> Unknown-warn. Else NotATestProject.
  Both-signals-present -> MTP + ambiguity warning.
- Output a table: project, TFM, classification, triggering signals.

STEP 3 — Edge probes
a) Add "test": { "runner": "Microsoft.Testing.Platform" } to global.json and
   document what, if anything, changes in evaluated properties (it likely
   changes dotnet-test behaviour, not project evaluation — confirm; conclude
   whether ttr must read global.json itself as an extra signal).
b) Verify each classification empirically: for every fixture classified MTP,
   confirm `dotnet exec <dll> --server --client-port` style invocation is
   accepted (process starts and waits) — or at minimum `dotnet run -- --list-tests`
   works; for every VSTest one, confirm the DLL is NOT an MTP executable.
   Any mismatch between heuristic and reality is a key finding.
c) Evaluation performance: time evaluating all 11 projects (cold and warm) —
   ttr does this at startup and on every csproj change.

ACCEPTANCE CRITERIA
- AC1: 100% correct classification of rows 1–10; row 11 yields MTP + warning.
- AC2: Every classification empirically verified per STEP 3(b).
- AC3: Multi-TFM project reports both TFMs with per-TFM output paths.
- AC4: global.json influence documented with a clear yes/no on whether ttr
  needs to parse it as a detection signal.
- AC5: Evaluation timing reported; note if >2s cold for 11 projects.
- Deliver the final, corrected rule table (signals in priority order) —
  production copies it verbatim.
```

---

## Prompt — POC-4 (DECISION GATE): Spectre.Console interactive virtualised tree at 10k nodes

```text
GOAL
ttr's UI must show a NAVIGABLE tree of up to 10,000 test nodes that updates live
while results stream in, always fits the terminal, truncates every line with an
ellipsis, supports a modal overlay, and stays responsive to keyboard input the
whole time. Spectre.Console has no interactive tree widget (its Tree is
render-only), so prove this can be hand-built on Spectre primitives to strict
performance bars. This POC is one half of a framework decision; build it honestly
— no shortcuts that a real app couldn't use.

STEP 1 — Model + harness `poc/SpectreTreePoc`
Console app, package Spectre.Console (pin latest stable).
- Generate a synthetic tree: 8 projects -> namespaces -> classes -> ~10,000 leaf
  "tests" with realistic-length names (some > 120 chars, some containing CJK
  characters and emoji to force double-width cell handling).
- App state: node statuses (NotRun/Running/Passed/Failed), expansion set,
  selection, scroll offset, a failed-only filter flag, details-pane toggle.
- Architecture requirement: single render loop reading an immutable-ish state
  snapshot; a dedicated input thread doing Console.ReadKey(intercept:true)
  posting key events to a channel; a simulator task posting status-change
  events. NO rendering from event handlers.

STEP 2 — Rendering
- Virtualise: flatten expanded+filtered nodes to a list, render ONLY the rows in
  the viewport [scrollOffset, scrollOffset+visibleRows). Never materialise 10k rows.
- Layout: 1-line header (totals, fps counter, input-latency readout), tree pane,
  a right-side details pane (40% width) showing fake failure text for the
  selected node, 1-line footer with key hints.
- Ellipsis: every rendered line must be truncated to its pane width IN TERMINAL
  CELLS (CJK/emoji = 2 cells) with a trailing '…'. Find and use Spectre's cell
  width measurement (search the Spectre source/docs for its cell/segment width
  APIs) rather than string Length. Prove correctness with the wide-char names.
- Live updates: use Spectre's Live display (or ansi alternate-screen manual
  writes if Live fights you — if so, that's a finding) refreshed by the render
  loop at max 30 fps, only when a dirty flag is set. Running nodes show a
  braille spinner frame advanced per tick.
- Modal: pressing 'o' overlays a centered box (~90% of screen) showing a
  scrollable text file, capturing all input until 'c'. Base layer must not
  bleed through.

STEP 3 — Input + interactions
Keys: up/down move selection (selection stays in viewport, scrolls at edges),
PgUp/PgDn, Home/End, 'e' toggle expand of selected, 'E' (verify Shift+E arrives
as uppercase KeyChar) recursive expand/collapse, 'f' failed-only filter toggle
(tree re-flattens; selection survives or moves to nearest), 'o' modal, 'q' quit.
Instrument input latency: timestamp at key read -> timestamp when the frame
reflecting it completes; show rolling p95 in the header.

STEP 4 — Stress + resize (run inside tmux; script it)
- Storm test: simulator flips 500 statuses/second for 60s over the whole tree
  while you (scripted via tmux send-keys) navigate, toggle 'f', expand/collapse,
  open/close the modal every few seconds. Record fps and input-latency p95.
- Resize test: tmux resize-window through 80x24 -> 200x50 -> 100x30 -> 60x15
  during the storm. After each resize, tmux capture-pane and assert
  programmatically that NO line exceeds the width and the layout re-fit.
- Small-terminal test: 40x10 should show a graceful "too small" placeholder,
  not corrupt output.
- Capture a few capture-pane snapshots as artifacts committed to the repo.

ACCEPTANCE CRITERIA
- AC1: Sustained >= 25 fps during the 500 events/s storm at 10k nodes.
- AC2: Input latency p95 < 50 ms during the storm.
- AC3: Zero lines wider than the terminal in every capture-pane sample,
  including wide-char rows, at every tested size.
- AC4: Shift+E distinguishable from 'e' (report exactly what ConsoleKeyInfo
  contains for both).
- AC5: Modal fully captures input and restores the tree view intact on close.
- AC6: Resize storm produces no flicker/tearing/corruption (attach captures;
  describe any transient artifacts honestly).
- AC7: Failed-filter toggle re-flattens at 10k nodes in < 50 ms (measure).
Report CPU% of the process during the storm as supporting data.
```

---

## Prompt — POC-5 (DECISION GATE): RazorConsole — identical scenario

```text
GOAL
Same feasibility question as a sibling POC built directly on Spectre.Console,
but using RazorConsole (https://github.com/RazorConsole/RazorConsole — a
component framework that renders Razor components to Spectre renderables via a
VDOM, currently ALPHA). ttr needs: a navigable 10k-node virtualised tree with
live updates, strict fit-to-terminal with per-line ellipsis, modal overlay,
scrollable panes, and syntax-highlighted file display. RazorConsole ships
components that CLAIM to cover several of these (ViewHeightScrollable,
SyntaxHighlighter, an ellipsis overflow translator, focus management) — verify
those claims at our scale. Be a fair but ruthless evaluator: the output of this
POC decides whether ttr adopts an alpha dependency.

STEP 0 — Orientation (~45 min)
Read the repo README, design-doc/builtin-components.md and
design-doc/custom-translators.md in the RazorConsole repo, and the tutorial at
razorconsole.github.io. Install the gallery tool (dotnet tool install --global
RazorConsole.Gallery) and run it inside tmux to see the components live.
Record: exact package version pinned (expect an alpha), required SDK
(Microsoft.NET.Sdk.Razor), and any immediate red flags.

STEP 1 — Build the SAME scenario as the sibling POC, adapted to components:
- Synthetic tree: 8 projects -> ~10,000 leaves, long names, CJK/emoji names.
- A Tree component: RazorConsole has no interactive tree — build one as a
  custom component that renders ONLY the viewport slice of the flattened
  expanded+filtered node list (virtualisation is mandatory; if the component
  model forces materialising all 10k children, that is a headline finding).
- Layout: header with fps + input-latency p95, tree pane, right details pane
  (40%), footer hints.
- Ellipsis: use the documented ellipsis overflow mechanism
  (data-overflow="ellipsis" / the overflow translator) — verify it measures
  TERMINAL CELLS (CJK/emoji = 2) not chars; if it doesn't, document and work
  around, and mark AC3 accordingly.
- Live updates: drive 500 status changes/second from a background task into
  component state (StateHasChanged or the framework's equivalent). Spinners on
  running nodes.
- Keys: up/down, PgUp/PgDn, 'e', 'E' (does the framework's input pipeline
  deliver Shift+E distinctly?), 'f' filter, 'o' modal, 'q' quit. Note how much
  the built-in focus system helps or fights a single-focus tree app.
- Modal: overlay using the framework's mechanisms; inside it, use the built-in
  SyntaxHighlighter component to display a real ~300-line .cs file, wrapped in
  ViewHeightScrollable; scroll with arrows; 'c' closes.

STEP 2 — Stress, resize, capture: EXACTLY the same tmux-scripted storm, resize
sequence (80x24 -> 200x50 -> 100x30 -> 60x15), small-terminal (40x10) check,
capture-pane width assertions, and measurements as the sibling POC, so the two
FINDINGS are directly comparable. Additionally record: VDOM/diff overhead —
CPU% and allocation rate (dotnet-counters if available) during the storm.

ACCEPTANCE CRITERIA — identical bars, plus alpha-risk assessment:
- AC1: >= 25 fps sustained during 500 events/s storm at 10k nodes.
- AC2: Input latency p95 < 50 ms during storm.
- AC3: Zero over-width lines in all capture-pane samples (incl. wide chars).
- AC4: Shift+E vs 'e' distinguishable through the framework's input pipeline.
- AC5: Modal + SyntaxHighlighter + ViewHeightScrollable work as documented on a
  300-line file; scrolling is smooth.
- AC6: Resize storm clean (captures attached).
- AC7: Failed-filter re-flatten < 50 ms at 10k nodes.
- AC8 (risk): list every bug, undocumented behaviour, or missing capability
  hit, each with severity (blocker / workaround-exists / cosmetic); state
  whether virtualisation was achievable idiomatically or required fighting the
  framework; final one-paragraph adopt/avoid recommendation given the
  alternative is building on Spectre directly (which RazorConsole itself
  renders to, so a later migration path exists).
```

---

## Prompt — POC-6: Watch pipeline latency (FileSystemWatcher -> ProjectGraph dependents -> incremental build -> re-discovery diff)

```text
GOAL
ttr's watch mode reacts to a source change by rebuilding the changed project
plus its transitive dependents, re-discovering tests in affected test projects,
and diffing the test list. Prove the end-to-end latency is acceptable (< 4 s
warm for a single-file change on a 10-project solution) and nail down
FileSystemWatcher hygiene (debounce, editor rename storms) and the
"external build" quiescence detection variant.

STEP 1 — Fixture solution (generate with a script; commit the script)
10 projects, net10.0: LibA..LibF (class libraries, chained/diamond references:
B->A, C->A, D->B,C, E->D, F->E) plus 2 xUnit v2 test projects
(TestsCore -> refs A,B; TestsApp -> refs D,E,F) each with ~100 generated tests,
plus 2 unrelated libs (LibX, LibY) with a third test project TestsX -> LibX.
This shape lets you verify dependents-set correctness: a change in A must
implicate TestsCore AND TestsApp but NOT TestsX.

STEP 2 — Graph + build service `poc/WatchPoc`
- Microsoft.Build.Locator RegisterDefaults, then load a
  Microsoft.Build.Graph.ProjectGraph over the solution. From it, compute the
  reverse-dependency (dependents) closure for any project. Verify the three
  cases: change A -> {A,B,C,D,E,F,TestsCore,TestsApp}; change LibX -> {LibX,
  TestsX}; change TestsApp source -> {TestsApp} only.
- Build executor: run `dotnet build <project> --nologo -v:quiet -tl:off
  -consoleLoggerParameters:ErrorsOnly;NoSummary` as a child process per affected
  test project (building a test project builds its stale references). Parse any
  canonical-format diagnostics (path(line,col): error CODE: message) with a
  regex; prove the regex on an induced compile error.
- Discovery + diff: reuse the VSTest translation-layer discovery approach
  (VsTestConsoleWrapper.DiscoverTests) on affected test DLLs. Diff old vs new
  test lists keyed on a stable identity you construct
  (FQN + display name); report added/removed/kept counts.

STEP 3 — Watch mode A (source watching)
- FileSystemWatcher per project dir filtered to *.cs, *.csproj (recursive),
  ignoring bin/ obj/. 300 ms trailing-edge debounce coalescing bursts.
- Editor-storm simulation: mimic how real editors save — write to a temp file
  then File.Move over the original; also do a direct in-place write; also touch
  3 files across 2 projects within 200 ms. Assert each burst yields exactly ONE
  pipeline cycle with the correct union dependents-set.
- Measure, over >= 5 warm runs (change a single .cs in LibA, adding one new
  test to nothing — just touch a method body): T0 file saved -> T1 debounce
  fired -> T2 builds done -> T3 discovery done -> T4 diff done. Report each
  segment and total.
- Then a test-list-changing run: add a new [Fact] to TestsCore, confirm the
  diff reports exactly +1 with the right FQN; delete it, confirm -1.

STEP 4 — Watch mode B (external build watching)
- Watch the output assemblies (bin/**/ *.dll for the evaluated TFM) instead of
  sources. Builds write files incrementally, so implement quiescence: after a
  change event, poll size+lastWrite until stable for 500 ms AND the file opens
  with exclusive read (no writer lock) before declaring "new build".
- Drive it by running `dotnet build` yourself in another tmux pane; verify
  exactly one cycle fires per build, including for a no-op incremental build
  (does the assembly get rewritten? finding!) and a multi-project build.

ACCEPTANCE CRITERIA
- AC1: Dependents-set correct for all three STEP 2 cases.
- AC2: Warm single-file-change total latency (T0->T4) < 4 s median; report the
  per-segment breakdown (identify the dominant cost).
- AC3: Every editor-storm pattern coalesces to exactly one cycle.
- AC4: Diff correctness: +1/-1 detected with correct identities.
- AC5: External mode fires exactly once per real build; no-op incremental
  build behaviour documented.
- AC6: Build-error path: induced compile error is caught, parsed by the regex,
  and the cycle stops before discovery (no stale-DLL discovery).
```

---

## Prompt — POC-7: .sln and .slnx parsing via Microsoft.VisualStudio.SolutionPersistence

```text
GOAL
ttr accepts .sln, .slnx and .csproj targets. Prove the official
Microsoft.VisualStudio.SolutionPersistence library reliably enumerates projects
from both formats, including awkward shapes, and behaves sanely on malformed
input. (~half-day POC.)

STEP 1 — Harness `poc/SlnPoc` referencing the
Microsoft.VisualStudio.SolutionPersistence NuGet package (pin version; check its
GitHub repo microsoft/vs-solutionpersistence for API examples — the serializer
entry point is around SolutionSerializers.GetSerializerByMoniker /
.Serializers). Implement: load(path) -> print every project's relative path,
absolute resolved path, project type, and containing solution-folder chain.

STEP 2 — Fixtures (craft by hand and/or `dotnet sln` CLI; commit them)
 a) classic .sln: 5 projects, 2 nested solution folders, one solution folder
    containing only solution items (a .md file), Debug/Release configs with one
    project excluded from Debug build.
 b) the SAME solution as .slnx (create via `dotnet sln migrate` if available in
    the installed SDK — record whether it is — else hand-write per examples in
    the vs-solutionpersistence repo tests).
 c) a .slnx with a project path using forward slashes and one with a relative
    path escaping the solution dir (../Shared/Shared.csproj).
 d) a solution referencing a project file that does NOT exist on disk.
 e) a truncated/corrupt .sln and a .slnx with invalid XML.
 f) an empty solution (zero projects).

STEP 3 — Behaviour checks
- a) vs b): identical project sets? (diff them programmatically)
- c): are paths normalised for Linux correctly?
- d): does load succeed with the phantom entry present (ttr must then check
  File.Exists itself) or throw?
- e): exception type/message quality — can ttr give the user a decent error?
- f): empty enumeration, no crash.
- Round-trip: load b), save to a new path, reload, compare project sets.
- Note whether build-configuration/platform data is accessible (ttr doesn't
  need it for v1, but record where it lives).

ACCEPTANCE CRITERIA
- AC1: Both formats yield identical, correctly-resolved project path sets.
- AC2: Solution folders and nested folders enumerate without confusion with
  buildable projects (solution items don't appear as projects).
- AC3: Malformed inputs produce catchable, describable errors (no hangs, no
  process crash).
- AC4: Phantom-project behaviour documented with the recommended ttr handling.
- AC5: Round-trip preserves the project set.
State the pinned version production should use and any API awkwardness found.
```

---

## Prompt — POC-8: `--continue` state persistence at 10k-test scale

```text
GOAL
ttr saves session state (test results summary + UI state) after every run and
restores it with `--continue`. At 10k tests with realistic failure details the
naive single-JSON design is suspect. Prove the split design: a compact
state.json index plus per-test detail sidecar files, with atomic writes, meets
size and speed targets.

STEP 1 — Data generator `poc/StatePoc`
Generate a realistic 10k-test session in memory:
- 10,000 test summaries: id (a 40–120 char string: adapterKind|projectPath|tfm|
  FQN|paramHash), status (85% Passed, 10% Failed, 5% Skipped), durationMs,
  finishedUtc.
- For each FAILED test (~1,000): a detail object with a 200–400 char message, a
  15–30 frame stack trace (~2 KB) with plausible file paths + line numbers, and
  0–4 KB of captured stdout.
- UI state: ~800 expanded node ids, selected id, five booleans, two scroll ints.

STEP 2 — Persistence implementation
- Schema: state.json = { schema:1, createdUtc, targets:[{path,hash}], ui:{...},
  tests:[{id,status,durationMs,finishedUtc,detailRef?}], runs:{keep:3,latest} }.
  detailRef points into results/<runId>/<sha256(id) first 16 hex>.json.
- Writes: System.Text.Json (source-generated context — measure with and
  without to see if it matters here). ALL writes atomic: write to
  <name>.tmp in the same directory then File.Move(overwrite:true). Details
  written in parallel (bounded).
- Save must be callable while "the app runs": simulate by requiring the save of
  state.json + ~1,000 detail files to happen on background tasks while the main
  thread loops at 30 iterations/sec unimpeded (measure main-loop jitter during
  save).
- Restore: load state.json, verify a target hash mismatch path (tamper with a
  hash -> restore must degrade to "targets changed" result, not crash), lazy
  detail loading (time to load ONE detail on demand), and a full-details load
  (worst case) time.
- Pruning: create 5 fake run directories, prune to keep 3, verify.
- Crash safety: kill -9 the process (script it) mid-save 5 times; every
  surviving state.json must parse (atomicity proof).

STEP 3 — Measurements (3+ runs each, report median/p95)
Save total (background), main-loop jitter during save, restore-index time,
single-detail lazy load, full-detail load, on-disk size of state.json and of
the results/ tree, and gzip'd sizes (is transparent compression worth it? if
System.IO.Compression on each detail file changes size >3x, report the CPU
trade-off).

ACCEPTANCE CRITERIA
- AC1: state.json save < 200 ms and does not disturb the 30/s main loop by
  more than 10 ms p95 jitter.
- AC2: Restore of the index < 500 ms; single lazy detail < 20 ms.
- AC3: Total on-disk footprint < 20 MB for the 10k scenario (report gzip
  option findings).
- AC4: 5/5 kill -9 runs leave a parseable state.json (old or new, never torn).
- AC5: Hash-mismatch restore degrades gracefully with a machine-readable
  reason.
- AC6: Pruning correct.
Recommend: final schema tweaks, whether compression is worth it, and whether
1,000 small files vs one packed details file is the better sidecar shape
(measure both if time allows; a packed single file with an offset index is the
alternative).
```

---

## Prompt — POC-9: Source-file reference parsing across test frameworks

```text
GOAL
ttr's 'o' key opens source files referenced in failure output, so it needs a
parser that extracts ordered (path, line?) references from real error messages
and stack traces produced by xUnit v2, xUnit v3, NUnit (classic), NUnit 4 (MTP),
MSTest, and TUnit — plus MSBuild compiler diagnostics. Prove >= 95% extraction
on real corpus data (not invented samples).

STEP 1 — Corpus collection (the important half of this POC)
Create six tiny fixture test projects (one per framework above, net10.0, pin
versions) each containing tests that fail in varied ways:
  assert-equality failure; exception thrown 3 calls deep (so the trace has
  multiple user frames); async test failure (state-machine frames!); a theory/
  parameterised case failure; a failure whose MESSAGE itself contains a file
  path (e.g. "expected file /tmp/out/data.json to exist"); an assertion inside
  a helper method in a second source file.
Collect the REAL ErrorMessage + ErrorStackTrace strings the platform APIs
surface — not console output. Cheapest route: a small harness using
Microsoft.TestPlatform.TranslationLayer RunTests for the three VSTest-era
frameworks (capture TestResult.ErrorMessage/ErrorStackTrace), and for the MTP
frameworks run the test exe and capture its structured failure output (if a
server-mode client isn't practical for you, `dotnet run -- --report-trx` and
read the TRX XML fields — the TEXT is what matters here, not the transport).
Also collect: 5 real MSBuild error lines by inducing compile errors
(`dotnet build` output, canonical form path(line,col): error CS####: msg).
Dump every sample to corpus/<framework>/<case>.txt and COMMIT the corpus —
it becomes ttr's parser test data.

STEP 2 — Parser
A single static function: IReadOnlyList<FileRef> Parse(string message,
string stackTrace, string projectDir) where FileRef = (absolutePath,
line?, sourceKind). Rules to implement:
- stack-frame form: " in <path>:line <N>" (note localisation risk: the word
  'in'/'line' — check whether the fixtures on this system emit English; record
  the risk either way and prefer a pattern tolerant of the ':line N' suffix).
- MSBuild form: <path>(<line>,<col>): (error|warning) <CODE>:
- bare paths in messages: absolute paths, and relative paths resolved against
  projectDir, restricted to extensions {.cs,.csproj,.fs,.vb,.razor,.json,.xml,
  .txt,.config,.props,.targets} to avoid false positives.
- Order: stack-trace refs in trace order (top frame first), then message refs;
  de-duplicate by (path,line); drop refs whose file does not exist (keep them
  in a separate "unresolved" list for diagnostics).
- Filter noise: frames from framework/system namespaces still carry file paths
  sometimes (e.g. /_/src/... from source-linked PDBs) — such non-existent
  paths land in "unresolved" naturally; verify async state-machine frames
  (MoveNext) still map to the user file.

STEP 3 — Evaluation
- Hand-label the corpus: for each sample, the expected ordered FileRef list
  (a labels.json next to each sample).
- Score the parser: a reference counts as found if path+line match a label.
  Report per-framework recall and overall; every miss gets a one-line cause.
- Package the parser + corpus + tests (xUnit) so production can lift them
  directly.

ACCEPTANCE CRITERIA
- AC1: Corpus contains all six frameworks x all six failure shapes (36 samples
  minimum) of REAL captured text, plus 5 MSBuild diagnostics.
- AC2: Overall recall >= 95% on labelled references; zero false positives that
  point at existing-but-irrelevant files (spot-check top-frame correctness on
  the async cases specifically).
- AC3: Relative-path resolution and existence filtering demonstrated.
- AC4: Localisation risk assessed and documented.
- AC5: Parser ships as a lift-able library with its corpus-driven test suite
  green.
```
