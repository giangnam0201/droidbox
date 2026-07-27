using System.Reflection;
using System.Text.Json;
using DroidBox.Core.Models;

namespace DroidBox.Core.Manifest;

public static class VersionManifest
{
    private const string ResourceName = "DroidBox.Core.Manifest.versions.json";

    public static IReadOnlyList<AndroidVersion> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        return Parse(stream);
    }

    public static IReadOnlyList<AndroidVersion> Parse(Stream json)
    {
        var versions = JsonSerializer.Deserialize<List<AndroidVersion>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (versions is null || versions.Count == 0)
            throw new InvalidOperationException("Version manifest is empty or invalid.");

        var duplicateId = versions.GroupBy(v => v.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
            throw new InvalidOperationException($"Duplicate version id in manifest: '{duplicateId.Key}'.");

        return versions;
    }

    public static IReadOnlyList<AndroidVersion> Parse(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Parse(stream);
    }
}
