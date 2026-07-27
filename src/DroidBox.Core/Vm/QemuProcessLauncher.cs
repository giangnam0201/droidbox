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
    /// Starts a QEMU process booting the given overlay disk. Tries WHPX hardware acceleration
    /// first and falls back to software emulation (TCG) if WHPX isn't available on this machine
    /// (e.g. the Windows Hypervisor Platform optional feature isn't enabled) instead of hard
    /// failing with a silent crash. The overlay's backing golden image already has the Android
    /// setup wizard disabled and boot animations/services trimmed, so this reaches the home
    /// screen in seconds under WHPX (much slower under the TCG fallback).
    /// </summary>
    public static Process Start(VmInstance vm, AndroidVersion version, Action<string>? onOutputLine = null)
    {
        if (!File.Exists(PathConfig.QemuSystemExe))
            throw new QemuLaunchException($"qemu-system-x86_64.exe not found at '{PathConfig.QemuSystemExe}'.");

        var args = new List<string>
        {
            "-M", "pc",
            "-accel", "whpx:tcg",
            "-cpu", "max",
            "-m", version.RamMb.ToString(),
            "-smp", "2",
            "-drive", $"file={vm.OverlayPath},if=virtio,format=qcow2",
            "-netdev", $"user,id=net0,hostfwd=tcp::{vm.AdbHostPort}-:5555",
            "-device", "virtio-net-pci,netdev=net0",
            // std VGA (not virtio-vga) -- android-x86's kernel driver support for virtio-gpu is
            // unreliable and boots to a black screen even when the VM is otherwise running fine.
            "-vga", "std",
            "-display", "sdl",
            "-name", $"DroidBox - {version.DisplayName} ({vm.Id})",
        };

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
    /// Creates a copy-on-write overlay disk backed by the golden image. This is the "instant
    /// create" step: it writes only a small delta file, not a full disk copy.
    /// </summary>
    public static async Task CreateOverlayAsync(string goldenImagePath, string overlayPath, CancellationToken ct = default)
    {
        if (!File.Exists(PathConfig.QemuImgExe))
            throw new QemuLaunchException($"qemu-img.exe not found at '{PathConfig.QemuImgExe}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);

        var psi = new ProcessStartInfo(PathConfig.QemuImgExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("create");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("qcow2");
        psi.ArgumentList.Add("-F");
        psi.ArgumentList.Add("qcow2");
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add(goldenImagePath);
        psi.ArgumentList.Add(overlayPath);

        using var process = Process.Start(psi) ?? throw new QemuLaunchException("Failed to start qemu-img.");
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new QemuLaunchException($"qemu-img create failed (exit {process.ExitCode}): {stderr}");
    }
}
