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

/// <summary>
/// Whether watch mode is off or which <see cref="AppEvent"/> source drives it (plan §9, brief M6).
/// Off means the watch header segment is not shown at all, so every non-watch snapshot stays
/// byte-identical to earlier phases.
/// </summary>
public enum WatchKind
{
    /// <summary>Not watching (the default; no header segment).</summary>
    Off,

    /// <summary>Source mode (bare <c>--watch</c> / <c>--watch build</c>): ttr rebuilds on source changes.</summary>
    Build,

    /// <summary>External mode (<c>--watch external</c>): the user drives builds; ttr reacts to new assemblies.</summary>
    External,
}

/// <summary>
/// The watch pipeline's coarse activity, driven by the watch coordinator via
/// <see cref="AppEvent.WatchStateChanged"/> (plan §9). The <c>building</c>/<c>running</c> header states
/// are DERIVED from <see cref="AppState.Busy"/>/<see cref="AppState.Running"/>; this enum only carries
/// the states that aren't otherwise observable (a detected change before a build starts, and a change
/// coalesced behind an active run).
/// </summary>
public enum WatchActivity
{
    /// <summary>No cycle pending — <c>watch: idle</c>.</summary>
    Idle,

    /// <summary>A change was debounced and a cycle is about to build — <c>change detected</c>.</summary>
    ChangeDetected,

    /// <summary>A change landed during an active run and is queued into one follow-up cycle (AC6).</summary>
    Queued,
}
