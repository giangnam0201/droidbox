using Microsoft.Win32;

namespace DroidBox.Core.Vm;

public static class HypervisorCheck
{
    /// <summary>
    /// Best-effort, non-elevated check for whether Windows Hypervisor Platform (WHPX) is
    /// likely enabled. Not authoritative — the definitive signal is whether QEMU itself
    /// accepts "-accel whpx" at launch, which QemuProcessLauncher surfaces as a QemuLaunchException.
    /// </summary>
    public static bool WhpxLikelyEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\whvsvc");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public const string EnableInstructions =
        "Windows Hypervisor Platform is required for accelerated Android VMs.\n" +
        "Enable it from an elevated PowerShell prompt, then reboot:\n" +
        "  Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform";
}
