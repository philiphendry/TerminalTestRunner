namespace TtrParser;

/// <summary>Where a <see cref="FileRef"/> was extracted from (affects nothing functionally; kept for diagnostics/tests).</summary>
public enum SourceKind
{
    /// <summary>A stack-trace frame of the canonical <c> in &lt;path&gt;:line &lt;N&gt;</c> form.</summary>
    StackFrame,

    /// <summary>An MSBuild diagnostic of the <c>path(line,col): error CODE:</c> form.</summary>
    MsBuild,

    /// <summary>A bare path found in a failure message.</summary>
    Message,
}

/// <summary>
/// An ordered, de-duplicated reference to a source file extracted from failure text (plan §11.5,
/// POC-9). <see cref="Path"/> is the resolved absolute path; <see cref="Line"/> is 1-based when the
/// source carried one. <see cref="Exists"/> is the filesystem existence check result — unresolved
/// references (framework/source-linked frames pointing at files not on disk) are kept in order with
/// <see cref="Exists"/> = false so the 'o' modal can traverse and dim them (plan §11.5, brief M5).
/// </summary>
public sealed record FileRef(string Path, int? Line, SourceKind Kind, bool Exists)
{
    /// <summary>The file name alone (for the modal title).</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}
