using System.Threading.Channels;
using Ttr.Cli.Watch;
using Ttr.Core;
using Ttr.Core.Persistence;
using Ttr.Runners;
using Ttr.Ui;

namespace Ttr.Cli;

/// <summary>
/// The composition root's runtime wiring (plan §5): ONE channel, a reducer loop (consumer), an
/// input thread (producer), a backend producer (fake adapter OR the real evaluate/build/discover
/// pipeline), an optional watch coordinator, and a render loop — all coordinated by a single
/// <see cref="CancellationTokenSource"/>. The terminal is entered inside a try/finally (and the shell is
/// <c>using</c>) so it is ALWAYS restored, including on an injected crash (AC3).
/// </summary>
public static class App
{
    /// <summary>Fake-adapter session (Phases 1–2 behaviour; unchanged).</summary>
    public static int Run(string scenario, int seed, DiagnosticLog? diag = null)
    {
        var adapter = new FakeAdapter(scenario, seed);
        var target = new TestTarget(scenario);
        var runsEnabled = adapter.Scenario.RunsSupported;
        var initial = AppState.Initial(scenario, runsEnabled);

        return RunLoop(initial, adapter,
            async (writer, _, ct) =>
            {
                await adapter.DiscoverAsync(target, writer, ct).ConfigureAwait(false);
                if (runsEnabled) await adapter.RunAsync(target, [], writer, ct).ConfigureAwait(false);
            }, diag: diag);
    }

    /// <summary>The <c>--fake --watch</c> demo (brief M1): a scripted watch session over a tiny registered
    /// project. Full cycles run on a timer — change → build spinner → re-discovery diff (+1/−1) → auto-rerun
    /// — with one cycle arriving mid-run to show the queued state. Every state here is a real reducer state,
    /// so the demo and the snapshot suite agree.</summary>
    public static int RunFakeWatch(DiagnosticLog? diag = null)
    {
        var adapter = new FakeWatchAdapter();
        var initial = AppState.Initial("watch", runsEnabled: true, rootName: "watch") with { Watch = WatchKind.Build };
        var target = new TestTarget("watch");
        return RunLoop(initial, adapter, (writer, state, ct) => FakeWatchDemo.RunAsync(adapter, target, writer, state, ct), diag: diag);
    }

    /// <summary>The <c>--fake --continue</c> demo (brief M1): a scripted restore over a small registered
    /// project — the tree arrives with restored pass/fail results (a few already Stale) and pre-applied UI
    /// state, then a simulated rebuild flips a subtree to Stale. Every state is a real reducer state, so the
    /// demo and the M1 snapshots agree. No disk is touched (the "restore" is scripted, not loaded).</summary>
    public static int RunFakeContinue(DiagnosticLog? diag = null)
    {
        var initial = AppState.Initial("Sample.slnx", runsEnabled: true, rootName: "Sample.slnx");
        var target = new TestTarget("Sample.slnx");
        return RunLoop(initial, adapter: null,
            (writer, state, ct) => FakeContinueDemo.RunAsync(writer, state, ct), diag: diag);
    }

    /// <summary>Real session: evaluate/build/discover the resolved targets, then run them on demand (Phase 4)
    /// and — when <paramref name="watch"/> is set — react to source (or external-build) changes (Phase 5).
    /// The backend is BOTH the initial discovery producer and the run adapter; <paramref name="store"/> (when
    /// non-null) persists after every run + on exit and serves lazy restored details; <paramref name="afterDiscovery"/>
    /// applies a restore / degrade toast once the tree is populated; <paramref name="watchFactory"/> builds the
    /// watch coordinator once the channel + reducer loop exist.</summary>
    public static int RunReal(
        RealBackend backend, string title, SessionStore? store = null,
        Func<ChannelWriter<AppEvent>, Task>? afterDiscovery = null, WatchKind watch = WatchKind.Off,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory = null,
        DiagnosticLog? diag = null)
    {
        var initial = AppState.Initial(title, runsEnabled: true, rootName: title) with { Watch = watch };
        var target = new TestTarget(title);
        return RunLoop(initial, adapter: backend,
            async (writer, _, ct) =>
            {
                await backend.DiscoverAsync(target, writer, ct).ConfigureAwait(false);
                if (afterDiscovery is not null) await afterDiscovery(writer).ConfigureAwait(false);
            },
            watchFactory, store, diag);
    }

    private static int RunLoop(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, Task> produce,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory = null,
        SessionStore? store = null, DiagnosticLog? diag = null)
        => RunLoopAsync(initial, adapter, produce, watchFactory, store, diag).GetAwaiter().GetResult();

