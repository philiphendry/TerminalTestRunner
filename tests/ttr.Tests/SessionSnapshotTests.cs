using Ttr.Core;
using Ttr.Runners;
using Xunit;
using static Ttr.Tests.TestKit;
using static VerifyXunit.Verifier;

namespace Ttr.Tests;

/// <summary>
/// The Phase 6 session UX contract (brief M1), snapshotted fake-first: a restored tree with mixed fresh/Stale
/// results (dim ✓/✗), the restored-session header notice, the whole subtree going Stale after a simulated
/// rebuild, and the targets-changed degrade toast. These render only in restore/stale situations, so every
/// prior-phase snapshot stays byte-identical (AC8) — verified by the untouched existing suites.
/// </summary>
public class SessionSnapshotTests
{
    private const string Proj = FakeContinueScript.Project;

    /// <summary>Restored tree: Add/Divide fresh green, Subtract fresh red (detail pane shows it), the
    /// StringTests subtree dimmed Stale — plus the "restored N results from …" header notice.</summary>
    [Fact]
    public Task Continue_restored_mixed_fresh_and_stale() => Verify(Render(ContinueRestored(), 120, 20));

    /// <summary>The same restore at a narrower width to pin the header notice truncation behaviour.</summary>
    [Fact]
    public Task Continue_restored_notice_narrow() => Verify(Render(ContinueRestored(), 80, 16));

    /// <summary>A simulated rebuild (re-discovery cycle) flips every kept result to Stale — the whole tree dims
    /// until a rerun would replace it (AC2, fake-first).</summary>
    [Fact]
    public Task Continue_rebuild_flips_subtree_stale()
    {
        var s = Feed(ContinueRestored(),
            new AppEvent.RediscoveryStarted([Proj]),
            new AppEvent.TestsDiscovered(FakeContinueScript.Initial),
            new AppEvent.RediscoveryCompleted([Proj]));
        return Verify(Render(s, 120, 20));
    }

    /// <summary>The targets-changed degrade: a fresh discovered tree (no restore) with the specific toast (AC4).</summary>
    [Fact]
    public Task Continue_targets_changed_degrade()
    {
        var s = Feed(ContinueBase(), new AppEvent.Toast("targets changed since last session — starting fresh"));
        return Verify(Render(s, 100, 16));
    }
}
