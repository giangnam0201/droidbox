using DroidBox.Core.Manifest;

namespace DroidBox.Tests;

public class VersionManifestTests
{
    [Fact]
    public void LoadEmbedded_ReturnsNonEmptyList()
    {
        var versions = VersionManifest.LoadEmbedded();

        Assert.NotEmpty(versions);
    }

    [Fact]
    public void LoadEmbedded_ContainsAndroid71()
    {
        var versions = VersionManifest.LoadEmbedded();

        Assert.Contains(versions, v => v.Id == "7.1");
    }

    [Fact]
    public void LoadEmbedded_AllEntriesHaveRequiredFields()
    {
        var versions = VersionManifest.LoadEmbedded();

        foreach (var v in versions)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Id));
            Assert.False(string.IsNullOrWhiteSpace(v.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(v.GoldenImageUrl));
            Assert.True(v.RamMb > 0);
            Assert.True(v.DiskGb > 0);
        }
    }

    [Fact]
    public void Parse_EmptyArray_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => VersionManifest.Parse("[]"));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => VersionManifest.Parse("not json"));
    }

    [Fact]
    public void Parse_DuplicateIds_Throws()
    {
        const string json = """
        [
          { "Id": "7.1", "DisplayName": "A", "GoldenImageUrl": "http://x", "GoldenImageSha256": "" },
          { "Id": "7.1", "DisplayName": "B", "GoldenImageUrl": "http://y", "GoldenImageSha256": "" }
        ]
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => VersionManifest.Parse(json));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void Parse_ValidJson_UsesProvidedDefaultsWhenOmitted()
    {
        const string json = """
        [
          { "Id": "6.0", "DisplayName": "Android 6.0", "GoldenImageUrl": "http://x", "GoldenImageSha256": "" }
        ]
        """;

        var versions = VersionManifest.Parse(json);

        var v = Assert.Single(versions);
        Assert.Equal("6.0", v.Id);
        Assert.Equal(1024, v.RamMb); // default
        Assert.Equal(8, v.DiskGb);   // default
    }
}
