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
    /// <summary>This VM's own private disk -- a raw copy of the golden image, not a
    /// copy-on-write overlay. QEMU's internal (qcow2) snapshots are only resolved against the
    /// top/active image being booted, not walked through a backing-file chain (verified
    /// empirically), so a COW overlay backed by the golden image can never see a snapshot
    /// baked into that golden image. A raw copy can, because it's a real standalone file with
    /// its own snapshot table.</summary>
    public required string DiskPath { get; init; }
    public int AdbHostPort { get; init; }
    public int MonitorPort { get; init; }
    public VmState State { get; set; } = VmState.Stopped;
    public int? ProcessId { get; set; }

    /// <summary>True once a post-first-boot QEMU snapshot ("dbsnap") has been saved into this
    /// VM's disk (either inherited from the golden image at creation time, or saved locally
    /// after this VM's own first boot). When true, starts pass -loadvm and resume
    /// near-instantly instead of re-running the whole cold boot (GRUB -> kernel -> Android
    /// init).</summary>
    public bool HasSnapshot { get; set; }
}
