namespace Ttr.Core;

/// <summary>
/// Everything needed to place a test in the tree, carried by every event that can introduce
/// a node (discovery AND run events — so a theory case first seen via a run event, the 1→N
/// shape from CLAUDE.md invariant 7, can still be inserted). A null <see cref="CaseDisplay"/>
/// means the leaf is the Method node itself (a plain test); a non-null value means the leaf is
/// a Case node under that Method (one theory row).
/// </summary>
public sealed record TestIdentity(
    TestCaseId Id,
    string Project,
    string Tfm,
    string Namespace,
    string ClassName,
    string Method,
    string? CaseDisplay = null,
    string? SourceFile = null,
    int? SourceLine = null)
{
    /// <summary>Human-facing label for the leaf node.</summary>
    public string LeafLabel => CaseDisplay ?? Method;

    /// <summary>True when this identity denotes a theory row (a Case leaf under a Method branch).</summary>
    public bool IsCase => CaseDisplay is not null;
}
