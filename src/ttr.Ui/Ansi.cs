namespace Ttr.Ui;

/// <summary>Raw ANSI escape sequences for the direct-ANSI renderer (CLAUDE.md invariant 4).</summary>
public static class Ansi
{
    public const string Esc = "\x1b";

    public const string AltScreenOn = Esc + "[?1049h";
    public const string AltScreenOff = Esc + "[?1049l";
    public const string HideCursor = Esc + "[?25l";
    public const string ShowCursor = Esc + "[?25h";
    public const string CursorHome = Esc + "[H";
    public const string ClearToEol = Esc + "[K";
    public const string ClearScreen = Esc + "[2J";

    public const string Reset = Esc + "[0m";
    public const string Bold = Esc + "[1m";
    public const string Dim = Esc + "[2m";
    public const string Underline = Esc + "[4m";
    public const string Reverse = Esc + "[7m";
    public const string ReverseOff = Esc + "[27m";

    public const string Red = Esc + "[31m";
    public const string Green = Esc + "[32m";
    public const string Yellow = Esc + "[33m";
    public const string Blue = Esc + "[34m";
    public const string Cyan = Esc + "[36m";
    public const string Grey = Esc + "[90m";

    /// <summary>Move the cursor to (row, col), both 1-based.</summary>
    public static string MoveTo(int row, int col) => $"{Esc}[{row};{col}H";
}
