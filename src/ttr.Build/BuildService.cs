using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ttr.Core;

namespace Ttr.Build;

/// <summary>The result of building one project: success, parsed diagnostics, and the raw output kept as
/// the user-openable fallback (plan §7).</summary>
public sealed record BuildOutcome(bool Success, IReadOnlyList<BuildDiagnostic> Diagnostics, string RawOutput);

/// <summary>
/// Out-of-process build (plan §7): <c>dotnet build … --nologo -v:quiet -tl:off
/// -consoleLoggerParameters:ErrorsOnly;NoSummary</c>, so a crashed build can never take the TUI down and
/// we get MSBuild node reuse for free. Parses the canonical diagnostic format
/// (<c>path(line,col): error CODE: message</c>, POC-6) and keeps the raw output attached. The orchestrator
/// fans these out across test projects in parallel (<c>Task.WhenAll</c>) — sequential builds miss the
/// watch latency bar (§7). Every spawned process carries <c>DOTNET_CLI_UI_LANGUAGE=en</c> (CLAUDE.md
/// locale rule) so the diagnostic (and later stack-frame) strings are the English forms the parser expects.
/// </summary>
public static partial class BuildService
{
    [GeneratedRegex(@"^(?<file>[^(\r\n]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<msg>.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticRx();

    /// <summary>
    /// Staleness: the project is stale if its output assembly is missing or older than the newest input
    /// (project file + any Compile source). Only stale projects build; <c>--no-build</c> bypasses this
    /// entirely (plan §7).
    /// </summary>
    public static bool IsStale(string projectPath, TfmEvaluation tfm)
    {
        if (string.IsNullOrEmpty(tfm.OutputAssemblyPath) || !File.Exists(tfm.OutputAssemblyPath))
            return true;

        var outputTime = File.GetLastWriteTimeUtc(tfm.OutputAssemblyPath);
        var newest = SafeWriteTime(projectPath);
        foreach (var src in tfm.CompileFiles)
        {
            var t = SafeWriteTime(src);
            if (t > newest) newest = t;
        }
        return newest > outputTime;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (IOException) { return DateTime.MinValue; }
    }

    public static async Task<BuildOutcome> BuildAsync(string projectPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? ".",
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v:quiet");
        psi.ArgumentList.Add("-tl:off");
        psi.ArgumentList.Add("-consoleLoggerParameters:ErrorsOnly;NoSummary");
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";   // CLAUDE.md locale rule (starts this phase)

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        var raw = output.ToString();
        var diagnostics = ParseDiagnostics(raw);
        var success = proc.ExitCode == 0;
        return new BuildOutcome(success, diagnostics, raw);
    }

    /// <summary>Parse canonical diagnostics; multi-line/non-canonical messages are dropped, which is why the
    /// raw output is always kept attached (plan §7). Errors first, then warnings, in file order.</summary>
    public static IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string rawOutput)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<BuildDiagnostic>();
        foreach (Match m in DiagnosticRx().Matches(rawOutput))
        {
            var file = m.Groups["file"].Value.Trim();
            var key = $"{file}|{m.Groups["line"].Value}|{m.Groups["col"].Value}|{m.Groups["code"].Value}";
            if (!seen.Add(key)) continue;   // dotnet prints each diagnostic per-TFM; dedup
            diagnostics.Add(new BuildDiagnostic(
                file,
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["code"].Value,
                m.Groups["msg"].Value,
                m.Groups["sev"].Value));
        }
        return diagnostics
            .OrderByDescending(d => d.Severity == "error")
            .ToList();
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (SystemException) { }
    }
}
