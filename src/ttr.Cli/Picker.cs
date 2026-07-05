namespace Ttr.Cli;

/// <summary>
/// The pre-UI multi-select picker shown when a CWD scan finds several candidates (plan §4). A simple
/// numbered prompt before the alt-screen is entered; a fuller reuse of the tree's list primitives is a
/// polish item (recorded in docs/phase-3-notes.md). Returns the chosen targets, or an empty list if the
/// user cancels. With no interactive input, auto-selects all candidates.
/// </summary>
public static class Picker
{
    public static IReadOnlyList<string> Choose(IReadOnlyList<string> candidates)
    {
        if (Console.IsInputRedirected) return candidates;   // non-interactive → take everything

        Console.WriteLine("Multiple targets found — choose one or more (Enter = all):");
        for (var i = 0; i < candidates.Count; i++)
            Console.WriteLine($"  [{i + 1}] {Path.GetFileName(candidates[i])}");
        Console.Write("Selection (e.g. 1,3 or 'a' for all, blank to quit): ");

        var line = Console.ReadLine();
        if (line is null) return [];
        line = line.Trim();
        if (line.Length == 0) return [];
        if (line.Equals("a", StringComparison.OrdinalIgnoreCase)) return candidates;

        var chosen = new List<string>();
        foreach (var token in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(token, out var n) && n >= 1 && n <= candidates.Count)
                chosen.Add(candidates[n - 1]);
        return chosen.Count > 0 ? chosen : candidates;
    }
}
