using DroidBox.Core.Models;

namespace DroidBox.Tests;

public class VmInstanceTests
{
    [Fact]
    public void NewInstance_DefaultsToStopped()
    {
        var vm = new VmInstance
        {
            Id = "abc123",
            VersionId = "7.1",
            OverlayPath = @"C:\vms\abc123.qcow2",
        };

        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Null(vm.ProcessId);
    }

    [Fact]
    public void RoundTrip_ThroughJson_PreservesFields()
    {
        var vm = new VmInstance
        {
            Id = "abc123",
            VersionId = "7.1",
            OverlayPath = @"C:\vms\abc123.qcow2",
            AdbHostPort = 5555,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(vm);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VmInstance>(json);

        Assert.NotNull(restored);
        Assert.Equal(vm.Id, restored!.Id);
        Assert.Equal(vm.VersionId, restored.VersionId);
        Assert.Equal(vm.OverlayPath, restored.OverlayPath);
        Assert.Equal(vm.AdbHostPort, restored.AdbHostPort);
    }
}
