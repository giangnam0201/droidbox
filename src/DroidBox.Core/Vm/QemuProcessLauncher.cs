using System.Diagnostics;
using DroidBox.Core.Models;

namespace DroidBox.Core.Vm;

public sealed class QemuLaunchException : Exception
{
    public QemuLaunchException(string message) : base(message) { }
}

public static class QemuProcessLauncher
{
    /// <summary>
    /// Starts a QEMU process booting the given VM's disk. Tries WHPX hardware acceleration
    /// first and falls back to software emulation (TCG) if WHPX isn't available on this machine
    /// (e.g. the Windows Hypervisor Platform optional feature isn't enabled) instead of hard
    /// failing with a silent crash.
    /// </summary>
    public static Process Start(VmInstance vm, AndroidVersion version, Action<string>? onOutputLine = null)
    {
        if (!File.Exists(PathConfig.QemuSystemExe))
            throw new QemuLaunchException($"qemu-system-x86_64.exe not found at '{PathConfig.QemuSystemExe}'.");

        var args = new List<string>
        {
            "-M", "pc",
            // Separate -accel flags, tried in order -- "whpx:tcg" as one value is invalid
            // syntax (confirmed via the app's log panel: "invalid accelerator whpx:tcg").
            "-accel", "whpx",
            "-accel", "tcg",
            "-cpu", "max",
            "-m", version.RamMb.ToString(),
            "-smp", "2",
            "-drive", $"file={vm.DiskPath},if=virtio,format=qcow2",
            "-netdev", $"user,id=net0,hostfwd=tcp::{vm.AdbHostPort}-:5555",
            "-device", "virtio-net-pci,netdev=net0",
            // std VGA (not virtio-vga) -- android-x86's kernel driver support for virtio-gpu is
            // unreliable and boots to a black screen even when the VM is otherwise running fine.
            "-vga", "std",
            "-display", "sdl",
            "-monitor", $"tcp:127.0.0.1:{vm.MonitorPort},server,nowait",
            "-name", $"DroidBox - {version.DisplayName} ({vm.Id})",
        };

        // Resume from the saved post-first-boot snapshot instead of cold-booting again --
        // this is what makes every start after the first one near-instant.
        if (vm.HasSnapshot)
        {
            args.Add("-loadvm");
            args.Add(QemuMonitorClient.SnapshotTag);
        }

        var startInfo = new ProcessStartInfo(PathConfig.QemuSystemExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
            startInfo.ArgumentList.Add(a);

        var process = Process.Start(startInfo)
            ?? throw new QemuLaunchException("Failed to start QEMU process.");

        if (onOutputLine is not null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    /// <summary>
    /// Creates this VM's own private disk as a RAW copy of the golden image -- deliberately
    /// not a copy-on-write overlay. QEMU's internal snapshots are only resolved against the
    /// top/active image being booted, never through a backing-file chain (verified empirically:
    /// a COW overlay backed by a snapshotted golden image gets "Snapshot 'x' does not exist"
    /// from -loadvm, but a raw file copy of that same golden image loads it fine, because it's
    /// a real standalone file with its own snapshot table). Trades instant (near-0-byte) create
    /// for a real disk-sized copy, in exchange for every VM -- including its very first boot --
    /// being able to resume instantly if the golden image has a baked-in snapshot.
    /// </summary>
    public static async Task CreateVmDiskAsync(string goldenImagePath, string diskPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);

        await using (var source = File.OpenRead(goldenImagePath))
        await using (var target = File.Create(diskPath))
        {
            await source.CopyToAsync(target, ct);
        }
    }

    /// <summary>Whether a qcow2 image has an internal snapshot tagged
    /// <see cref="QemuMonitorClient.SnapshotTag"/> -- used to tell a fresh VM disk copied from
    /// the golden image that it can -loadvm immediately, without ever cold-booting.</summary>
    public static async Task<bool> HasEmbeddedSnapshotAsync(string qcow2Path, CancellationToken ct = default)
    {
        if (!File.Exists(PathConfig.QemuImgExe))
            throw new QemuLaunchException($"qemu-img.exe not found at '{PathConfig.QemuImgExe}'.");

        var psi = new ProcessStartInfo(PathConfig.QemuImgExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("snapshot");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(qcow2Path);

        using var process = Process.Start(psi) ?? throw new QemuLaunchException("Failed to start qemu-img.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return stdout.Contains(QemuMonitorClient.SnapshotTag, StringComparison.Ordinal);
    }
}
