using System.Runtime.InteropServices;

namespace Ttr.Ui;

/// <summary>
/// Windows virtual-terminal enablement (CLAUDE.md invariant 4, POC-4 deferral). Direct-ANSI rendering
/// needs the console in VT mode; on Windows this must be turned on with <c>SetConsoleMode</c> +
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> BEFORE any ANSI byte is written. On modern consoles
/// (Windows Terminal, conhost on Win10 1809+) this succeeds; on legacy conhost it cannot be enabled and
/// we surface a clear message rather than spraying raw escape codes. A no-op on non-Windows.
/// </summary>
public static partial class WindowsTerminal
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    /// <summary>
    /// Enable VT processing on the console's stdout. Returns true on non-Windows (nothing to do) or when VT
    /// is on/enabled; returns false with a human-readable <paramref name="error"/> on legacy consoles where
    /// it can't be enabled (or when output isn't a real console). Never throws.
    /// </summary>
    public static bool TryEnableVirtualTerminalProcessing(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) return true;   // Linux/macOS terminals are VT-native

        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == 0 || handle == -1)
            {
                error = "No console output handle — ttr needs an interactive terminal.";
                return false;
            }
            if (!GetConsoleMode(handle, out var mode))
            {
                // Output redirected or a legacy console with no mode to query.
                error = "This output is not an interactive console (or a legacy console). ttr needs a VT-capable terminal.";
                return false;
            }
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;   // already enabled
            if (!SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing))
            {
                error = "This terminal does not support ANSI/VT sequences (legacy conhost). "
                    + "Run ttr in Windows Terminal, or enable virtual-terminal processing for your console.";
                return false;
            }
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "Could not load kernel32.dll to enable virtual-terminal processing.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "Console-mode APIs are unavailable on this Windows build.";
            return false;
        }
    }
}
