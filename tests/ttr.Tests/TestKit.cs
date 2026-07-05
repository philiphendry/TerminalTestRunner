using System.Text.RegularExpressions;
using Ttr.Core;
using Ttr.Runners;
using Ttr.Ui;

namespace Ttr.Tests;

/// <summary>Shared helpers for building events/ids and inspecting rendered frames.</summary>
internal static partial class TestKit
{
    public const string Project = "P";
    public const string Tfm = "net10.0";

    public static TestIdentity Id(string ns, string cls, string method, string? theoryRow = null)
    {
        var id = TestCaseId.ForCase("fake", Project, Tfm, $"{ns}.{cls}.{method}", theoryRow);
        return new TestIdentity(id, Project, Tfm, ns, cls, method, theoryRow);
    }

    public static TestCaseId BranchId(TestNodeKind kind, params string[] segments)
        => TestCaseId.ForBranch(kind, segments);

    public static AppState Feed(AppState s, params AppEvent[] events)
    {
        foreach (var e in events) s = Reducer.Reduce(s, e);
        return s;
    }

    public static AppState Discover(AppState s, params TestIdentity[] ids)
        => Reducer.Reduce(s, new AppEvent.TestsDiscovered(ids));

    public static ConsoleKeyInfo Key(char ch, ConsoleKey key, bool shift = false, bool control = false)
        => new(ch, key, shift, alt: false, control);

    public static ConsoleKeyInfo Char(char ch)
        => new(ch, (ConsoleKey)char.ToUpperInvariant(ch), shift: char.IsUpper(ch), alt: false, control: false);

