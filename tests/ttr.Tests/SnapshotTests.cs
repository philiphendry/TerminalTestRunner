using Ttr.Core;
using Ttr.Runners;
using Xunit;
using static Ttr.Tests.TestKit;
using static VerifyXunit.Verifier;

namespace Ttr.Tests;

/// <summary>
/// The UX contract (brief M7, plan §12). Each test drives a deterministic fake scenario + key script
/// through the reducer and Verify-snapshots the rendered (ANSI-stripped, path-scrubbed) frame. A
/// one-character change to layout or content breaks a snapshot — see tests/ttr.Tests/README.md.
///
/// Snapshots use the curated <c>files</c> scenario (14 tests, fixed outcomes/durations) unless a case
/// needs otherwise. Machine paths are scrubbed to {BASE}. RenderInfo is fixed (fps/p95/wall = 0,
/// spinner tick 0) so completed-run frames are deterministic.
/// </summary>
public class SnapshotTests
{
    private static ConsoleKeyInfo Down => Key('\0', ConsoleKey.DownArrow);
    private static ConsoleKeyInfo Tab => Key('\0', ConsoleKey.Tab);
    private static ConsoleKeyInfo End => Key('\0', ConsoleKey.End);

    /// <summary>files after a full run, selection on the failing leaf Add_ReturnsSum.</summary>
    private static AppState AtFailingLeaf() =>
        Press(Play("files", 7), Down, Down, Down, Down, Down);

    /// <summary>A single failed leaf with a crafted, machine-independent detail (synthetic paths) so
    /// wrap on/off snapshots are deterministic — the real 'files' stack traces embed absolute paths.</summary>
    private static AppState CraftedFailure()
    {
        var id = Id("Contoso.Sample.Parsing", "TokenizerTests", "Parses_Nested_Arrays");
        var s = Discover(AppState.Initial("crafted"), id);
        var detail = new TestResultDetail(
            Message: "Assert.Equal() Failure: the parsed token stream did not match the long expected " +
                     "sequence produced by the reference tokenizer implementation",
            ExceptionChain: ["Xunit.Sdk.EqualException"],
            StackTrace:
                "   at Contoso.Sample.Parsing.Tokenizer.Next() in /work/src/Parsing/Tokenizer.cs:line 128\n" +
                "   at Contoso.Sample.Parsing.TokenizerTests.Parses_Nested_Arrays() in /work/src/Parsing/TokenizerTests.cs:line 42",
            StandardOutput: "[info] tokenizing 4096 bytes of deeply nested input that runs on long enough to force wrapping");
        s = Feed(s, new AppEvent.TestStarted(id),
            new AppEvent.TestFinished(id, TestOutcome.Failed, TimeSpan.FromMilliseconds(33), detail));
        return Press(s, Down, Down, Down, Down, Down); // select the leaf
    }

    [Fact]
    public Task Default_layout_times_off() =>
        Verify(Render(Play("files", 7), 100, 30));

    [Fact]
    public Task Times_on() =>
        Verify(Render(Press(Play("files", 7), Char('t')), 100, 30));

    [Fact]
    public Task Detail_right() =>
        Verify(Render(Press(AtFailingLeaf(), Char('s')), 100, 30));

    [Fact]
    public Task Detail_beneath() =>
        Verify(Render(Press(AtFailingLeaf(), Char('s'), Char('b')), 100, 30));

    [Fact]
    public Task Detail_wrap_off() =>
        Verify(Render(Press(CraftedFailure(), Char('s')), 70, 30)); // narrow → long lines truncate with …

    [Fact]
    public Task Detail_wrap_on() =>
        Verify(Render(Press(CraftedFailure(), Char('s'), Char('w')), 70, 30));

    [Fact]
    public Task Filter_before_vanish()
    {
        var s = Press(Play("files", 7), Char('f'));
        return Verify(Render(s, 100, 30));
    }

    [Fact]
    public Task Filter_after_vanish()
    {
        var s = Press(Play("files", 7), Char('f'));
        // A currently-failed leaf passes on rerun → it (and any emptied branch) vanishes on next flatten.
        var plan = ScenarioBuilder.Build("files", 7).Plans
            .First(p => p.Identity.Method == "Add_ReturnsSum" && !p.Identity.IsCase);
        s = Reducer.Reduce(s, new AppEvent.TestFinished(plan.Identity, TestOutcome.Passed, TimeSpan.FromMilliseconds(9)));
        return Verify(Render(s, 100, 30));
    }

    [Fact]
    public Task Modal_open() =>
        Verify(Render(Press(AtFailingLeaf(), Char('o')), 100, 30));

    [Fact]
    public Task Modal_next_ref() =>
        Verify(Render(Press(AtFailingLeaf(), Char('o'), Char('n')), 100, 30));

    [Fact]
    public Task Modal_scrolled() =>
        Verify(Render(Press(AtFailingLeaf(), Char('o'), End), 100, 30));

    [Fact]
    public Task Modal_closed_restores_tree() =>
        Verify(Render(Press(AtFailingLeaf(), Char('o'), Char('c')), 100, 30));

    [Fact]
    public Task Help_overlay() =>
        Verify(Render(Press(Play("files", 7), Char('?')), 100, 30));

    [Fact]
    public Task Too_small_placeholder() =>
        Verify(Render(Play("files", 7), 40, 10));

    [Fact]
    public Task Resize_mid_run()
    {
        // A mid-run state: discovery done, a few tests started but not finished (spinners), then resized.
        var built = ScenarioBuilder.Build("files", 7);
        var s = AppState.Initial("files");
        foreach (var plan in built.Plans.Where(p => p.DiscoverUpfront))
            s = Reducer.Reduce(s, new AppEvent.TestsDiscovered([plan.Identity]));
        foreach (var plan in built.Plans.Take(3))
            s = Reducer.Reduce(s, new AppEvent.TestStarted(plan.Identity));
        return Verify(Render(s, 120, 20));
    }
}
