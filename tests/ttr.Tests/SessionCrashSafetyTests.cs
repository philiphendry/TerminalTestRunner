using System.Diagnostics;
using System.Text.Json;
using Ttr.Core.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Ttr.Tests;

/// <summary>
/// POC-8's kill -9 harness, ported (brief M3/M6): spawn the real <c>ttr</c> process in its env-gated
/// loop-save mode, SIGKILL it mid-write, and assert state.json is always parseable (the atomic temp-file +
/// rename guarantee — never a torn file). Five interrupted saves, 5/5 must survive.
/// </summary>
[Collection("integration")]   // serialised with the other timing-sensitive/heavy suites (see WatchIntegrationTests)
public sealed class SessionCrashSafetyTests
{
    private readonly ITestOutputHelper _out;
    public SessionCrashSafetyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Five_kill_dash_9_saves_all_leave_a_parseable_state()
    {
        var ttrDll = Path.Combine(AppContext.BaseDirectory, "ttr.dll");
        Assert.True(File.Exists(ttrDll), $"ttr.dll not found next to the test binary: {ttrDll}");

        var survived = 0;
        var sawState = 0;
        var rng = new Random(20260705);
        for (var iter = 0; iter < 5; iter++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ttr-kill9-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            try
            {
                RunAndKill(ttrDll, dir, rng.Next(15, 120));

                var statePath = Path.Combine(dir, "state.json");
                if (!File.Exists(statePath)) { survived++; continue; }   // killed before the first rename — valid
                sawState++;
                try
                {
                    var doc = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(statePath));
                    Assert.NotNull(doc);
                    Assert.Equal(SessionDocument.CurrentSchema, doc!.Schema);
                    survived++;
                }
                catch (JsonException ex)
                {
                    _out.WriteLine($"iteration {iter}: TORN state.json — {ex.Message}");
                }
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
        }

        _out.WriteLine($"kill -9 survived {survived}/5 (state.json present in {sawState})");
        Assert.Equal(5, survived);
    }

    private static void RunAndKill(string ttrDll, string dir, int killAfterMs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(ttrDll);
        psi.Environment["TTR_CRASH_SAVE_DIR"] = dir;
        psi.Environment["TTR_CRASH_SAVE_COUNT"] = "3000";   // enough bytes that a kill lands mid-write

        using var proc = Process.Start(psi)!;
        // Wait for the harness to signal it has started looping, then let a few saves happen.
        var ready = proc.StandardOutput.ReadLine();
        Thread.Sleep(killAfterMs);
        proc.Kill(entireProcessTree: true);   // SIGKILL on Linux
        proc.WaitForExit(5000);
    }
}
