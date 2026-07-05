using Ttr.Ui;
using Xunit;

namespace Ttr.Tests;

/// <summary>Capability detection + degrade transform (brief M2, AC3). Pure — no terminal, env injected.</summary>
public class CapabilitiesTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Utf8_locale_is_full_tier()
    {
        var d = Capabilities.Detect(vtError: null, Env(("LANG", "en_US.UTF-8")));
        Assert.False(d.Refused);
        Assert.True(d.Caps.Unicode);
        Assert.True(d.Caps.Color);
    }

    [Fact]
    public void Non_utf8_locale_degrades_to_ascii()
    {
        var d = Capabilities.Detect(vtError: null, Env(("LANG", "C"), ("LC_ALL", "C")));
        Assert.False(d.Refused);
        Assert.False(d.Caps.Unicode);   // ASCII glyphs
    }

    [Fact]
    public void Lc_all_takes_precedence_over_lang()
    {
        // POSIX precedence: LC_ALL wins even when LANG says UTF-8.
        var d = Capabilities.Detect(vtError: null, Env(("LC_ALL", "C"), ("LANG", "en_US.UTF-8")));
        Assert.False(d.Caps.Unicode);
    }

    [Fact]
    public void Ttr_ascii_env_forces_fallback_even_on_utf8()
    {
        var d = Capabilities.Detect(vtError: null, Env(("LANG", "en_US.UTF-8"), ("TTR_ASCII", "1")));
        Assert.False(d.Refused);
        Assert.False(d.Caps.Unicode);
    }

    [Fact]
    public void No_color_disables_colour_but_keeps_glyph_tier()
    {
        var d = Capabilities.Detect(vtError: null, Env(("LANG", "en_US.UTF-8"), ("NO_COLOR", "1")));
        Assert.False(d.Refused);
        Assert.True(d.Caps.Unicode);
        Assert.False(d.Caps.Color);
        Assert.True(d.Caps.StalePrefix);   // colourless → '~' stale marker
    }

    [Fact]
    public void Dumb_terminal_is_refused()
    {
        var d = Capabilities.Detect(vtError: null, Env(("TERM", "dumb"), ("LANG", "en_US.UTF-8")));
        Assert.True(d.Refused);
        Assert.Contains("dumb", d.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vt_enable_failure_is_refused_with_that_message()
    {
        var d = Capabilities.Detect(vtError: "legacy conhost", Env(("LANG", "en_US.UTF-8")));
        Assert.True(d.Refused);
        Assert.Equal("legacy conhost", d.Refusal);
    }
}
