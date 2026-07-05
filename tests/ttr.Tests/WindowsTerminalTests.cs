using Ttr.Ui;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// Exercises the Windows virtual-terminal enable path (CLAUDE.md invariant 4, brief M5). The call path
/// runs on every OS; on Windows CI it drives the real <c>SetConsoleMode</c> P/Invoke, and on a
/// legacy/redirected console it must return a clear message rather than throwing. On non-Windows it is a
/// no-op that always succeeds.
/// </summary>
public class WindowsTerminalTests
{
    [Fact]
    public void Vt_enablement_call_path_runs_without_throwing()
    {
        var ok = WindowsTerminal.TryEnableVirtualTerminalProcessing(out var error);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(ok);        // VT-native platform → no-op success
            Assert.Null(error);
        }
        else
        {
            // The P/Invoke path executed and returned a definite result. When it fails (legacy conhost or
            // redirected output under CI), it yields a non-empty, actionable message and never throws.
            if (!ok) Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }
}
