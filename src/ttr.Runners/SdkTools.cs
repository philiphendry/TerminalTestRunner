namespace Ttr.Runners;

/// <summary>
/// Locates SDK-bundled tools (plan §6.3, Strategy A). We run as a global tool, so we cannot assume a
/// relative path to <c>vstest.console.dll</c>; instead we resolve it from the active .NET SDK directory.
/// The <c>Microsoft.TestPlatform.Portable</c> payload is the documented fallback (its host is net8.0) —
/// implemented here only as a located-path hook (env var), not a shipped payload yet (brief M6).
/// </summary>
public static class SdkTools
{
    /// <summary>Full path to <c>vstest.console.dll</c> in the highest installed SDK, or null if not found.</summary>
    public static string? FindVsTestConsole()
    {
        // Explicit override / fallback-payload hook.
        var overridePath = Environment.GetEnvironmentVariable("TTR_VSTEST_CONSOLE");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

        var sdkRoot = Path.Combine(DotnetRoot(), "sdk");
        if (!Directory.Exists(sdkRoot)) return null;

        return Directory.EnumerateDirectories(sdkRoot)
            .Select(d => Path.Combine(d, "vstest.console.dll"))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.Ordinal)   // highest SDK version wins
            .FirstOrDefault();
    }

    private static string DotnetRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "sdk"))) return env;

        // If we were launched via the dotnet muxer, ProcessPath is it; otherwise (apphost) this misses,
        // so fall through to PATH + well-known locations.
        var proc = Environment.ProcessPath;
        var procDir = proc is not null ? Path.GetDirectoryName(proc) : null;
        if (procDir is not null && Directory.Exists(Path.Combine(procDir, "sdk"))) return procDir;

        foreach (var dir in DotnetOnPath())
            if (Directory.Exists(Path.Combine(dir, "sdk"))) return dir;

        foreach (var known in new[] { "/usr/lib/dotnet", "/usr/share/dotnet", "/usr/local/share/dotnet" })
            if (Directory.Exists(Path.Combine(known, "sdk"))) return known;

        return procDir ?? "/usr/lib/dotnet";
    }

    /// <summary>Directories containing a <c>dotnet</c> executable found on PATH (symlinks resolved).</summary>
    private static IEnumerable<string> DotnetOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in new[] { "dotnet", "dotnet.exe" })
            {
                var candidate = Path.Combine(dir, exe);
                if (!File.Exists(candidate)) continue;
                string? resolvedDir = null;
                try
                {
                    var target = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;
                    resolvedDir = Path.GetDirectoryName(target);
                }
                catch (IOException) { }
                if (resolvedDir is not null) yield return resolvedDir;
            }
        }
    }
}
