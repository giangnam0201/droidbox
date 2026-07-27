using System.Net.Sockets;
using System.Text;

namespace DroidBox.Core.Vm;

/// <summary>
/// Talks to QEMU's legacy HMP monitor over TCP to save a snapshot of the running VM (RAM + CPU
/// + device state, written into the qcow2 overlay itself). Using HMP text commands instead of
/// QMP's newer job-based snapshot-save/snapshot-load API because it's a single synchronous
/// command supported unchanged across QEMU versions.
/// </summary>
public sealed class QemuMonitorClient
{
    public const string SnapshotTag = "dbsnap";

    public static async Task SaveSnapshotAsync(int monitorPort, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", monitorPort, ct);
        using var stream = client.GetStream();

        // Drain the monitor's banner/prompt before sending a command.
        await Task.Delay(300, ct);
        await DrainAsync(stream, ct);

        var command = Encoding.ASCII.GetBytes($"savevm {SnapshotTag}\n");
        await stream.WriteAsync(command, ct);

        // savevm blocks the monitor until the snapshot is fully written; give it time to finish
        // and drain the response so the connection doesn't look hung to the caller.
        await Task.Delay(1000, ct);
        await DrainAsync(stream, ct);
    }

    private static async Task DrainAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (stream.DataAvailable)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;
        }
    }
}
