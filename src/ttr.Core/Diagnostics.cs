namespace Ttr.Core;

/// <summary>Severity of a <see cref="NodeNotice"/> — drives its glyph and colour (brief M1).</summary>
public enum NoticeSeverity
{
    /// <summary>Non-alarming note (e.g. dual-mode "VSTest also available"); does not override the node glyph.</summary>
    Info,

    /// <summary>Something the user should see but isn't fatal (phantom project, unknown runner, dead MTP opt-in, zero tests).</summary>
    Warning,

    /// <summary>A hard problem for this node (unparseable solution entry, MTP host never handshook).</summary>
    Error,
}

/// <summary>
/// A diagnostic attached to a tree node (brief M1, plan §4/§6.2). Carried by
/// <see cref="AppEvent.ProjectRegistered"/> (classification notes on a project),
/// <see cref="AppEvent.NoticeRaised"/> (a standalone <see cref="TestNodeKind.Notice"/> node for a
/// phantom / unparseable solution entry) and <see cref="AppEvent.DiscoveryFailed"/> (a smoke-validation
/// warning child). The detail pane shows <see cref="Detail"/> below the summary.
/// </summary>
public sealed record NodeNotice(NoticeSeverity Severity, string Summary, string? Detail = null);

/// <summary>
/// The per-(project,TFM) test platform, settled by the §6.2 detection rule. <see cref="Unknown"/> is a
/// test-shaped project with no recognised runner; <see cref="NotATest"/> is a non-test project (never
/// shown in the tree). <see cref="Fake"/> is the in-process fake adapter.
/// </summary>
public enum RunnerKind
{
    Fake,
    VsTest,
    Mtp,
    Unknown,
    NotATest,
}

/// <summary>Build lifecycle of a project node (plan §7, brief M1). Drives the build spinner / failure glyph.</summary>
public enum BuildPhase
{
    /// <summary>Not building (freshly registered, or up to date and skipped).</summary>
    None,

    /// <summary>A build is in flight — the project node shows a spinner.</summary>
    Building,

    /// <summary>Build succeeded; discovery may proceed.</summary>
    Built,

    /// <summary>Build failed — red project node whose detail pane shows the diagnostics + raw output.</summary>
    Failed,
}

/// <summary>
/// One parsed build diagnostic (plan §7). The canonical MSBuild form is
/// <c>path(line,col): error CODE: message</c>; <see cref="Severity"/> is <c>error</c> or <c>warning</c>.
/// Diagnostics carry file paths, so the 'o' modal works on build errors too (the reducer derives the
/// failed node's file references from the raw build output).
/// </summary>
public sealed record BuildDiagnostic(
    string File, int Line, int Column, string Code, string Message, string Severity = "error")
{
    public override string ToString() => $"{File}({Line},{Column}): {Severity} {Code}: {Message}";
}
