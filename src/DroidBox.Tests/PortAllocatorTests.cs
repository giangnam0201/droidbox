using System.Net;
using System.Net.Sockets;
using DroidBox.Core.Vm;

namespace DroidBox.Tests;

public class PortAllocatorTests
{
    [Fact]
    public void FindFreePort_ReturnsABindablePort()
    {
        var port = PortAllocator.FindFreePort(20000);

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(); // should not throw
        listener.Stop();
    }

    [Fact]
    public void FindFreePort_SkipsAnOccupiedPort()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var found = PortAllocator.FindFreePort(occupiedPort);

        Assert.NotEqual(occupiedPort, found);
        occupied.Stop();
    }
}
