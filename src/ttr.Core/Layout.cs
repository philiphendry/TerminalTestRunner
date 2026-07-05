namespace Ttr.Core;

/// <summary>
/// Pure layout arithmetic shared by the reducer (for nav/scroll clamping) and the renderer (for
/// drawing), so the tree viewport the reducer scrolls by always matches the rows the renderer draws.
/// No terminal types — just cell counts (CLAUDE.md: Core carries no rendering/terminal dependency).
///
/// Screen rows: 1 = header, <c>height</c> = footer. A visible toast steals one row above the footer.
/// The remaining "body" holds the tree, and — when the detail pane is open — the detail pane, either
/// to the right (≈40% width) or beneath (≈40% height, with a 1-row separator).
/// </summary>
public static class Layout
{
    // Strictly-exceed minimums: exactly 40×10 shows the placeholder (brief AC3).
    public const int MinWidth = 40;
    public const int MinHeight = 10;

    public static bool TooSmall(int width, int height) => width <= MinWidth || height <= MinHeight;

    /// <summary>Body rows available below the header and above the footer (and toast, if shown).</summary>
    public static int BodyRows(AppState s, int height) =>
        Math.Max(0, height - 2 - (s.Toast is not null ? 1 : 0));

    /// <summary>Detail pane width when docked right (includes neither the 1-col separator nor the tree).</summary>
    public static int DetailPaneWidth(int width) => Math.Clamp((int)(width * 0.40), 16, Math.Max(16, width - 24));

    /// <summary>Detail pane height when docked beneath (excludes its 1-row separator).</summary>
    public static int DetailPaneHeight(int bodyRows) => Math.Clamp((int)(bodyRows * 0.40), 3, Math.Max(3, bodyRows - 3));

    /// <summary>Width of the tree pane (narrower when the detail pane is docked right).</summary>
    public static int TreeWidth(AppState s, int width) =>
        s.DetailVisible && s.DetailOrientation == DetailOrientation.Right
            ? Math.Max(8, width - DetailPaneWidth(width) - 1)
            : width;

    /// <summary>Number of tree rows visible — the reducer's nav/scroll viewport.</summary>
    public static int TreeViewportRows(AppState s, int width, int height)
    {
        var body = BodyRows(s, height);
        if (s.DetailVisible && s.DetailOrientation == DetailOrientation.Beneath)
            return Math.Max(1, body - DetailPaneHeight(body) - 1);
        return Math.Max(1, body);
    }

    /// <summary>Number of detail-pane content rows visible (excludes its title row).</summary>
    public static int DetailViewportRows(AppState s, int width, int height)
    {
        if (!s.DetailVisible) return 0;
        var body = BodyRows(s, height);
        var rows = s.DetailOrientation == DetailOrientation.Beneath ? DetailPaneHeight(body) : body;
        return Math.Max(1, rows - 1); // minus the pane title row
    }

    // --- 'o' modal geometry (M5): a bordered box ~90% of the screen -------------

    /// <summary>Modal box outer width (border included).</summary>
    public static int ModalWidth(int width) => Math.Max(20, width - 4);

    /// <summary>Modal box outer height (border included).</summary>
    public static int ModalHeight(int height) => Math.Max(6, height - 2);

    /// <summary>Visible file-content rows inside the modal box (title and footer live in the borders).</summary>
    public static int ModalContentRows(int height) => Math.Max(1, ModalHeight(height) - 2);
}
