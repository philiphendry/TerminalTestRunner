using System.Threading.Channels;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>
/// The render loop (plan §11.2, CLAUDE.md invariant 2): ≤30 fps, dirty-flag gated, reads an
/// <see cref="AppState"/> snapshot and never blocks event processing. It polls terminal size
/// every frame and injects a <see cref="AppEvent.Resized"/> when it changes; the spinner ticks
/// mark the frame dirty while anything is Running so bursts of results coalesce naturally.
/// </summary>
public sealed class RenderLoop
{
    private const double FrameIntervalMs = 1000.0 / 30.0; // 30 fps cap
    private const double SpinnerIntervalMs = 80.0;
    private const int IdlePollMs = 12; // wake cadence when nothing is dirty (size + keypress pickup)

    public void Run(
        IUiShell shell, ReducerLoop loop, RenderMetrics metrics,
        ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        long lastRevision = -1;
        var lastWidth = 0;
        var lastHeight = 0;
        var lastFrameMs = double.NegativeInfinity;
        var lastTickMs = 0.0;
        var tick = 0;

        while (!ct.IsCancellationRequested)
        {
            var state = loop.Current;
            if (state.ShouldQuit) break;

            var width = shell.Width;
            var height = shell.Height;
            var now = metrics.NowMs;

            var sizeChanged = width != lastWidth || height != lastHeight;
            if (sizeChanged)
                events.TryWrite(new AppEvent.Resized(width, height));

            var tickDue = state.Running && now - lastTickMs >= SpinnerIntervalMs;
            var dirty = state.Revision != lastRevision || sizeChanged || tickDue;

            if (!dirty)
            {
                Thread.Sleep(IdlePollMs);
                continue;
            }

            // Dirty: draw at the frame deadline, otherwise sleep exactly until it (so we land
            // near 30 fps rather than overshooting on a coarse poll).
            var sinceFrame = now - lastFrameMs;
            if (sinceFrame < FrameIntervalMs)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Ceiling(FrameIntervalMs - sinceFrame)));
                continue;
            }

            if (tickDue) { tick++; lastTickMs = now; }
            var info = new RenderInfo(metrics.Fps, metrics.LatencyP95Ms, tick);
            shell.Write(FrameBuilder.Build(state, info, width, height));
            metrics.FrameRendered();
            lastRevision = state.Revision;
            lastWidth = width;
            lastHeight = height;
            lastFrameMs = now;
        }
    }
}
