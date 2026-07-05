using System.Text.RegularExpressions;

namespace TtrParser;

/// <summary>
/// Extracts ordered, de-duplicated source-file references from a test failure's message and stack
/// trace (plan §11.5, POC-9 spec in docs/ttr-poc-prompts_1.md).
///
/// NOTE (Phase 2 deviation, recorded in docs/phase-2-notes.md): the real POC-9 parser + corpus were
/// never in the repo seed, so this is implemented faithfully to that authoritative spec rather than
/// lifted verbatim. When the POC-9 artifacts surface, replace this and swap in its labelled corpus.
///
/// Rules:
/// - Stack-frame form <c> in &lt;path&gt;:line &lt;N&gt;</c> (English resource strings; ttr pins host
///   UI culture to en per §11.5). Trace order, top frame first.
/// - MSBuild form <c>&lt;path&gt;(&lt;line&gt;,&lt;col&gt;): (error|warning) CODE:</c>.
/// - Bare paths in the message, restricted to known source extensions to avoid false positives.
/// - Order: stack-trace refs, then message refs; de-duplicated by (resolved path, line).
/// - Frames whose path contains an <c>obj</c> segment or ends <c>.g.cs</c> are dropped entirely —
///   the user must never be sent to generated code (CLAUDE.md parser rule).
/// - Relative paths resolve against <c>projectDir</c>; each ref carries its existence check.
/// </summary>
public static class FileReferenceParser
{
    // " in <path>:line <N>" at end of a frame line. Non-greedy path so the LAST ":line" wins even
    // when the path itself contains ':' (Windows drive letters).
    private static readonly Regex StackFrameRx =
        new(@"\sin\s+(?<path>.+?):line\s+(?<line>\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // MSBuild diagnostic: path(line,col): error|warning CODE:
    private static readonly Regex MsBuildRx =
        new(@"(?<path>[^\s(][^()\r\n]*?)\((?<line>\d+),\d+\):\s+(?:error|warning)\s+\w+:",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // Bare path (absolute, or relative starting ./ ../) ending in a known source extension, with an
    // optional ":line N" suffix (some messages echo the stack-frame form inline).
    private static readonly Regex BarePathRx =
        new(@"(?<path>(?:[A-Za-z]:[\\/]|[\\/]|\.{1,2}[\\/])[^\s""'<>|]+?\.(?:cs|csproj|fs|vb|razor|json|xml|txt|config|props|targets))(?::line\s+(?<line>\d+))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<FileRef> Parse(string? message, string? stackTrace, string projectDir)
    {
        var result = new List<FileRef>();
        var seen = new HashSet<(string Path, int? Line)>();

        void Add(string rawPath, int? line, SourceKind kind)
        {
            var resolved = Resolve(rawPath, projectDir);
            if (resolved is null) return;
            if (IsGenerated(resolved)) return;            // /obj/ or *.g.cs — never surface
            var key = (resolved, line);
            if (!seen.Add(key)) return;                    // dedup by (path, line)
            result.Add(new FileRef(resolved, line, kind, File.Exists(resolved)));
        }

        // Stack-trace refs first, in trace order (top frame first).
        if (!string.IsNullOrEmpty(stackTrace))
            foreach (Match m in StackFrameRx.Matches(stackTrace))
                Add(m.Groups["path"].Value.Trim(), ParseLine(m.Groups["line"]), SourceKind.StackFrame);

        // Then message refs: MSBuild diagnostics, then bare source paths.
        if (!string.IsNullOrEmpty(message))
        {
            foreach (Match m in MsBuildRx.Matches(message))
                Add(m.Groups["path"].Value.Trim(), ParseLine(m.Groups["line"]), SourceKind.MsBuild);
            foreach (Match m in BarePathRx.Matches(message))
                Add(m.Groups["path"].Value.Trim(), ParseLine(m.Groups["line"]), SourceKind.Message);
        }

        return result;
    }

    private static int? ParseLine(Group g) =>
        g.Success && int.TryParse(g.Value, out var n) ? n : null;

    /// <summary>Resolve to a normalised absolute path (relative against <paramref name="projectDir"/>); null if unparseable.</summary>
    private static string? Resolve(string path, string projectDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(projectDir, path));
            return full;
        }
        catch (ArgumentException) { return null; }   // invalid path characters
        catch (PathTooLongException) { return null; }
    }

    private static bool IsGenerated(string path)
    {
        var norm = path.Replace('\\', '/');
        return norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || norm.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }
}
