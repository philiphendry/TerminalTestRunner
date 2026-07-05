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
        if (node.Kind == TestNodeKind.Notice)
        {
            ComposeNotice(node, lines);
            return lines;
        }
        if (node.BuildPhase == BuildPhase.Failed)
        {
            // A build-failed project has no discovered children (discovery is gated on build), so route
            // by build state before the leaf check below (plan §7).
            ComposeBuildFailure(node, lines);
            return lines;
        }
        // Structural nodes are branches even when childless (e.g. a project mid-build, or an
        // unknown-runner project with no tests); only Method/Case nodes are true test leaves.
        var isBranch = !node.IsLeaf
            || node.Kind is TestNodeKind.Solution or TestNodeKind.Project or TestNodeKind.Tfm
                or TestNodeKind.Namespace or TestNodeKind.Class;
        if (isBranch)
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

    /// <summary>A standalone diagnostic node (phantom / unparseable / smoke failure): its notice, verbatim.</summary>
    private static void ComposeNotice(TestNode node, List<DetailLine> lines)
    {
        lines.Add(new DetailLine(node.Name, Header: true));
        if (node.Notice is not { } n)
        {
            lines.Add(new DetailLine("(no detail)"));
            return;
        }
        lines.Add(new DetailLine($"{n.Severity}: {n.Summary}"));
        if (!string.IsNullOrEmpty(n.Detail))
        {
            lines.Add(new DetailLine(""));
            foreach (var l in Split(n.Detail)) lines.Add(new DetailLine("  " + l));
        }
    }

    /// <summary>A build-failed project: parsed diagnostics (openable via 'o') then the raw output fallback (plan §7).</summary>
    private static void ComposeBuildFailure(TestNode node, List<DetailLine> lines)
    {
        lines.Add(new DetailLine(node.Name, Header: true));
        lines.Add(new DetailLine("Build failed"));

        if (node.BuildDiagnostics.Count > 0)
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine($"{node.BuildDiagnostics.Count} diagnostic(s):", Header: true));
            foreach (var d in node.BuildDiagnostics)
            {
                var text = d.ToString();
                lines.Add(new DetailLine("  " + text, Underline: ResolvesInLine(node, text)));
            }
        }

        if (!string.IsNullOrEmpty(node.BuildOutput))
        {
            lines.Add(new DetailLine(""));
            lines.Add(new DetailLine("Build output:", Header: true));
            foreach (var l in Split(node.BuildOutput)) lines.Add(new DetailLine("  " + l));
        }
    }

    private static void ComposeBranch(TestNode node, List<DetailLine> lines)
    {
        lines.Add(new DetailLine(node.Name, Header: true));

        // A classification note on the project (dead MTP opt-in, unknown runner, dual-mode info) leads.
        if (node.Notice is { } notice)
        {
            lines.Add(new DetailLine($"{notice.Severity}: {notice.Summary}"));
            if (!string.IsNullOrEmpty(notice.Detail))
                foreach (var l in Split(notice.Detail)) lines.Add(new DetailLine("  " + l));
            lines.Add(new DetailLine(""));
        }

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
