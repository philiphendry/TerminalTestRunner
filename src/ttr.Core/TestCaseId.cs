using System.Security.Cryptography;
using System.Text;

namespace Ttr.Core;

/// <summary>
/// The stable, derived identity used for tree diffing, rerun selection, and --continue
/// matching (CLAUDE.md invariant 7, plan §5.1). Composed of adapter kind + project path +
/// TFM + fully-qualified name + a parameter-display hash so that theory rows — which share a
/// single non-unique FQN — get distinct ids. Platform-native ids are session-scoped and kept
/// only for execution; everything durable keys on this.
/// </summary>
public readonly record struct TestCaseId(string Value)
{
    /// <summary>Derives a leaf (test-case) id. <paramref name="paramDisplay"/> disambiguates theory rows.</summary>
    public static TestCaseId ForCase(
        string adapterKind, string projectPath, string tfm, string fullyQualifiedName, string? paramDisplay)
    {
        var paramHash = string.IsNullOrEmpty(paramDisplay) ? "" : ShortHash(paramDisplay);
        return new TestCaseId($"{adapterKind}|{projectPath}|{tfm}|{fullyQualifiedName}|{paramHash}");
    }

    /// <summary>
    /// Derives a structural (branch) node id from an ordered path of segment keys. Branch ids
    /// are prefixes of their leaf ids' hierarchy so the tree is addressable at every level.
    /// </summary>
    public static TestCaseId ForBranch(TestNodeKind kind, params string[] segments)
        => new($"{kind}::{string.Join("|", segments)}");

    /// <summary>Short, stable, content-based hash of a parameter display string.</summary>
    public static string ShortHash(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        // 8 hex chars is ample to disambiguate rows within a single method.
        return Convert.ToHexStringLower(hash[..4]);
    }

    public override string ToString() => Value;
}
