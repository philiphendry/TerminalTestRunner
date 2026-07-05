using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;
using Ttr.Ui;

namespace Ttr.Cli;

/// <summary>
/// The composition root's runtime wiring (plan §5): ONE channel, a reducer loop (consumer), an
/// input thread (producer), a backend producer (fake adapter OR the real evaluate/build/discover
/// pipeline), and a render loop — all coordinated by a single <see cref="CancellationTokenSource"/>.
/// The terminal is entered inside a try/finally (and the shell is <c>using</c>) so it is ALWAYS
/// restored, including on an injected crash (AC3).
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
            async (writer, ct) =>
            {
                await adapter.DiscoverAsync(target, writer, ct).ConfigureAwait(false);
                if (runsEnabled) await adapter.RunAsync(target, [], writer, ct).ConfigureAwait(false);
            });
    }

    /// <summary>Real read-only session: evaluate/build/discover the resolved targets (Phase 3). Runs are
    /// disabled (they arrive in Phase 4). The backend streams the same <see cref="AppEvent"/> shapes the
    /// Fake adapter does, so the whole UI is backend-agnostic.</summary>
    public static int RunReal(RealBackend backend, string title)
    {
        var initial = AppState.Initial(title, runsEnabled: false, rootName: title);
        return RunLoop(initial, adapter: null, backend.RunAsync);
    }

    private static int RunLoop(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, CancellationToken, Task> produce)
        => RunLoopAsync(initial, adapter, produce).GetAwaiter().GetResult();

    private static async Task<int> RunLoopAsync(
        AppState initial, ITestSessionAdapter? adapter,
        Func<ChannelWriter<AppEvent>, CancellationToken, Task> produce)
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
                try { await produce(channel.Writer, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new AppEvent.FatalError("Backend failed.", ex.Message));
                }
            });

            var render = new RenderLoop();
            await Task.Run(() => render.Run(shell, loop, metrics, channel.Writer, cts.Token));

            await cts.CancelAsync();
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
