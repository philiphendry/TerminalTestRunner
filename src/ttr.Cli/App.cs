using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;
using Ttr.Ui;

namespace Ttr.Cli;

/// <summary>
/// The composition root's runtime wiring (plan §5): ONE channel, a reducer loop (consumer), an
/// input thread (producer), the fake adapter (producer), and a render loop — all coordinated by a
/// single <see cref="CancellationTokenSource"/>. The terminal is entered inside a try/finally (and
/// the shell is <c>using</c>) so it is ALWAYS restored, including on an injected crash (AC3).
/// </summary>
public static class App
{
    public static int Run(string scenario, int seed)
        => RunAsync(scenario, seed).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string scenario, int seed)
    {
        // Ctrl+C is delivered to the input thread as a key (reducer → exit 130) instead of the
        // runtime killing us before the terminal is restored.
        try { Console.TreatControlCAsInput = true; }
        catch (IOException) { /* no console */ }

        var channel = Channel.CreateUnbounded<AppEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var loop = new ReducerLoop(AppState.Initial(scenario));
        var metrics = new RenderMetrics();
        var adapter = new FakeAdapter(scenario, seed);
        var target = new TestTarget(scenario);

        using var cts = new CancellationTokenSource();
        using var shell = new AnsiConsoleShell();
        shell.Enter();
        try
        {
            // AC3: prove terminal restoration on a forced crash. Throws *after* entering the alt
            // screen; the finally + `using` below restore the terminal before it propagates.
            if (Environment.GetEnvironmentVariable("TTR_CRASH") == "1")
                throw new InvalidOperationException("injected crash (TTR_CRASH=1) — verifying terminal restoration.");

            var reducerTask = Task.Run(() => loop.RunAsync(channel.Reader, cts.Token));

            var inputThread = new Thread(() => InputReader.Run(channel.Writer, metrics, cts.Token))
            {
                IsBackground = true,
                Name = "ttr-input",
            };
            inputThread.Start();

            var adapterTask = Task.Run(async () =>
            {
                try
                {
                    await adapter.DiscoverAsync(target, channel.Writer, cts.Token);
                    await adapter.RunAsync(target, [], channel.Writer, cts.Token);
                }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new AppEvent.FatalError("Fake adapter failed.", ex.Message));
                }
            });

            // Render loop runs until a reducer sets ShouldQuit (q / Ctrl+C / fatal).
            var render = new RenderLoop();
            await Task.Run(() => render.Run(shell, loop, metrics, channel.Writer, cts.Token));

            // Tear down producers/consumer.
            await cts.CancelAsync();
            channel.Writer.TryComplete();
            await SwallowAsync(reducerTask);
            await SwallowAsync(adapterTask);
        }
        finally
        {
            shell.Exit();
        }

        await adapter.DisposeAsync();

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
