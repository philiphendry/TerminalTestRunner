using System.Text.RegularExpressions;
using Ttr.Core;
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
}
