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

    /// <summary>Runs a shell command on the device and returns its stdout, or null if adb
    /// isn't reachable yet (e.g. Android hasn't finished booting far enough to accept
    /// connections) -- callers polling for boot state should treat that as "not ready" rather
    /// than a hard failure.</summary>
    public async Task<string?> TryShellAsync(int hostPort, string command, CancellationToken ct = default)
    {
        var serial = $"127.0.0.1:{hostPort}";
        try
        {
            await RunAsync(["connect", serial], ct);
            var (stdout, exitCode) = await RunCapturingAsync(["-s", serial, "shell", command], ct);
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch (AdbException)
        {
            return null;
        }
    }

    private Task RunAsync(IEnumerable<string> args, CancellationToken ct) => RunCapturingAsync(args, ct, throwOnError: true);

    private async Task<(string Stdout, int ExitCode)> RunCapturingAsync(
        IEnumerable<string> args, CancellationToken ct, bool throwOnError = false)
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

        if (throwOnError && process.ExitCode != 0)
            throw new AdbException($"adb {string.Join(' ', args)} failed: {stdout}\n{stderr}");

        return (stdout, process.ExitCode);
    }
}