    private static async Task<int> RunLoopAsync(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, Task> produce,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory,
        SessionStore? store, DiagnosticLog? diag = null)
    {
        // Ctrl+C is delivered to the input thread as a key (reducer → exit 130) instead of the
        // runtime killing us before the terminal is restored.
        try { Console.TreatControlCAsInput = true; }
        catch (IOException) { /* no console */ }

        var channel = Channel.CreateUnbounded<AppEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var metrics = new RenderMetrics();
        using var cts = new CancellationTokenSource();

        // The orchestrator launches side effects (reruns, 'o' highlighting, toast expiry) in reaction
        // to reducer-produced state; the reducer loop invokes it after each change (plan invariant 1).
        var orchestrator = new Orchestrator(adapter, channel.Writer, cts.Token, store: store, diag: diag);
        var loop = new ReducerLoop(initial, orchestrator.OnReduced);

        // CLAUDE.md invariant 4: enable Windows VT processing BEFORE any ANSI byte is written; on a legacy
        // conhost where it can't be enabled, fail with a clear message instead of spraying escape codes.
        WindowsTerminal.TryEnableVirtualTerminalProcessing(out var vtError);

        // Capability detection (brief M2): decide full → ASCII → refuse from the VT result + the environment
        // (UTF-8 locale, NO_COLOR, TERM, TTR_ASCII). A dumb / no-VT terminal is refused (exit 2) — a broken TUI
        // is worse than none. NOT exit 3 (that is a build failure preventing any run, plan §3).
        var detection = Capabilities.Detect(vtError);
        if (detection.Refused)
        {
            Console.Error.WriteLine(detection.Refusal);
            diag?.Session($"refused: {detection.Refusal}");
            if (adapter is not null) await adapter.DisposeAsync();
            return 2;
        }
        var caps = detection.Caps;
        diag?.Session($"render caps: unicode={caps.Unicode}, color={caps.Color}");

        // The watch coordinator (Phase 5) reads the latest state (for run-queue coalescing) and writes
        // watch events onto the one channel — built here so it can capture the reducer loop.
        var watch = watchFactory?.Invoke(channel.Writer, () => loop.Current, cts.Token);

        using var shell = new AnsiConsoleShell();
        shell.Enter();
        try
        {
            if (Environment.GetEnvironmentVariable("TTR_CRASH") == "1")
                throw new InvalidOperationException("injected crash (TTR_CRASH=1) — verifying terminal restoration.");

            var reducerTask = Task.Run(() => loop.RunAsync(channel.Reader, cts.Token));

            var inputThread = new Thread(() => InputReader.Run(channel.Writer, metrics, cts.Token))
            {
                IsBackground = true,
                Name = "ttr-input",
            };
            inputThread.Start();

            var producerTask = Task.Run(async () =>
            {
                try { await produce(channel.Writer, () => loop.Current, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new AppEvent.FatalError("Backend failed.", ex.Message));
                }
            });

            watch?.Start(cts.Token);

            var render = new RenderLoop();
            await Task.Run(() => render.Run(shell, loop, metrics, channel.Writer, cts.Token, caps));

            // Shutdown ordering (AC9): cancel first so any in-flight watch cycle / run unwinds through the
            // Phase 4 cancellation paths, THEN dispose the watch coordinator (which stops the watchers and
            // drains its loop) — a watcher event can never fire into a torn-down pipeline.
            await cts.CancelAsync();
            if (watch is not null) await watch.DisposeAsync();
            channel.Writer.TryComplete();
            await SwallowAsync(reducerTask);
            await SwallowAsync(producerTask);
        }
        finally
        {
            shell.Exit();
        }

        if (adapter is not null) await adapter.DisposeAsync();

        var final = loop.Current;
        // Save on clean exit (brief M3): persist the final tree + UI so the next --continue restores it. Best
        // effort — a save failure must never change the exit code. (A mid-run crash keeps the last saved run.)
        if (store is not null)
            try { store.Save(SessionSnapshot.Capture(final)); diag?.Session("saved session state on exit"); }
            catch (Exception ex) { Console.Error.WriteLine($"warning: could not save session: {ex.Message}"); }

        if (final.FatalMessage is { } msg)
            Console.Error.WriteLine(msg);
        diag?.Session($"exit code {final.ExitCode}");
        return final.ExitCode;
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t; }
        catch (OperationCanceledException) { }
        catch { /* best-effort shutdown */ }
    }
}
