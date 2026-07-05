namespace Ttr.Core;

/// <summary>
/// The rich outcome detail an adapter attaches to a finished test (plan §11.2 detail pane): the
/// failure message, the exception type chain, the multi-line stack trace, and captured stdout.
/// Immutable; carried on <see cref="AppEvent.TestFinished"/> and stored on the leaf
/// <see cref="TestNode"/>. The reducer runs <c>TtrParser</c> over <see cref="Message"/> +
/// <see cref="StackTrace"/> to derive the node's ordered file references (brief M1).
/// </summary>
public sealed record TestResultDetail(
    string? Message = null,
    IReadOnlyList<string>? ExceptionChain = null,
    string? StackTrace = null,
    string? StandardOutput = null)
{
    public static readonly TestResultDetail Empty = new();

    public bool IsEmpty =>
        string.IsNullOrEmpty(Message)
        && (ExceptionChain is null || ExceptionChain.Count == 0)
        && string.IsNullOrEmpty(StackTrace)
        && string.IsNullOrEmpty(StandardOutput);

    // The auto-generated record equality compares ExceptionChain by reference; make it structural so
    // two scenario builds with the same seed produce equal details (FakeAdapter determinism tests).
    public bool Equals(TestResultDetail? other) =>
        other is not null
        && Message == other.Message
        && StackTrace == other.StackTrace
        && StandardOutput == other.StandardOutput
        && ChainEqual(ExceptionChain, other.ExceptionChain);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Message);
        hash.Add(StackTrace);
        hash.Add(StandardOutput);
        if (ExceptionChain is not null)
            foreach (var e in ExceptionChain)
                hash.Add(e);
        return hash.ToHashCode();
    }

    private static bool ChainEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ca = a?.Count ?? 0;
        var cb = b?.Count ?? 0;
        if (ca != cb) return false;
        for (var i = 0; i < ca; i++)
            if (a![i] != b![i]) return false;
        return true;
    }
}
