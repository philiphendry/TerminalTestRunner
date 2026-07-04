using System.Threading.Channels;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>
/// Reads keystrokes on a dedicated thread (CLAUDE.md invariant 2) and publishes them as
/// <see cref="AppEvent.KeyPressed"/> onto the one channel. Ctrl+C is delivered as a normal key
/// (the caller sets <c>Console.TreatControlCAsInput = true</c>) so the reducer maps it to exit
/// 130 rather than the runtime tearing us down before the terminal is restored.
/// </summary>
public static class InputReader
{
    public static void Run(ChannelWriter<AppEvent> events, RenderMetrics metrics, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // stdin redirected / no console — no interactive input available.
                return;
            }

            metrics.KeyObserved();
            events.TryWrite(new AppEvent.KeyPressed(key));
        }
    }
}
