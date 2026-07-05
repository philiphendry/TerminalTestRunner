namespace Ttr.Ui;

/// <summary>
/// Startup capability detection (plan §11.2, brief M2). Reads the environment (UTF-8 locale, <c>NO_COLOR</c>,
/// <c>TERM</c>, the <c>TTR_ASCII</c> override) plus the Windows VT-enable result and decides the render tier:
/// full → ASCII glyphs → refuse. A dumb terminal (<c>TERM=dumb</c>) or a console where VT could not be enabled
/// is refused with a one-line message and a non-zero exit — a broken TUI is worse than none.
/// </summary>
public static class Capabilities
{
    /// <summary>The outcome: either a <see cref="Caps"/> to render with, or a <see cref="Refusal"/> message.</summary>
    public readonly record struct Detection(Caps Caps, string? Refusal)
    {
        public bool Refused => Refusal is not null;
    }

    /// <summary>
    /// Decide the render tier. <paramref name="vtError"/> is the Windows VT-enable failure message (null when VT
    /// is available / not Windows). Environment lookups go through <paramref name="getEnv"/> so the policy is
    /// unit-testable without touching the real process environment.
    /// </summary>
    public static Detection Detect(string? vtError, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        // A legacy console that can't do VT can't render the TUI at all (POC-4 / Phase 4 path).
        if (vtError is not null)
            return new Detection(Caps.Full, vtError);

        // TERM=dumb: no cursor addressing / SGR — refuse rather than spray escapes into a scrollback log.
        var term = getEnv("TERM");
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return new Detection(Caps.Full,
                "ttr needs an interactive VT-capable terminal (TERM=dumb). Run it in a real terminal.");

        var forceAscii = getEnv("TTR_ASCII") == "1";
        var noColor = getEnv("NO_COLOR") is { Length: > 0 };
        var unicode = !forceAscii && IsUtf8(getEnv);
        return new Detection(new Caps(Unicode: unicode, Color: !noColor), null);
    }

    /// <summary>Whether the terminal is a UTF-8 locale (drives Unicode vs ASCII glyphs). The POSIX locale env
    /// vars are authoritative when set (in POSIX precedence order) — .NET defaults <c>Console.OutputEncoding</c>
    /// to UTF-8 on Unix regardless of the actual terminal, so it cannot be the primary signal; a locale of
    /// <c>C</c>/<c>POSIX</c> therefore correctly degrades to ASCII (AC3). With no locale set, the console code
    /// page decides, falling back to UTF-8 on Windows (modern consoles) and ASCII elsewhere (the safe choice).</summary>
    private static bool IsUtf8(Func<string, string?> getEnv)
    {
        foreach (var name in (string[])["LC_ALL", "LC_CTYPE", "LANG"])
        {
            var value = getEnv(name);
            if (!string.IsNullOrEmpty(value))
                return value.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("UTF8", StringComparison.OrdinalIgnoreCase);
        }

        try { if (Console.OutputEncoding.CodePage == 65001) return true; }
        catch (IOException) { /* output redirected */ }
        return OperatingSystem.IsWindows();
    }
}
