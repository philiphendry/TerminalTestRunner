using Ttr.Cli.Watch;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// Real-<see cref="System.IO.FileSystemWatcher"/> coverage for the watch sources (brief M4/M5, AC3/AC4/AC9):
/// the three POC-6 editor-storm patterns each coalesce to exactly one batch (mode A), an assembly rewrite
/// fires once while a no-op fires zero (mode B), and a disposed source never fires again (shutdown ordering).
/// These are timing-based (inotify delivery + debounce), so they poll with generous windows; they create
/// their own temp trees and need no built fixture.
/// </summary>
[Collection("watch-fs")]
public class WatchIntegrationTests
{
    private const int DebounceMs = 200;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ttr-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<int> BatchCountAfter(Func<Task> act, string projectDir, string projectPath)
    {
        var batches = new List<WatchBatch>();
        var gate = new object();
        using var debouncer = new Debouncer(DebounceMs, b => { lock (gate) batches.Add(b); });
        await using var source = new SourceWatchSource([(projectPath, projectDir)]);
        source.Start(c => debouncer.Notify(c));
        await Task.Delay(150);        // let the watcher settle before the storm

        await act();

        // Wait for delivery + debounce to flush, then a quiet margin to catch any stray second batch.
        await Task.Delay(1500);
        lock (gate) return batches.Count;
    }

    [Fact]
    public async Task Rename_save_storm_yields_exactly_one_cycle()
    {
        var dir = NewTempDir();
        var target = Path.Combine(dir, "Calc.cs");
        await File.WriteAllTextAsync(target, "// v0");
        try
        {
            var count = await BatchCountAfter(async () =>
            {
                // Editor rename-save: write a temp file then move it over the target.
                var tmp = Path.Combine(dir, "Calc.cs.tmp");
                await File.WriteAllTextAsync(tmp, "// v1");
                File.Move(tmp, target, overwrite: true);
            }, dir, Path.Combine(dir, "P.csproj"));
            Assert.Equal(1, count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task In_place_multiwrite_save_yields_exactly_one_cycle()
    {
        var dir = NewTempDir();
        var target = Path.Combine(dir, "Calc.cs");
        try
        {
            var count = await BatchCountAfter(async () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    await File.WriteAllTextAsync(target, $"// write {i}");
                    await Task.Delay(20);
                }
            }, dir, Path.Combine(dir, "P.csproj"));
            Assert.Equal(1, count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Burst_across_two_projects_yields_one_cycle_covering_both()
    {
        var root = NewTempDir();
        var dirA = Path.Combine(root, "A");
        var dirB = Path.Combine(root, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var pathA = Path.Combine(dirA, "A.csproj");
        var pathB = Path.Combine(dirB, "B.csproj");

        var batches = new List<WatchBatch>();
        var gate = new object();
        using var debouncer = new Debouncer(DebounceMs, b => { lock (gate) batches.Add(b); });
        await using var source = new SourceWatchSource([(pathA, dirA), (pathB, dirB)]);
        source.Start(c => debouncer.Notify(c));
        await Task.Delay(150);
        try
        {
            // 3 files across 2 projects in one burst.
            await File.WriteAllTextAsync(Path.Combine(dirA, "One.cs"), "// 1");
            await File.WriteAllTextAsync(Path.Combine(dirA, "Two.cs"), "// 2");
            await File.WriteAllTextAsync(Path.Combine(dirB, "Three.cs"), "// 3");
            await Task.Delay(1500);

            WatchBatch batch;
            lock (gate) { batch = Assert.Single(batches); }
            Assert.Contains(pathA, batch.Projects);
            Assert.Contains(pathB, batch.Projects);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Mode_b_fires_once_on_an_assembly_rewrite_and_zero_on_a_noop()
    {
        var dir = NewTempDir();
        var asm = Path.Combine(dir, "Tests.dll");
        await File.WriteAllBytesAsync(asm, [0x4d, 0x5a, 0x00]);   // pretend assembly
        try
        {
            var changes = new List<WatchChange>();
            var gate = new object();
            await using var source = new AssemblyWatchSource([(Path.Combine(dir, "Tests.csproj"), asm)]);
            source.Start(c => { lock (gate) changes.Add(c); });
            await Task.Delay(150);

            // No-op window: nobody rewrites the assembly → zero changes (500 ms quiescence + margin).
            await Task.Delay(900);
            lock (gate) Assert.Empty(changes);

            // A real "build": rewrite the assembly → exactly one change after quiescence.
            await File.WriteAllBytesAsync(asm, [0x4d, 0x5a, 0x00, 0x01, 0x02]);
            await Task.Delay(1500);
            lock (gate) Assert.Single(changes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Disposed_source_never_fires_again()
    {
        var dir = NewTempDir();
        var target = Path.Combine(dir, "Calc.cs");
        await File.WriteAllTextAsync(target, "// v0");
        try
        {
            var changes = new List<WatchChange>();
            var gate = new object();
            var source = new SourceWatchSource([(Path.Combine(dir, "P.csproj"), dir)]);
            source.Start(c => { lock (gate) changes.Add(c); });
            await Task.Delay(150);

            await source.DisposeAsync();      // shutdown FIRST

            // A change AFTER disposal must not reach the callback (AC9: no fire into a torn-down pipeline).
            await File.WriteAllTextAsync(target, "// after dispose");
            await Task.Delay(800);
            lock (gate) Assert.Empty(changes);
        }
        finally { Directory.Delete(dir, true); }
    }
}
