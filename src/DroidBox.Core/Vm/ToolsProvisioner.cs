using System.Diagnostics;
using System.IO.Compression;

namespace DroidBox.Core.Vm;

public sealed class ToolsProvisionException : Exception
{
    public ToolsProvisionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Downloads and installs QEMU + adb into PathConfig.ToolsDir the first time DroidBox needs
/// them, so the published app itself stays small instead of bundling ~200MB of QEMU binaries.
/// </summary>
public sealed class ToolsProvisioner
{
    // NSIS installer date-stamped filenames have no stable "latest" alias upstream, so this
    // needs bumping occasionally -- check https://qemu.weilnetz.de/w64/ for the current one.
    private const string QemuInstallerUrl = "https://qemu.weilnetz.de/w64/qemu-w64-setup-20260723.exe";
    private const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    private readonly HttpClient _http;

    public ToolsProvisioner(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public bool AreToolsPresent() =>
        File.Exists(PathConfig.QemuImgExe) &&
        File.Exists(PathConfig.QemuSystemExe) &&
        File.Exists(PathConfig.AdbExe);

    public async Task EnsureAllAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(PathConfig.ToolsDir);

        if (!File.Exists(PathConfig.QemuImgExe) || !File.Exists(PathConfig.QemuSystemExe))
            await InstallQemuAsync(status, ct);

        if (!File.Exists(PathConfig.AdbExe))
            await InstallAdbAsync(status, ct);
    }

    private async Task InstallQemuAsync(IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Downloading QEMU (one-time, ~200MB)...");
        var qemuDir = Path.Combine(PathConfig.ToolsDir, "qemu");
        var installerPath = Path.Combine(Path.GetTempPath(), "droidbox-qemu-setup.exe");

        await DownloadAsync(QemuInstallerUrl, installerPath, ct);

        status?.Report("Installing QEMU...");
        // NSIS's /D switch must be the last argument and MUST NOT be quoted, even though the
        // path can contain spaces -- so this is built as a raw Arguments string, not an
        // ArgumentList (which would auto-quote it and break NSIS's parser).
        var psi = new ProcessStartInfo(installerPath)
        {
            Arguments = $"/S /D={qemuDir}",
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new ToolsProvisionException("Failed to start the QEMU installer.");
        await process.WaitForExitAsync(ct);

        File.Delete(installerPath);

        if (!File.Exists(PathConfig.QemuImgExe) || !File.Exists(PathConfig.QemuSystemExe))
            throw new ToolsProvisionException(
                $"QEMU installer finished but qemu-img.exe/qemu-system-x86_64.exe still missing under '{qemuDir}'.");
    }

    private async Task InstallAdbAsync(IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Downloading Android platform-tools (adb)...");
        var zipPath = Path.Combine(Path.GetTempPath(), "droidbox-platform-tools.zip");

        await DownloadAsync(PlatformToolsUrl, zipPath, ct);

        status?.Report("Extracting adb...");
        ZipFile.ExtractToDirectory(zipPath, PathConfig.ToolsDir, overwriteFiles: true);
        File.Delete(zipPath);

        if (!File.Exists(PathConfig.AdbExe))
            throw new ToolsProvisionException($"platform-tools extracted but adb.exe not found at '{PathConfig.AdbExe}'.");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct);
    }
}
