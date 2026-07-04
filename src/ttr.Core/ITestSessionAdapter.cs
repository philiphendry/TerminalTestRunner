using System.Threading.Channels;

namespace Ttr.Core;

/// <summary>
/// A test target (project, TFM). Phase 1 uses a minimal shape; real targets (resolved from
/// solutions/projects with output paths) arrive in Phase 3.
/// </summary>
public sealed record TestTarget(string Name, string ProjectPath = "", string Tfm = "");

/// <summary>
/// The single seam behind which all test-platform specifics live (CLAUDE.md invariant 6,
/// plan §6.1). One instance per (project, TFM); the orchestrator fans out and merges. In
/// Phase 1 the Fake adapter is the only implementation. Cancellation must map to a real
/// platform cancel for real adapters — not merely abandoning the task.
/// </summary>
public interface ITestSessionAdapter : IAsyncDisposable
{
    /// <summary>Streams <see cref="AppEvent.TestsDiscovered"/> batches as tests are found.</summary>
    Task DiscoverAsync(TestTarget target, ChannelWriter<AppEvent> events, CancellationToken ct);

    /// <summary>
    /// Runs the given subset (empty = all); streams <see cref="AppEvent.TestStarted"/> /
    /// <see cref="AppEvent.TestFinished"/> and a final <see cref="AppEvent.RunCompleted"/>.
    /// </summary>
    Task RunAsync(TestTarget target, IReadOnlyList<TestCaseId> subset,
        ChannelWriter<AppEvent> events, CancellationToken ct);
}
