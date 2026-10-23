using System.Text.Json;

namespace Ttr.Build;

/// <summary>
/// Reads the resolved (transitive) package ids from NuGet's <c>project.assets.json</c> (plan §6.2). Detection
/// needs these because the classic adapters often arrive transitively — e.g. the <c>MSTest</c> metapackage
/// pulls in <c>Microsoft.NET.Test.Sdk</c> + <c>MSTest.TestAdapter</c>, neither of which appears as a direct
/// <c>PackageReference</c> item. PURE (no MSBuild types) so it is unit-tested from inline JSON.
/// </summary>
public static class AssetsFile
{
    /// <summary>Package ids resolved for <paramref name="tfm"/>, or empty if the file is missing/unreadable.</summary>
    public static IReadOnlySet<string> ReadPackageIds(string? assetsPath, string tfm)
    {
        if (string.IsNullOrEmpty(assetsPath) || !File.Exists(assetsPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return ParsePackageIds(File.ReadAllText(assetsPath), tfm);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // best-effort: direct refs still apply
        }
    }

    /// <summary>
    /// Assets v4 keys <c>targets</c> by the TFM alias (<c>net9.0</c>, <c>net9.0/win-x64</c>), so the TFM's own
    /// closure is used when a key matches. Older v3 files key by framework name (<c>.NETCoreApp,Version=v9.0</c>)
    /// — no alias to match — so fall back to the cross-TFM <c>libraries</c> union.
    /// </summary>
    public static IReadOnlySet<string> ParsePackageIds(string json, string tfm)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var matchedTarget = false;
        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
        {
            foreach (var target in targets.EnumerateObject())
            {
                if (!string.Equals(target.Name, tfm, StringComparison.OrdinalIgnoreCase)
                    && !target.Name.StartsWith(tfm + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                matchedTarget = true;
                AddPackages(target.Value, ids);
            }
        }

        if (!matchedTarget && root.TryGetProperty("libraries", out var libraries))
            AddPackages(libraries, ids);

        return ids;
    }

    /// <summary>Add each <c>"Id/Version": { "type": "package" }</c> entry's id (project refs are skipped).</summary>
    private static void AddPackages(JsonElement section, HashSet<string> ids)
    {
        if (section.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object
                && entry.Value.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() != "package")
                continue;
            var slash = entry.Name.IndexOf('/');
            ids.Add(slash < 0 ? entry.Name : entry.Name[..slash]);
        }
    }
}
