using System.Security.Cryptography;
using DroidBox.Core.Models;

namespace DroidBox.Core.Vm;

public sealed class GoldenImageStore
{
    private readonly HttpClient _http;

    public GoldenImageStore(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string PathFor(AndroidVersion version) =>
        Path.Combine(PathConfig.GoldenImagesDir, $"{version.Id}.qcow2");

    public bool IsCached(AndroidVersion version) => File.Exists(PathFor(version));

    public async Task<string> EnsureAvailableAsync(
        AndroidVersion version,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(PathConfig.GoldenImagesDir);
        var destination = PathFor(version);

        if (File.Exists(destination) && await MatchesHashAsync(destination, version.GoldenImageSha256, ct))
            return destination;

        var tempPath = destination + ".download";
        using (var response = await _http.GetAsync(version.GoldenImageUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(tempPath);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                    progress?.Report((double)read / total.Value);
            }
        }

        if (!string.IsNullOrEmpty(version.GoldenImageSha256) &&
            !await MatchesHashAsync(tempPath, version.GoldenImageSha256, ct))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException(
                $"Downloaded golden image for '{version.Id}' failed SHA-256 verification.");
        }

        File.Move(tempPath, destination, overwrite: true);
        return destination;
    }

    private static async Task<bool> MatchesHashAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha256))
            return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var hex = Convert.ToHexString(hash);
        return string.Equals(hex, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
