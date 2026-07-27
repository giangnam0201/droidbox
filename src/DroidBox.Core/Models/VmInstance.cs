namespace DroidBox.Core.Models;

public enum VmState
{
    Stopped,
    Starting,
    Running,
}

public sealed class VmInstance
{
    public required string Id { get; init; }
    public required string VersionId { get; init; }
    public required string OverlayPath { get; init; }
    public int AdbHostPort { get; init; }
    public int MonitorPort { get; init; }
    public VmState State { get; set; } = VmState.Stopped;
    public int? ProcessId { get; set; }

    /// <summary>True once a post-first-boot QEMU snapshot ("dbsnap") has been saved into the
    /// overlay disk. When true, subsequent starts pass -loadvm and resume near-instantly
    /// instead of re-running the whole cold boot (GRUB -> kernel -> Android init).</summary>
    public bool HasSnapshot { get; set; }
}
