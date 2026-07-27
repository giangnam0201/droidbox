using System.Net;
using System.Security.Cryptography;
using System.Text;
using DroidBox.Core.Models;
using DroidBox.Core.Vm;

namespace DroidBox.Tests;

public class GoldenImageStoreTests
{
    private static AndroidVersion MakeVersion(string id, string sha256 = "") => new()
    {
        Id = id,
        DisplayName = $"Android {id}",
        GoldenImageUrl = $"https://example.invalid/{id}.qcow2",
        GoldenImageSha256 = sha256,
    };

    [Fact]
    public void PathFor_UsesVersionIdAsFileName()
    {
        var store = new GoldenImageStore();
        var version = MakeVersion("7.1");

        var path = store.PathFor(version);

        Assert.EndsWith("7.1.qcow2", path);
    }

    [Fact]
    public void IsCached_FalseWhenFileMissing()
    {
        var store = new GoldenImageStore();
        var version = MakeVersion("does-not-exist-" + Guid.NewGuid());

        Assert.False(store.IsCached(version));
    }

    [Fact]
    public async Task EnsureAvailableAsync_DownloadsAndVerifiesHash()
    {
        var content = "fake-golden-image-bytes"u8.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(content));
        var version = MakeVersion("test-" + Guid.NewGuid().ToString("N")[..8], sha256);

        var handler = new StubHandler(content);
        var store = new GoldenImageStore(new HttpClient(handler));

        try
        {
            var path = await store.EnsureAvailableAsync(version);

            Assert.True(File.Exists(path));
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(store.PathFor(version)))
                File.Delete(store.PathFor(version));
        }
    }

    [Fact]
    public async Task EnsureAvailableAsync_ThrowsOnHashMismatch()
    {
        var content = "fake-golden-image-bytes"u8.ToArray();
        var version = MakeVersion("test-" + Guid.NewGuid().ToString("N")[..8], "0000000000000000000000000000000000000000000000000000000000000");

        var handler = new StubHandler(content);
        var store = new GoldenImageStore(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureAvailableAsync(version));
    }

    private sealed class StubHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            return Task.FromResult(response);
        }
    }
}
