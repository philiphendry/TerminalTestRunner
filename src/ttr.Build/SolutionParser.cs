using System.Xml;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Ttr.Build;

/// <summary>Why a solution file could not be parsed (plan §4 — the exceptions are NOT uniform, POC-7).</summary>
public enum SolutionErrorKind
{
    /// <summary>Malformed <c>.sln</c> — the library's <c>SolutionException</c>.</summary>
    Malformed,

    /// <summary>Malformed <c>.slnx</c> — a raw, unwrapped <c>System.Xml.XmlException</c> (NOT wrapped).</summary>
    MalformedXml,

    /// <summary>The solution file does not exist.</summary>
    NotFound,

    /// <summary>Unrecognised extension — <c>GetSerializerByMoniker</c> returned null.</summary>
    UnsupportedExtension,
}

/// <summary>A solution that could not be parsed, with a user-facing message and its category.</summary>
public sealed record SolutionError(SolutionErrorKind Kind, string Message);

/// <summary>A project referenced by the solution whose file does not exist on disk (plan §4).</summary>
public sealed record PhantomProject(string Name, string ResolvedPath);

/// <summary>
/// The outcome of parsing a solution: the resolved, existing project files; the phantom references; and
/// (when the file itself could not be parsed) a categorised <see cref="Error"/>. "Loaded ≠ intact": the
/// <c>.sln</c> parser silently accepts truncated files, so a successful parse with a short project list is
/// still returned as data, never an exception (plan §4).
/// </summary>
public sealed record SolutionParseResult(
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<PhantomProject> Phantoms,
    SolutionError? Error)
{
    public static SolutionParseResult Failed(SolutionErrorKind kind, string message) =>
        new([], [], new SolutionError(kind, message));
}

/// <summary>
/// Parses <c>.sln</c> and <c>.slnx</c> with <c>Microsoft.VisualStudio.SolutionPersistence</c> (pinned
/// 1.0.52), applying the plan §4 error contract (the four distinct failure categories), resolving project
/// paths (<c>Path.GetFullPath(Path.Combine(dir, filePath))</c>, backslash-tolerant on Linux), and running
/// an explicit <c>File.Exists</c> pass so phantom projects become warning nodes rather than silent omissions.
/// Project kinds are read from the <c>TypeId</c> GUID, never the (blank-by-design) <c>Type</c> string.
/// </summary>
public static class SolutionParser
{
    private static readonly string[] BuildableExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static async Task<SolutionParseResult> ParseAsync(string solutionPath, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(solutionPath);
        if (!File.Exists(full))
            return SolutionParseResult.Failed(SolutionErrorKind.NotFound, $"solution not found: {full}");

        ISolutionSerializer? serializer;
        try
        {
            serializer = SolutionSerializers.GetSerializerByMoniker(full);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        {
            return SolutionParseResult.Failed(SolutionErrorKind.UnsupportedExtension,
                $"unsupported solution format: {Path.GetFileName(full)}");
        }
        if (serializer is null)
            return SolutionParseResult.Failed(SolutionErrorKind.UnsupportedExtension,
                $"unsupported solution format: {Path.GetFileName(full)}");

        try
        {
            var model = await serializer.OpenAsync(full, ct).ConfigureAwait(false);
            return Resolve(full, model);
        }
        catch (SolutionException ex)
        {
            return SolutionParseResult.Failed(SolutionErrorKind.Malformed,
                $"malformed solution: {ex.Message}");
        }
        catch (XmlException ex)   // .slnx malformed XML is raw/unwrapped (POC-7)
        {
            return SolutionParseResult.Failed(SolutionErrorKind.MalformedXml,
                $"malformed .slnx (XML): {ex.Message}");
        }
        catch (FileNotFoundException)
        {
            return SolutionParseResult.Failed(SolutionErrorKind.NotFound, $"solution not found: {full}");
        }
    }

    private static SolutionParseResult Resolve(string solutionPath, dynamic model)
    {
        var dir = Path.GetDirectoryName(solutionPath) ?? ".";
        var projects = new List<string>();
        var phantoms = new List<PhantomProject>();

        foreach (var project in model.SolutionProjects)
        {
            string filePath = project.FilePath;
            if (string.IsNullOrWhiteSpace(filePath)) continue;

            // FilePath may be backslash-authored; normalise so it resolves on Linux too (POC-7).
            var resolved = Path.GetFullPath(Path.Combine(dir, filePath.Replace('\\', '/')));
            var ext = Path.GetExtension(resolved);
            if (Array.IndexOf(BuildableExtensions, ext.ToLowerInvariant()) < 0)
                continue;   // shared/other project kinds are not independently buildable test targets

            if (File.Exists(resolved))
                projects.Add(resolved);
            else
                phantoms.Add(new PhantomProject(Path.GetFileNameWithoutExtension(resolved), resolved));
        }

        return new SolutionParseResult(projects, phantoms, Error: null);
    }
}
