using Ttr.Build;
using Ttr.Core;
using Xunit;

namespace Ttr.Tests;

/// <summary>The §6.2 V2 detection rule (settled by POC-3, hardened by POC-9), tested as a pure function
/// over evaluation signals — no MSBuild, no restore. One case per rule branch (brief M2).</summary>
public class DetectionTests
{
    private static EvalSignals Signals(
        bool isTestProject = true, bool isMtp = false,
        string[]? packages = null, (string, bool)[]? optIns = null)
        => new(isTestProject, isMtp,
            new HashSet<string>(packages ?? [], StringComparer.OrdinalIgnoreCase),
            (optIns ?? []).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void IsTestingPlatformApplication_is_authoritative_mtp()
    {
        var r = Detection.Detect(Signals(isMtp: true, packages: ["xunit.v3", "Microsoft.Testing.Platform"]));
        Assert.Equal(RunnerKind.Mtp, r.Runner);
        Assert.Null(r.Notice);
    }

    [Fact]
    public void Dual_mode_mtp_with_classic_adapter_is_informational_not_alarm()
    {
        var r = Detection.Detect(Signals(isMtp: true,
            packages: ["Microsoft.NET.Test.Sdk", "MSTest.TestAdapter", "MSTest.TestFramework"]));
        Assert.Equal(RunnerKind.Mtp, r.Runner);
        Assert.Equal(NoticeSeverity.Info, r.Notice!.Severity);
    }

    [Theory]
    [InlineData("xunit.runner.visualstudio")]
    [InlineData("NUnit3TestAdapter")]
    [InlineData("MSTest.TestAdapter")]
    public void TestSdk_plus_classic_adapter_is_vstest(string adapter)
    {
        var r = Detection.Detect(Signals(packages: ["Microsoft.NET.Test.Sdk", adapter]));
        Assert.Equal(RunnerKind.VsTest, r.Runner);
        Assert.Null(r.Notice);
    }

    [Fact]
    public void Dead_mtp_opt_in_on_classic_vstest_warns_incomplete_migration()
    {
        // POC-3's correction: UseMicrosoftTestingPlatformRunner=true but IsTestingPlatformApplication=false.
        var r = Detection.Detect(Signals(
            packages: ["Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio"],
            optIns: [("UseMicrosoftTestingPlatformRunner", true)]));
        Assert.Equal(RunnerKind.VsTest, r.Runner);
        Assert.Equal(NoticeSeverity.Warning, r.Notice!.Severity);
        Assert.Contains("migration", r.Notice.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_shaped_with_no_recognised_runner_is_unknown_and_warns()
    {
        var r = Detection.Detect(Signals(packages: ["SomeRandomPackage"]));
        Assert.Equal(RunnerKind.Unknown, r.Runner);
        Assert.Equal(NoticeSeverity.Warning, r.Notice!.Severity);
    }

    [Fact]
    public void Non_test_project_is_not_a_test()
    {
        var r = Detection.Detect(Signals(isTestProject: false, packages: ["Newtonsoft.Json"]));
        Assert.Equal(RunnerKind.NotATest, r.Runner);
        Assert.Null(r.Notice);
    }

    [Fact]
    public void Config_override_wins_outright()
    {
        var r = Detection.Detect(Signals(isMtp: true), configOverride: RunnerKind.VsTest);
        Assert.Equal(RunnerKind.VsTest, r.Runner);
        Assert.Equal(NoticeSeverity.Info, r.Notice!.Severity);
    }

    [Fact]
    public void Config_reader_matches_by_name_and_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ttrcfg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"),
                """{ "runnerOverrides": { "Foo.Tests": "vstest", "/abs/Bar.Tests/Bar.Tests.csproj": "mtp" } }""");
            var cfg = TtrConfig.Load(dir);
            Assert.Equal(RunnerKind.VsTest, cfg.OverrideFor("/x/Foo.Tests/Foo.Tests.csproj"));
            Assert.Equal(RunnerKind.Mtp, cfg.OverrideFor("/abs/Bar.Tests/Bar.Tests.csproj"));
            Assert.Null(cfg.OverrideFor("/x/Unlisted.Tests/Unlisted.Tests.csproj"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
