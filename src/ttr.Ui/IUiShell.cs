namespace Ttr.Ui;

/// <summary>
/// The seam between the renderer and the physical terminal (plan §11, CLAUDE.md invariant 4).
/// The ANSI implementation enters the alternate screen on <see cref="Enter"/> and MUST restore
/// the terminal on <see cref="Exit"/>/<see cref="IDisposable.Dispose"/> on every exit path,
/// including crashes. A fake implementation lets frames be rendered into memory for tests.
/// </summary>
public interface IUiShell : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Enter the alternate screen and hide the cursor.</summary>
    void Enter();

    /// <summary>Restore the terminal (cursor shown, main screen). Idempotent.</summary>
    void Exit();

    /// <summary>Write a fully-composed, cursor-positioned frame.</summary>
    void Write(string frame);
}
