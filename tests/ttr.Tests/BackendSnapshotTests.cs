using Ttr.Core;
using Xunit;
using static Ttr.Tests.TestKit;
using static VerifyXunit.Verifier;

namespace Ttr.Tests;

/// <summary>
/// The Phase 3 UX contract for the NEW states (brief M1), snapshotted fake-first via the
/// <c>backend</c> scenario before any real backend exists. Covers: project registration + detection,
/// the single-vs-multi TFM level, the build spinner, the build-failure node with parsed diagnostics,
/// the warning/error notice family (phantom, unparseable, unknown runner, dead MTP opt-in, dual-mode
/// info, and both smoke-validation failures), and the read-only "runs arrive in Phase 4" toast.
///
/// Phase 2 snapshots are unaffected (AC6): these use a distinct scenario and distinct node states.
/// </summary>
public class BackendSnapshotTests
{
    private static ConsoleKeyInfo Char(char c) => TestKit.Char(c);

    /// <summary>The whole read-only tree after discovery: every new node kind visible at once.</summary>
    [Fact]
    public Task Backend_overview() =>
        Verify(Render(PlayBackend(), 100, 40));

    /// <summary>A project mid-build shows a spinner + "building" in the counts gutter.</summary>
    [Fact]
    public Task Backend_building()
    {
        const string p1 = "matrix/XunitV2.Tests/XunitV2.Tests.csproj";
        const string p2 = "matrix/XunitV3.Tests/XunitV3.Tests.csproj";
        var s = Feed(AppState.Initial("backend", runsEnabled: false),
            new AppEvent.ProjectRegistered(p1, "XunitV2.Tests", ["net10.0"], RunnerKind.VsTest),
            new AppEvent.ProjectRegistered(p2, "XunitV3.Tests", ["net10.0", "net8.0"], RunnerKind.Mtp),
            new AppEvent.BuildStarted(p1));
        return Verify(Render(s, 100, 20));
    }

    /// <summary>Detail pane on the build-failed project: parsed diagnostics + raw output fallback.</summary>
    [Fact]
    public Task Backend_build_failure_detail()
    {
        var s = SelectByName(PlayBackend(), "Broken.Tests");
        return Verify(Render(Press(s, Char('s')), 100, 40));
    }

    /// <summary>Detail pane on a standalone diagnostic node (the phantom project).</summary>
    [Fact]
    public Task Backend_notice_detail()
    {
        var s = SelectByName(PlayBackend(), "Missing.Tests");
        return Verify(Render(Press(s, Char('s')), 100, 40));
    }

    /// <summary>Detail pane on the dead-MTP-opt-in project: the classification warning leads its detail.</summary>
    [Fact]
    public Task Backend_migration_warning_detail()
    {
        var s = SelectByName(PlayBackend(), "Migrating.Tests");
        return Verify(Render(Press(s, Char('s')), 100, 40));
    }

    /// <summary>r/R on a read-only session toasts "runs arrive in Phase 4" (brief AC7).</summary>
    [Fact]
    public Task Backend_rerun_toast() =>
        Verify(Render(Press(PlayBackend(), Char('R')), 100, 40));
}
