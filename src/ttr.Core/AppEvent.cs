namespace Ttr.Core;

/// <summary>
/// The single typed message stream (CLAUDE.md invariant 1, plan Appendix B). Every input —
/// adapter events, keystrokes, resize, fatal errors — is one of these on ONE channel; a single
/// consumer applies reducers. Phase 1 implements only the subset the fake adapter + UI emit.
/// </summary>
public abstract record AppEvent
{
    /// <summary>
    /// A project entered the tree after MSBuild evaluation + §6.2 detection, BEFORE build/discovery
    /// (Phase 3). Creates the Project node (keyed by <paramref name="ProjectPath"/>) with its display
    /// name, detected runner, and evaluated TFM set — a single TFM collapses the TFM level, two or more
    /// insert TFM child nodes eagerly. An optional <see cref="NodeNotice"/> carries a classification
    /// note (dead MTP opt-in, unknown runner, dual-mode info).
    /// </summary>
    public sealed record ProjectRegistered(
        string ProjectPath, string DisplayName, IReadOnlyList<string> Tfms,
        RunnerKind Runner, NodeNotice? Notice = null) : AppEvent;

    /// <summary>
    /// A standalone diagnostic node under the solution root (brief M1, plan §4): a phantom project
    /// (referenced but file-not-found) or an unparseable solution entry. <paramref name="Key"/> is a
    /// unique child key; <paramref name="Name"/> is the display label.
    /// </summary>
    public sealed record NoticeRaised(string Key, string Name, NodeNotice Notice) : AppEvent;

    /// <summary>A project's build started — the Project node shows a spinner (plan §7).</summary>
    public sealed record BuildStarted(string ProjectPath) : AppEvent;

    /// <summary>A project's build succeeded; discovery may proceed for it (plan §7).</summary>
    public sealed record BuildSucceeded(string ProjectPath) : AppEvent;

    /// <summary>
    /// A project's build failed (plan §7): red project node whose detail pane shows the parsed
    /// <paramref name="Diagnostics"/> and the <paramref name="RawOutput"/> fallback. The reducer derives
    /// the node's 'o' file references from the raw output so the modal works on build errors too.
    /// </summary>
    public sealed record BuildFailed(
        string ProjectPath, IReadOnlyList<BuildDiagnostic> Diagnostics, string RawOutput) : AppEvent;

    /// <summary>
    /// Runner smoke validation failed for a (project, TFM) (plan §6.2): an MTP host that exited without
    /// a handshake, or a test project that discovered zero tests. Surfaces a warning
    /// <see cref="TestNodeKind.Notice"/> child under the project (or its TFM node) — never a silently
    /// empty subtree. <paramref name="Tfm"/> is null for single-TFM projects.
    /// </summary>
    public sealed record DiscoveryFailed(string ProjectPath, string? Tfm, NodeNotice Notice) : AppEvent;

    /// <summary>A batch of discovered tests (streamed as discovery finds them).</summary>
    public sealed record TestsDiscovered(IReadOnlyList<TestIdentity> Tests) : AppEvent;

    /// <summary>A test began executing. Carries full identity so mid-run theory cases can be inserted.</summary>
    public sealed record TestStarted(TestIdentity Test) : AppEvent;

    /// <summary>A test finished with an outcome, duration, and (for failures) rich result detail.</summary>
    public sealed record TestFinished(
        TestIdentity Test, TestOutcome Outcome, TimeSpan Duration, TestResultDetail? Detail = null)
        : AppEvent;

    /// <summary>The run finished. The reducer sweeps any still-Running leaves to NotRun (no phantom spinners).</summary>
    public sealed record RunCompleted : AppEvent;

    // --- Watch subsystem (Phase 5, plan §9) ------------------------------------

    /// <summary>
    /// The watch coordinator's coarse activity changed (plan §9). Carries only the states that aren't
    /// already observable from <see cref="AppState.Busy"/> (building) or <see cref="AppState.Running"/>
    /// (running): a detected-but-not-yet-built change, a change queued behind an active run, or a return
    /// to idle. Ignored when not in watch mode.
    /// </summary>
    public sealed record WatchStateChanged(WatchActivity Activity) : AppEvent;

    /// <summary>
    /// A re-discovery cycle is beginning for <paramref name="ProjectPaths"/> (plan §8, brief M3): the
    /// reducer tombstones every existing leaf under those projects. Surviving tests are un-tombstoned as
    /// their <see cref="TestsDiscovered"/> events re-arrive; <see cref="RediscoveryCompleted"/> then sweeps
    /// whatever is still tombstoned (the removed tests). Kept tests retain their status/detail — the diff
    /// never blanks a result.
    /// </summary>
    public sealed record RediscoveryStarted(IReadOnlyList<string> ProjectPaths) : AppEvent;

    /// <summary>Re-discovery finished for <paramref name="ProjectPaths"/>: remove any still-tombstoned leaf,
    /// prune emptied branches, and keep selection valid (move to the nearest survivor). Brief M3.</summary>
    public sealed record RediscoveryCompleted(IReadOnlyList<string> ProjectPaths) : AppEvent;

    /// <summary>
    /// The watch cycle asks to auto-rerun the affected test set (plan §9): all leaves under
    /// <paramref name="ProjectPaths"/>, narrowed to failed-only when the 'f' filter is active (AC7). The
    /// reducer marks them <see cref="TestStatus.Queued"/> and launches a run — or, if a run is already
    /// active, coalesces them into the ONE consolidated follow-up run (reusing the Phase 4 queue, AC6).
    /// </summary>
    public sealed record WatchRerunRequested(IReadOnlyList<string> ProjectPaths) : AppEvent;

    /// <summary>Off-thread syntax highlighting for the open 'o' modal is ready (plan §11.5): one span
    /// list per source line.</summary>
    public sealed record HighlightReady(string FilePath, IReadOnlyList<IReadOnlyList<HlSpan>> Lines) : AppEvent;

    /// <summary>A transient toast's lifetime elapsed; the reducer clears it iff the id still matches.</summary>
    public sealed record ToastExpired(long Id) : AppEvent;

    /// <summary>A key was read on the input thread.</summary>
    public sealed record KeyPressed(ConsoleKeyInfo Key) : AppEvent;

    /// <summary>The terminal was resized (drives nav clamping; the renderer polls size independently for drawing).</summary>
    public sealed record Resized(int Width, int Height) : AppEvent;

    /// <summary>An unrecoverable error to surface before exiting.</summary>
    public sealed record FatalError(string Message, string? Detail = null) : AppEvent;
}
