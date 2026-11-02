using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace KeyShare;

/// <summary>Finds hosts on the local network with a UDP broadcast.</summary>
internal static class Discovery
{
    /// <summary>Answers discovery broadcasts until cancelled. Runs on the host.</summary>
    public static async Task RespondAsync(Action<string> status, CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
        }
        catch (Exception ex)
        {
            Log.Error("Discovery bind", ex);
            status($"Auto-discovery is off (UDP {Protocol.DiscoveryPort} busy). Join using this PC's IP address.");
            return;
        }

        using (udp)
        {
            byte[] reply = Encoding.UTF8.GetBytes($"{Protocol.DiscoverReplyPrefix}{Protocol.TcpPort}|{Environment.MachineName}");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(ct);
                    if (r.Buffer.AsSpan().SequenceEqual(Protocol.DiscoverQuery))
                        await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { /* e.g. ICMP port unreachable from a past reply; keep going */ }
                catch (Exception ex) { Log.Error("Discovery", ex); await Task.Delay(500, ct); }
            }
        }
    }

    /// <summary>Broadcasts a query on every network and collects the hosts that answer.</summary>
    public static async Task<List<(IPEndPoint endpoint, string name)>> FindHostsAsync(CancellationToken ct)
    {
        var found = new List<(IPEndPoint, string)>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };

        foreach (var target in BroadcastAddresses())
        {
            try { await udp.SendAsync(Protocol.DiscoverQuery, new IPEndPoint(target, Protocol.DiscoveryPort), ct); }
            catch (SocketException) { }
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(1500);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(window.Token);
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (!text.StartsWith(Protocol.DiscoverReplyPrefix)) continue;
                var parts = text[Protocol.DiscoverReplyPrefix.Length..].Split('|', 2);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int port)) continue;
                var ep = new IPEndPoint(r.RemoteEndPoint.Address, port);
                if (!found.Any(f => f.Item1.Equals(ep))) found.Add((ep, parts[1]));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Listening window over.
        }
        catch (SocketException)
        {
        }
        return found;
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(b));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing networks", ex);
        }
        return list;
    }
}
