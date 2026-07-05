using Ttr.Core;

namespace Ttr.Ui;

/// <summary>The '?' help overlay: the full §11.3 keymap in a centred box; any key closes it (M6).</summary>
public static class HelpRenderer
{
    private static readonly (string Keys, string Action)[] Keymap =
    [
        ("↑/↓  j/k", "Move selection"),
        ("→/←", "Expand / collapse (← on leaf → parent)"),
        ("PgUp/PgDn  Home/End", "Page / jump (tree or focused detail)"),
        ("e", "Toggle expand/collapse of node"),
        ("E  (Shift+E)", "Toggle node AND all descendants"),
        ("f", "Toggle failed-only filter"),
        ("s", "Toggle detail pane"),
        ("b", "Detail pane right ↔ beneath"),
        ("w", "Toggle word wrap (detail / modal)"),
        ("t", "Toggle durations"),
        ("r", "Rerun tests under selected node"),
        ("R  (Shift+R)", "Rerun all"),
        ("o", "Open file reference in modal"),
        ("Tab", "Cycle focus tree ↔ detail"),
        ("?", "This help overlay"),
        ("q / Ctrl+C", "Quit"),
        ("", ""),
        ("Modal:", "c/Esc close · n/p next/prev · w wrap · ↑↓/PgUp/PgDn scroll"),
    ];

    public static string Render(int width, int height)
    {
        var boxW = Layout.ModalWidth(width);
        var boxH = Layout.ModalHeight(height);
        var inner = Overlay.Inner(boxW);

        var rows = new List<string>();
        var keyCol = Math.Min(24, Math.Max(10, inner / 3));
        foreach (var (keys, action) in Keymap)
        {
            if (keys.Length == 0 && action.Length == 0) { rows.Add(new string(' ', inner)); continue; }
            var line = " " + Cells.FitPad(keys, keyCol) + "  " + action;
            rows.Add(Cells.FitPad(line, inner));
        }

        return Overlay.Render(width, height, boxW, boxH, "ttr — keymap (§11.3)", "press any key to close", rows);
    }
}