    [GeneratedRegex("\x1b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex("(?=\x1b\\[\\d+;1H)")]
    private static partial Regex RowSplit();

    /// <summary>The visible (ANSI-stripped) text of each row a frame writes.</summary>
    public static IEnumerable<string> VisibleRows(string frame)
    {
        foreach (var part in RowSplit().Split(frame))
        {
            if (string.IsNullOrEmpty(part)) continue;
            yield return AnsiEscape().Replace(part, string.Empty);
        }
    }

    // --- Scenario playback + rendering (snapshot suite, brief M7) ---------------

    /// <summary>Deterministically drive a fake scenario through the reducer: discovery, then every
    /// test Started→Finished (with failure detail), then RunCompleted. No timing, no threads.</summary>
    public static AppState Play(string scenario, int seed)
    {
        var built = ScenarioBuilder.Build(scenario, seed);
        var s = AppState.Initial(scenario);
        foreach (var plan in built.Plans.Where(p => p.DiscoverUpfront))
            s = Reducer.Reduce(s, new AppEvent.TestsDiscovered([plan.Identity]));
        foreach (var plan in built.Plans)
        {
            s = Reducer.Reduce(s, new AppEvent.TestStarted(plan.Identity));
            s = Reducer.Reduce(s, new AppEvent.TestFinished(plan.Identity, plan.Outcome, plan.Duration, plan.Detail));
        }
        return Reducer.Reduce(s, new AppEvent.RunCompleted());
    }

    /// <summary>Feed a sequence of keystrokes.</summary>
    public static AppState Press(AppState s, params ConsoleKeyInfo[] keys)
    {
        foreach (var k in keys) s = Reducer.Reduce(s, new AppEvent.KeyPressed(k));
        return s;
    }

    /// <summary>Deterministically drive the Phase 3 <c>backend</c> scenario: prelude (registration +
    /// build lifecycle + phantom/unparseable notices), then discover all upfront tests, then the
    /// postlude smoke-validation notices. Runs are disabled, so tests remain NotRun (read-only).</summary>
    public static AppState PlayBackend()
    {
        var built = ScenarioBuilder.Build("backend", 0);
        var s = AppState.Initial("backend", built.RunsSupported);
        foreach (var e in built.Prelude ?? []) s = Reducer.Reduce(s, e);
        foreach (var plan in built.Plans.Where(p => p.DiscoverUpfront))
            s = Reducer.Reduce(s, new AppEvent.TestsDiscovered([plan.Identity]));
        foreach (var e in built.Postlude ?? []) s = Reducer.Reduce(s, e);
        return s;
    }

    /// <summary>Row index of a node by id in the current flattened rows (-1 if not visible).</summary>
    public static int RowIndexOf(AppState s, TestCaseId id)
    {
        for (var i = 0; i < s.Rows.Count; i++)
            if (s.Rows[i].Node.Id.Equals(id)) return i;
        return -1;
    }

    /// <summary>Move the selection onto a node by id, deterministically (Home then Down), so scroll clamps
    /// the same way it would for a user navigating there.</summary>
    public static AppState SelectRow(AppState s, TestCaseId id)
    {
        var target = RowIndexOf(s, id);
        if (target < 0) return s;
        s = Press(s, Key('\0', ConsoleKey.Home));
        for (var i = 0; i < target; i++) s = Press(s, Key('\0', ConsoleKey.DownArrow));
        return s;
    }

    public static TestCaseId ProjectId(string projectPath) => TestCaseId.ForBranch(TestNodeKind.Project, projectPath);

    /// <summary>The <c>--fake --watch</c> base state (brief M1): the registered single-TFM project with its
    /// initial three tests discovered, watch mode on. The snapshot suite drives the watch states from here.</summary>
    public static AppState WatchBase()
    {
        var s = AppState.Initial("watch", runsEnabled: true, rootName: "watch") with { Watch = WatchKind.Build };
        s = Reducer.Reduce(s, FakeWatchScript.ProjectRegistered);
        return Reducer.Reduce(s, new AppEvent.TestsDiscovered(FakeWatchScript.Initial));
    }

    /// <summary>The watch base after an initial run (Subtract fails, the rest pass) — the state the
    /// re-discovery diff snapshots start from, so kept results are visible.</summary>
    public static AppState WatchRan()
    {
        var s = WatchBase();
        foreach (var id in FakeWatchScript.Initial)
        {
            s = Reducer.Reduce(s, new AppEvent.TestStarted(id));
            var (outcome, detail) = FakeWatchScript.Outcome(id);
            s = Reducer.Reduce(s, new AppEvent.TestFinished(id, outcome, TimeSpan.FromMilliseconds(2), detail));
        }
        return Reducer.Reduce(s, new AppEvent.RunCompleted());
    }

    // --- Sessions / --continue (Phase 6, brief M1) ------------------------------

    /// <summary>The <c>--fake --continue</c> base: the registered project with its tests discovered, before the
    /// restore attaches results (the snapshot suite drives the restore states from here).</summary>
    public static AppState ContinueBase()
    {
        var s = AppState.Initial("Sample.slnx", runsEnabled: true, rootName: "Sample.slnx");
        s = Reducer.Reduce(s, FakeContinueScript.ProjectRegistered);
        return Reducer.Reduce(s, new AppEvent.TestsDiscovered(FakeContinueScript.Initial));
    }

    /// <summary>The base after a scripted restore: mixed fresh/Stale results, pre-applied UI, and the failure's
    /// detail lazily loaded (so the detail pane has content) — the state AC1/AC2's fake demo shows.</summary>
    public static AppState ContinueRestored()
    {
        var s = ContinueBase();
        s = Reducer.Reduce(s, new AppEvent.SessionRestored(
            FakeContinueScript.RestoredResults, FakeContinueScript.RestoredUiState, FakeContinueScript.RelativeTime));
        return Reducer.Reduce(s, new AppEvent.DetailLoaded(FakeContinueScript.Subtract.Id, FakeContinueScript.SubtractDetail));
    }

    /// <summary>Move the selection onto the first visible row whose node name equals <paramref name="name"/>.</summary>
    public static AppState SelectByName(AppState s, string name)
    {
        for (var i = 0; i < s.Rows.Count; i++)
            if (s.Rows[i].Node.Name == name) return SelectRow(s, s.Rows[i].Node.Id);
        return s;
    }

    /// <summary>Render a frame through the in-memory shell at (w,h); returns the ANSI-stripped visible
    /// grid with machine paths scrubbed, so snapshots are deterministic across machines/OS.</summary>
    public static string Render(AppState s, int w, int h)
    {
        s = Reducer.Reduce(s, new AppEvent.Resized(w, h)); // keep reducer viewport in sync with draw size
        var shell = new StringUiShell(w, h);
        shell.Write(FrameBuilder.Build(s, new RenderInfo(0, 0, 0, 0), w, h));
        var text = string.Join("\n", VisibleRows(shell.LastFrame));
        return Scrub(text);
    }

    /// <summary>Render a frame in the ASCII fallback tier (brief M2): non-Unicode glyphs, colour on (the
    /// colour axis is invisible to the ANSI-stripped snapshot). The verified baseline is the Unicode frame
    /// with ttr's glyph vocabulary transliterated to ASCII — no braille/box/arrows survive.</summary>
    public static string RenderAscii(AppState s, int w, int h)
    {
        s = Reducer.Reduce(s, new AppEvent.Resized(w, h));
        var shell = new StringUiShell(w, h);
        shell.Write(FrameBuilder.Build(s, new RenderInfo(0, 0, 0, 0), w, h, new Caps(Unicode: false, Color: true)));
        var text = string.Join("\n", VisibleRows(shell.LastFrame));
        return Scrub(text);
    }

    private static string Scrub(string text)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        return text.Replace(baseDir, "{BASE}", StringComparison.Ordinal);
    }
}
