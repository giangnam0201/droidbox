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
    public VmState State { get; set; } = VmState.Stopped;
    public int? ProcessId { get; set; }
}
