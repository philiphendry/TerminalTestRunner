using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ttr.Ui;

/// <summary>
/// Live instrumentation surfaced in the header (brief M3): frames-per-second and input-latency
/// p95 (key-read → next-frame). Kept quantitative on purpose so every future review can read
/// the numbers straight off the screen. Thread-safe: the input thread only enqueues timestamps;
/// the render thread does everything else.
/// </summary>
public sealed class RenderMetrics
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // fps (render thread only)
    private int _framesSinceWindow;
    private double _windowStartMs;
    private double _fps;

    // input latency
    private readonly ConcurrentQueue<double> _keyStamps = new();
    private readonly double[] _ring = new double[256];
    private int _ringCount;
    private int _ringPos;

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Called by the input thread the instant a key is read.</summary>
    public void KeyObserved() => _keyStamps.Enqueue(NowMs);

    /// <summary>Called by the render thread once per frame; updates fps and drains key latencies.</summary>
    public void FrameRendered()
    {
        _framesSinceWindow++;
        var now = NowMs;
        var dt = now - _windowStartMs;
        if (dt >= 500)
        {
            _fps = _framesSinceWindow * 1000.0 / dt;
            _framesSinceWindow = 0;
            _windowStartMs = now;
        }

        while (_keyStamps.TryDequeue(out var stamp))
        {
            _ring[_ringPos] = now - stamp;
            _ringPos = (_ringPos + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
        }
    }

    public double Fps => _fps;

    public double LatencyP95Ms
    {
        get
        {
            if (_ringCount == 0) return 0;
            var copy = new double[_ringCount];
            Array.Copy(_ring, copy, _ringCount);
            Array.Sort(copy);
            var idx = (int)Math.Ceiling(0.95 * _ringCount) - 1;
            return copy[Math.Clamp(idx, 0, _ringCount - 1)];
        }
    }
}
