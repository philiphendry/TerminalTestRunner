namespace Ttr.Core;

/// <summary>Where the detail pane sits relative to the tree ('b' toggles).</summary>
public enum DetailOrientation
{
    /// <summary>Right of the tree, ~40% width.</summary>
    Right,

    /// <summary>Beneath the tree, ~40% height.</summary>
    Beneath,
}

/// <summary>Which pane keyboard input scrolls (Tab cycles).</summary>
public enum PaneFocus
{
    Tree,
    Detail,
}
