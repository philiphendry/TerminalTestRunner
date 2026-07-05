using System.Collections.Immutable;

namespace Ttr.Core;

/// <summary>A materialised tree row: the node plus its indentation depth.</summary>
public readonly record struct FlatRow(TestNode Node, int Depth);

/// <summary>
/// Flattens the expanded tree to an ordered list (plan §11.2, CLAUDE.md invariant 3). This is
/// O(visible nodes); the render layer caches the result keyed on <see cref="AppState.TreeVersion"/>
/// and materialises only the viewport slice, so total node count is irrelevant to render cost.
/// </summary>
public static class TreeFlattener
{
    public static List<FlatRow> Flatten(AppState state)
        => Flatten(state.Root, state.Expanded, state.FailedOnly);

    public static List<FlatRow> Flatten(TestNode root, ImmutableHashSet<TestCaseId> expanded, bool failedOnly = false)
    {
        var rows = new List<FlatRow>();
        Walk(root, 0, expanded, failedOnly, rows);
        return rows;
    }

    private static void Walk(
        TestNode node, int depth, ImmutableHashSet<TestCaseId> expanded, bool failedOnly, List<FlatRow> rows)
    {
        // Failed-only (plan §11.4): a subtree with no failing leaf is skipped entirely, so only failed
        // leaves and their ancestor chain remain. Expansion is still honoured within that.
        if (failedOnly && !HasFailure(node)) return;

        rows.Add(new FlatRow(node, depth));
        if (node.Children.Count > 0 && expanded.Contains(node.Id))
        {
            foreach (var child in node.Children)
                Walk(child, depth + 1, expanded, failedOnly, rows);
        }
    }

    private static bool HasFailure(TestNode node) =>
        node.IsLeaf ? node.Status == TestStatus.Failed : node.Failed > 0;
}
