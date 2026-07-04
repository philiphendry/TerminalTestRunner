using System.Text;

namespace Ttr.Ui;

/// <summary>
/// The direct-ANSI terminal shell (CLAUDE.md invariant 4). Alternate screen on entry; a
/// try/finally-safe <see cref="Exit"/> restores the terminal on any exit path so a crash never
/// wrecks the user's terminal. Output is buffered and flushed once per frame.
/// </summary>
public sealed class AnsiConsoleShell : IUiShell
{
    private readonly TextWriter _out;
    private bool _entered;
    private bool _restored;

    public AnsiConsoleShell()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { /* output redirected — nothing to set */ }

        // A wide buffered writer over stdout: build one frame string, write, flush.
        _out = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8, bufferSize: 1 << 16)
        {
            AutoFlush = false,
        };
    }

    public int Width => SafeDim(() => Console.WindowWidth, 80);
    public int Height => SafeDim(() => Console.WindowHeight, 24);

    public void Enter()
    {
        if (_entered) return;
        _entered = true;
        _out.Write(Ansi.AltScreenOn);
        _out.Write(Ansi.HideCursor);
        _out.Write(Ansi.ClearScreen);
        _out.Write(Ansi.CursorHome);
        _out.Flush();
    }

    public void Write(string frame)
    {
        _out.Write(frame);
        _out.Flush();
    }

    public void Exit()
    {
        if (_restored) return;
        _restored = true;
        if (_entered)
        {
            _out.Write(Ansi.Reset);
            _out.Write(Ansi.ShowCursor);
            _out.Write(Ansi.AltScreenOff);
            _out.Flush();
        }
    }

    public void Dispose() => Exit();

    private static int SafeDim(Func<int> read, int fallback)
    {
        try
        {
            var v = read();
            return v > 0 ? v : fallback;
        }
        catch (IOException) { return fallback; }
        catch (ArgumentOutOfRangeException) { return fallback; }
    }
}
