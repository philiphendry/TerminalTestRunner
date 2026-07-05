using System.Threading.Channels;
using Ttr.Cli.Watch;
using Ttr.Core;
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
    public static int Run(string scenario, int seed)
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
            });
    }

    /// <summary>The <c>--fake --watch</c> demo (brief M1): a scripted watch session over a tiny registered
    /// project. Full cycles run on a timer — change → build spinner → re-discovery diff (+1/−1) → auto-rerun
    /// — with one cycle arriving mid-run to show the queued state. Every state here is a real reducer state,
    /// so the demo and the snapshot suite agree.</summary>
    public static int RunFakeWatch()
    {
        var adapter = new FakeWatchAdapter();
        var initial = AppState.Initial("watch", runsEnabled: true, rootName: "watch") with { Watch = WatchKind.Build };
        var target = new TestTarget("watch");
        return RunLoop(initial, adapter, (writer, state, ct) => FakeWatchDemo.RunAsync(adapter, target, writer, state, ct));
    }

    /// <summary>Real session: evaluate/build/discover the resolved targets, then run them on demand (Phase 4)
    /// and — when <paramref name="watch"/> is set — react to source (or external-build) changes (Phase 5).
    /// The backend is BOTH the initial discovery producer and the run adapter; <paramref name="watchFactory"/>
    /// (when non-null) builds the watch coordinator once the channel + reducer loop exist.</summary>
    public static int RunReal(
        RealBackend backend, string title, WatchKind watch = WatchKind.Off,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory = null)
    {
        var initial = AppState.Initial(title, runsEnabled: true, rootName: title) with { Watch = watch };
        var target = new TestTarget(title);
        return RunLoop(initial, adapter: backend,
            (writer, _, ct) => backend.DiscoverAsync(target, writer, ct), watchFactory);
    }

    private static int RunLoop(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, Task> produce,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory = null)
        => RunLoopAsync(initial, adapter, produce, watchFactory).GetAwaiter().GetResult();

    private static async Task<int> RunLoopAsync(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, Task> produce,
        Func<ChannelWriter<AppEvent>, Func<AppState>, CancellationToken, WatchCoordinator>? watchFactory)
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
        var orchestrator = new Orchestrator(adapter, channel.Writer, cts.Token);
        var loop = new ReducerLoop(initial, orchestrator.OnReduced);

        // CLAUDE.md invariant 4: enable Windows VT processing BEFORE any ANSI byte is written; on a legacy
        // conhost where it can't be enabled, fail with a clear message instead of spraying escape codes.
        if (!WindowsTerminal.TryEnableVirtualTerminalProcessing(out var vtError))
        {
            Console.Error.WriteLine(vtError);
            if (adapter is not null) await adapter.DisposeAsync();
            return 3;
        }

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
            await Task.Run(() => render.Run(shell, loop, metrics, channel.Writer, cts.Token));

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
        if (final.FatalMessage is { } msg)
            Console.Error.WriteLine(msg);
        return final.ExitCode;
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t; }
        catch (OperationCanceledException) { }
        catch { /* best-effort shutdown */ }
    }
}
