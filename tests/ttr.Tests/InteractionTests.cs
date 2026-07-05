using Ttr.Core;
using Ttr.Ui;
using Xunit;
using static Ttr.Tests.TestKit;

namespace Ttr.Tests;

/// <summary>
/// Non-snapshot interaction tests (brief M7): focus cycling, modal input capture, queued-rerun
/// coalescing, and filter selection-survival — behaviours a frame snapshot can't fully pin down.
/// </summary>
public class InteractionTests
{
    private static ConsoleKeyInfo Down => Key('\0', ConsoleKey.DownArrow);
    private static ConsoleKeyInfo TabKey => Key('\0', ConsoleKey.Tab);

    [Fact]
    public void Tab_cycles_focus_only_when_detail_visible()
    {
        var s = Play("files", 7);
        Assert.Equal(PaneFocus.Tree, s.Focus);

        s = Press(s, TabKey);                 // no detail pane → Tab is a no-op
        Assert.Equal(PaneFocus.Tree, s.Focus);

        s = Press(s, Char('s'));              // open detail
        s = Press(s, TabKey);
        Assert.Equal(PaneFocus.Detail, s.Focus);
        s = Press(s, TabKey);
        Assert.Equal(PaneFocus.Tree, s.Focus);
    }

    [Fact]
    public void Detail_focus_scrolls_detail_not_tree()
    {
        // Beneath pane is short, so the crafted long detail has something to scroll.
        var s = Press(Play("files", 7), Down, Down, Down, Down, Down, Char('s'), Char('b'));
        s = Reducer.Reduce(s, new AppEvent.Resized(100, 24));
        var treeSel = s.Selection;
        var beforeScroll = s.DetailScroll;

        s = Press(s, TabKey, Down, Down);
        Assert.Equal(PaneFocus.Detail, s.Focus);
        Assert.Equal(treeSel, s.Selection);          // tree selection unchanged
        Assert.True(s.DetailScroll > beforeScroll);  // detail scrolled instead
    }

    [Fact]
    public void Modal_captures_all_input()
    {
        var s = Press(Play("files", 7), Down, Down, Down, Down, Down, Char('o'));
        Assert.NotNull(s.Modal);
        var failedOnlyBefore = s.FailedOnly;

        // Keys that normally act on the tree must not leak through while the modal is open.
        s = Press(s, Char('f'), Char('s'), Char('t'), Down);
        Assert.NotNull(s.Modal);
        Assert.Equal(failedOnlyBefore, s.FailedOnly);
        Assert.False(s.DetailVisible);

        s = Press(s, Char('c'));   // close
        Assert.Null(s.Modal);
    }

    [Fact]
    public void Modal_n_p_traverse_all_refs_including_unresolved()
    {
        var s = Press(Play("files", 7), Down, Down, Down, Down, Down, Char('o'));
        var total = s.Modal!.Refs.Count;
        Assert.True(total >= 2);
        Assert.Equal(0, s.Modal.Index); // opens on first resolved ref

        for (var i = 1; i < total; i++)
            s = Press(s, Char('n'));
        Assert.Equal(total - 1, s.Modal!.Index);
        Assert.False(s.Modal.Current.Exists); // last ref in the crafted failure is the unresolved frame

        s = Press(s, Char('n'));               // wraps around
        Assert.Equal(0, s.Modal!.Index);
    }

    [Fact]
    public void Queued_reruns_coalesce_into_one_followup()
    {
        var s = Play("files", 7);                 // completed, not running
        s = Press(s, Char('R'));                  // rerun all → launches
        Assert.True(s.Running);
        Assert.Equal(1, s.RunGeneration);

        // Two more reruns while running → queued and coalesced (no extra launches yet).
        s = Press(s, Char('R'), Char('R'));
        Assert.Equal(1, s.RunGeneration);
        Assert.Contains("queued", s.Toast, StringComparison.OrdinalIgnoreCase);

        // Completion launches exactly ONE consolidated follow-up.
        s = Reducer.Reduce(s, new AppEvent.RunCompleted());
        Assert.Equal(2, s.RunGeneration);
        Assert.False(s.QueuedRerunAll);
    }

    [Fact]
    public void Filter_moves_selection_to_a_survivor_when_selected_leaf_vanishes()
    {
        // Select a PASSING leaf (Subtract_ReturnsDifference), then filter to failed-only.
        var s = Press(Play("files", 7), Down, Down, Down, Down, Down, Down);
        var passingLeaf = s.Selection;
        s = Press(s, Char('f'));

        Assert.NotNull(s.Selection);
        Assert.NotEqual(passingLeaf, s.Selection);                    // moved off the vanished leaf
        Assert.Contains(s.Rows, r => r.Node.Id.Equals(s.Selection!.Value)); // resolves to a live row
    }

    [Fact]
    public void Highlighter_colours_csharp_and_leaves_unknown_plain()
    {
        var lang = SyntaxHighlighter.LanguageForExtension("Foo.cs");
        Assert.NotNull(lang);
        var lines = SyntaxHighlighter.Highlight("public int X = 1; // note", lang);
        Assert.NotNull(lines);
        var spans = lines!.SelectMany(l => l).ToList();
        Assert.Contains(spans, sp => sp.Color == HlColor.Keyword);   // 'public'/'int'
        Assert.Contains(spans, sp => sp.Color == HlColor.Comment);   // '// note'

        Assert.Null(SyntaxHighlighter.LanguageForExtension("layout.tmpl")); // unknown → render plain
    }
}
