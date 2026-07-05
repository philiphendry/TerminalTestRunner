using Ttr.Ui;

namespace Ttr.Tests;

/// <summary>
/// Test-only <see cref="IUiShell"/> that renders frames into memory instead of a terminal (brief M7,
/// plan §12). Fixed width/height; captures the last frame written so snapshot tests can assert on it.
/// </summary>
internal sealed class StringUiShell(int width, int height) : IUiShell
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public string LastFrame { get; private set; } = "";

    public void Enter() { }
    public void Exit() { }
    public void Write(string frame) => LastFrame = frame;
    public void Dispose() { }
}
