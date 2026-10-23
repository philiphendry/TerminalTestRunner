using Ttr.Build;
using Ttr.Core;
using Xunit;

namespace Ttr.Tests;

/// <summary>Transitive package ids from <c>project.assets.json</c> (plan §6.2) — pure JSON parsing, no restore.</summary>
public class AssetsFileTests
{
    // Trimmed from a real v4 assets file: a project referencing only the MSTest metapackage.
    private const string MsTestMetapackageV4 = """
        {
          "version": 4,
          "targets": {
            "net9.0-windows10.0.26100.0": {
              "MSTest/3.8.3": { "type": "package" },
              "MSTest.TestAdapter/3.8.3": { "type": "package" },
              "MSTest.TestFramework/3.8.3": { "type": "package" },
              "Microsoft.NET.Test.Sdk/17.13.0": { "type": "package" },
              "QuickWindowsUI/1.0.0": { "type": "project" }
            },
            "net9.0-windows10.0.26100.0/win-x64": {
              "MSTest/3.8.3": { "type": "package" }
            }
          },
          "libraries": {
            "MSTest/3.8.3": { "type": "package" },
            "MSTest.TestAdapter/3.8.3": { "type": "package" },
            "QuickWindowsUI/1.0.0": { "type": "project" }
          }
        }
        """;

    [Fact]
    public void V4_reads_the_matching_tfm_closure_and_skips_project_refs()
    {
        var ids = AssetsFile.ParsePackageIds(MsTestMetapackageV4, "net9.0-windows10.0.26100.0");
        Assert.Contains("mstest.testadapter", ids);
        Assert.Contains("Microsoft.NET.Test.Sdk", ids);
        Assert.DoesNotContain("QuickWindowsUI", ids);
    }

    [Fact]
    public void V4_does_not_leak_packages_from_another_tfm()
    {
        const string json = """
            {
              "targets": {
                "net8.0": { "xunit.runner.visualstudio/2.8.2": { "type": "package" } },
                "net9.0": { "NUnit3TestAdapter/4.6.0": { "type": "package" } }
              },
              "libraries": {}
            }
            """;
        var ids = AssetsFile.ParsePackageIds(json, "net9.0");
        Assert.Contains("NUnit3TestAdapter", ids);
        Assert.DoesNotContain("xunit.runner.visualstudio", ids);
    }

    [Fact]
    public void V3_framework_keyed_targets_fall_back_to_libraries()
    {
        const string json = """
            {
              "version": 3,
              "targets": { ".NETCoreApp,Version=v8.0": {} },
              "libraries": { "xunit.runner.visualstudio/2.8.2": { "type": "package" } }
            }
            """;
        Assert.Contains("xunit.runner.visualstudio", AssetsFile.ParsePackageIds(json, "net8.0"));
    }

    [Fact]
    public void Missing_assets_file_is_empty_not_an_error()
        => Assert.Empty(AssetsFile.ReadPackageIds(Path.Combine(Path.GetTempPath(), "no-such", "project.assets.json"), "net9.0"));

    [Fact]
    public void MSTest_metapackage_closure_classifies_as_vstest()
    {
        // The regression: only "MSTest" is a direct PackageReference; the adapter + Test.Sdk are transitive.
        var packages = new HashSet<string>(["MSTest", "Moq"], StringComparer.OrdinalIgnoreCase);
        packages.UnionWith(AssetsFile.ParsePackageIds(MsTestMetapackageV4, "net9.0-windows10.0.26100.0"));
        var r = Detection.Detect(new EvalSignals(true, false, packages, new Dictionary<string, bool>()));
        Assert.Equal(RunnerKind.VsTest, r.Runner);
    }
}
