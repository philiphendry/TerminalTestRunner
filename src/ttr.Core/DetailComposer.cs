namespace Ttr.Core;

/// <summary>One logical line of detail-pane content. <see cref="Header"/> lines are section labels;
/// <see cref="Underline"/> marks a stack frame whose file reference resolved (the "'o' will work" cue,
/// plan §11.2).</summary>
public readonly record struct DetailLine(string Text, bool Underline = false, bool Header = false);

/// <summary>
/// Builds the detail pane's logical lines for a node (plan §11.2, brief M2). Pure and terminal-free,
/// so the reducer can count lines for scroll clamping and the renderer can draw them; wrapping/
/// truncation to the pane width happens in the renderer (cell-aware).
/// </summary>
public static class DetailComposer
{
    private const int MaxFailingListed = 12;

    public static IReadOnlyList<DetailLine> Compose(TestNode node)
    {
        var lines = new List<DetailLine>();
        if (!node.IsLeaf)
        {
            ComposeBranch(node, lines);
            return lines;
        }

        lines.Add(new DetailLine($"{node.Name}", Header: true));
        lines.Add(new DetailLine($"Status: {node.Status}    Duration: {FormatDuration(node.Duration)}"));

        var d = node.Detail;
        if (d is null || d.IsEmpty)
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("(no failure detail)"));
            return lines;
        }

        if (!string.IsNullOrEmpty(d.Message))
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("Message:", Header: true));
            foreach (var l in Split(d.Message)) lines.Add(new DetailLine("  " + l));
        }

        if (d.ExceptionChain is { Count: > 0 })
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("Exception:", Header: true));
            for (var i = 0; i < d.ExceptionChain.Count; i++)
                lines.Add(new DetailLine((i == 0 ? "  " : "   ---> ") + d.ExceptionChain[i]));
        }

        if (!string.IsNullOrEmpty(d.StackTrace))
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("Stack trace:", Header: true));
            foreach (var l in Split(d.StackTrace))
                lines.Add(new DetailLine(l, Underline: ResolvesInLine(node, l)));
        }

        if (!string.IsNullOrEmpty(d.StandardOutput))
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("Output:", Header: true));
            foreach (var l in Split(d.StandardOutput)) lines.Add(new DetailLine("  " + l));
        }

        return lines;
    }

    private static void ComposeBranch(TestNode node, List<DetailLine> lines)
    {
        lines.Add(new DetailLine(node.Name, Header: true));
        lines.Add(new DetailLine(
            $"{node.TotalLeaves} tests · {node.Passed}✓ {node.Failed}✗ {node.Skipped}⊘"));

        if (node.Failed == 0)
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("(no failures in this subtree)"));
            return;
        }

        lines.Add(new DetailLine(""));
        lines.Add(new DetailLine($"{node.Failed} failing:", Header: true));
        var listed = 0;
        foreach (var leaf in FailingLeaves(node))
        {
            if (listed++ >= MaxFailingListed)
            {
                lines.Add(new DetailLine($"  … and {node.Failed - MaxFailingListed} more"));
                break;
            }
            lines.Add(new DetailLine("  ✗ " + QualifiedName(leaf)));
        }
    }

    private static IEnumerable<TestNode> FailingLeaves(TestNode node)
    {
        if (node.IsLeaf)
        {
            if (node.Status == TestStatus.Failed) yield return node;
            yield break;
        }
        foreach (var c in node.Children)
            foreach (var l in FailingLeaves(c))
                yield return l;
    }

    private static string QualifiedName(TestNode leaf) =>
        leaf.Parent is { } p ? $"{p.Name}.{leaf.Name}" : leaf.Name;

    private static bool ResolvesInLine(TestNode node, string line)
    {
        foreach (var r in node.FileRefs)
            if (r.Exists && line.Contains(r.Path, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static IEnumerable<string> Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    public static string FormatDuration(TimeSpan d)
    {
        if (d <= TimeSpan.Zero) return "0ms";
        if (d.TotalSeconds >= 1) return $"{d.TotalSeconds:0.0}s";
        return $"{d.TotalMilliseconds:0}ms";
    }
}
