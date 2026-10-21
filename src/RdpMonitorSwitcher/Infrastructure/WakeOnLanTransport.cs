using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Infrastructure;

/// <summary>
/// Sends the magic packet as a broadcast on every active IPv4 network (subnet broadcast plus
/// 255.255.255.255), UDP ports 9 and 7. Only reaches PCs in the same local network.
/// </summary>
internal sealed class UdpMagicPacketSender : IMagicPacketSender
{
    private static readonly int[] Ports = [9, 7];

    public async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                targets.Add(new IPAddress(broadcast));
            }
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        foreach (var target in targets)
        {
            foreach (var port in Ports)
            {
                try
                {
                    await udp.SendAsync(packet, new IPEndPoint(target, port), cancellationToken);
                }
                catch (SocketException)
                {
                    // Some interfaces (e.g. VPN adapters) reject broadcasts; the others still work.
                }
            }
        }
    }
}

internal static class TcpProbe
{
    /// <summary>True if a TCP connection to host:port succeeds within 2 seconds.</summary>
    public static async Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
