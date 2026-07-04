namespace Ttr.Core;

/// <summary>
/// The single typed message stream (CLAUDE.md invariant 1, plan Appendix B). Every input —
/// adapter events, keystrokes, resize, fatal errors — is one of these on ONE channel; a single
/// consumer applies reducers. Phase 1 implements only the subset the fake adapter + UI emit.
/// </summary>
public abstract record AppEvent
{
    /// <summary>A batch of discovered tests (streamed as discovery finds them).</summary>
    public sealed record TestsDiscovered(IReadOnlyList<TestIdentity> Tests) : AppEvent;

    /// <summary>A test began executing. Carries full identity so mid-run theory cases can be inserted.</summary>
    public sealed record TestStarted(TestIdentity Test) : AppEvent;

    /// <summary>A test finished with an outcome + duration (and optional message, unused in Phase 1 UI).</summary>
    public sealed record TestFinished(TestIdentity Test, TestOutcome Outcome, TimeSpan Duration, string? Message = null)
        : AppEvent;

    /// <summary>The run finished. The reducer sweeps any still-Running leaves to NotRun (no phantom spinners).</summary>
    public sealed record RunCompleted : AppEvent;

    /// <summary>A key was read on the input thread.</summary>
    public sealed record KeyPressed(ConsoleKeyInfo Key) : AppEvent;

    /// <summary>The terminal was resized (drives nav clamping; the renderer polls size independently for drawing).</summary>
    public sealed record Resized(int Width, int Height) : AppEvent;

    /// <summary>An unrecoverable error to surface before exiting.</summary>
    public sealed record FatalError(string Message, string? Detail = null) : AppEvent;
}
