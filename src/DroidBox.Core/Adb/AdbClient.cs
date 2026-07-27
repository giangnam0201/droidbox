using System.Diagnostics;

namespace DroidBox.Core.Adb;

public sealed class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}

public sealed class AdbClient
{
    private readonly string _adbExePath;

    public AdbClient(string adbExePath)
    {
        _adbExePath = adbExePath;
    }

    public async Task InstallApkAsync(int hostPort, string apkPath, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath))
            throw new AdbException($"APK not found: '{apkPath}'.");

        var serial = $"127.0.0.1:{hostPort}";
        await RunAsync(["connect", serial], ct);
        await RunAsync(["-s", serial, "install", "-r", apkPath], ct);
    }

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        if (!File.Exists(_adbExePath))
            throw new AdbException($"adb.exe not found at '{_adbExePath}'.");

        var psi = new ProcessStartInfo(_adbExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new AdbException("Failed to start adb.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new AdbException($"adb {string.Join(' ', args)} failed: {stdout}\n{stderr}");
    }
}
