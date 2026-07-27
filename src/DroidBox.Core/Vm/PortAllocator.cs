using System.Net;
using System.Net.Sockets;

namespace DroidBox.Core.Vm;

public static class PortAllocator
{
    public static int FindFreePort(int startingFrom = 5555)
    {
        for (var port = startingFrom; port < startingFrom + 2000; port++)
        {
            if (IsFree(port))
                return port;
        }

        throw new InvalidOperationException("No free TCP port found for adb forwarding.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
