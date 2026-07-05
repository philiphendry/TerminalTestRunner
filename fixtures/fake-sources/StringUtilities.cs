using System;
using System.Globalization;

namespace Contoso.Sample.Calculations;

/// <summary>Second source file, referenced by a helper frame in a fabricated stack trace (brief M1).</summary>
public static class StringUtilities
{
    public static string Format(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Trim().ToUpper(CultureInfo.InvariantCulture);   // line 12 — referenced frame
    }

    public static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
}
