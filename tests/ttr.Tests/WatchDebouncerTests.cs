using Ttr.Cli.Watch;
using Xunit;

namespace Ttr.Tests;

/// <summary>
/// The debounce/coalesce logic (brief M4 / AC3): the accumulate/flush split lets us prove the three POC-6
/// editor-storm patterns collapse to exactly ONE batch deterministically, without real timers. The 300 ms
/// window against a live <see cref="System.IO.FileSystemWatcher"/> is exercised by the integration tests.
/// </summary>
public class WatchDebouncerTests
{
    private const string ProjA = "/w/A/A.csproj";
    private const string ProjB = "/w/B/B.csproj";

    private static (Debouncer D, List<WatchBatch> Batches) New()
    {
        var batches = new List<WatchBatch>();
        return (new Debouncer(300, batches.Add), batches);
    }

    [Fact]
    public void Temp_write_then_rename_storm_coalesces_to_one_batch()
    {
        var (d, batches) = New();
        // An editor's rename-save: temp create + change + rename over the target — several raw events, one project.
        d.Notify(new WatchChange(ProjA, false));
        d.Notify(new WatchChange(ProjA, false));
        d.Notify(new WatchChange(ProjA, false));
        d.Flush();

        var batch = Assert.Single(batches);
        Assert.Equal(new[] { ProjA }, batch.Projects);
        d.Dispose();
    }

    [Fact]
    public void In_place_multiwrite_save_coalesces_to_one_batch()
    {
        var (d, batches) = New();
        for (var i = 0; i < 5; i++) d.Notify(new WatchChange(ProjA, false));
        d.Flush();
        Assert.Single(batches);
        Assert.Single(batches[0].Projects);
        d.Dispose();
    }

    [Fact]
    public void Burst_across_two_projects_coalesces_to_one_batch_covering_both()
    {
        var (d, batches) = New();
        // 3 files across 2 projects in one burst.
        d.Notify(new WatchChange(ProjA, false));
        d.Notify(new WatchChange(ProjB, false));
        d.Notify(new WatchChange(ProjA, false));
        d.Flush();

        var batch = Assert.Single(batches);
        Assert.Equal(2, batch.Projects.Count);
        Assert.Contains(ProjA, batch.Projects);
        Assert.Contains(ProjB, batch.Projects);
        d.Dispose();
    }

    [Fact]
    public void Project_file_changes_are_carried_separately()
    {
        var (d, batches) = New();
        d.Notify(new WatchChange(ProjA, false));            // a source edit
        d.Notify(new WatchChange(ProjA, true));             // and a .csproj edit
        d.Flush();

        var batch = Assert.Single(batches);
        Assert.Contains(ProjA, batch.Projects);
        Assert.Contains(ProjA, batch.ProjectFileChanges);   // flagged for re-evaluation + graph reload
        d.Dispose();
    }

    [Fact]
    public void Flush_with_nothing_pending_emits_no_batch()
    {
        var (d, batches) = New();
        d.Flush();
        Assert.Empty(batches);
        d.Dispose();
    }

    [Fact]
    public void Separate_flushes_produce_separate_batches()
    {
        var (d, batches) = New();
        d.Notify(new WatchChange(ProjA, false));
        d.Flush();
        d.Notify(new WatchChange(ProjB, false));
        d.Flush();
        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { ProjA }, batches[0].Projects);
        Assert.Equal(new[] { ProjB }, batches[1].Projects);
        d.Dispose();
    }

    [Fact]
    public void Editor_temp_and_build_artifacts_are_filtered_by_relevance()
    {
        Assert.True(SourceWatchSource.IsRelevant("/w/A/Calc.cs"));
        Assert.True(SourceWatchSource.IsRelevant("/w/A/A.csproj"));
        Assert.True(SourceWatchSource.IsRelevant("/w/A/Directory.Build.props"));

        Assert.False(SourceWatchSource.IsRelevant("/w/A/obj/Debug/A.dll"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/bin/Debug/A.dll"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/Calc.cs~"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/Calc.cs.tmp"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/4913"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/.Calc.cs.swp"));
        Assert.False(SourceWatchSource.IsRelevant("/w/A/README.md"));
    }

    [Fact]
    public void Project_file_classification()
    {
        Assert.True(SourceWatchSource.IsProjectFile("/w/A/A.csproj"));
        Assert.True(SourceWatchSource.IsProjectFile("/w/Directory.Build.targets"));
        Assert.False(SourceWatchSource.IsProjectFile("/w/A/Calc.cs"));
    }
}
