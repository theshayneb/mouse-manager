using System.Net;
using System.Net.Sockets;

namespace KeyShare;

/// <summary>
/// Runs on the PC being controlled: finds the host, connects with the share
/// code, plays its input, and reconnects on its own if the link drops.
/// </summary>
internal sealed class ClientService : IDisposable
{
    private readonly string _code;
    private readonly string _address;
    private readonly CancellationTokenSource _cts = new();
    private readonly InputInjector _injector = new();
    private SecureLink? _link;

    public event Action<string>? Status;
    public event Action<bool>? ActiveChanged
    {
        add => _injector.ActiveChanged += value;
        remove => _injector.ActiveChanged -= value;
    }

    public ClientService(string code, string address)
    {
        _code = code;
        _address = address.Trim();
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        bool everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var targets = await FindTargetsAsync(ct);
                if (targets.Count == 0)
                {
                    Status?.Invoke(_address.Length > 0
                        ? $"Can't find {_address}. Retrying…"
                        : "Looking for the host… (is it sharing, and on the same network?)");
                }

                foreach (var (ep, name) in targets)
                {
                    Status?.Invoke($"Connecting to {name}…");
                    SecureLink link;
                    try
                    {
                        link = await ConnectAsync(ep, ct);
                    }
                    catch (WrongCodeException ex)
                    {
                        Status?.Invoke($"{name}: {ex.Message} Check the code.");
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Log.Info($"Connect to {ep} failed: {ex.Message}");
                        Status?.Invoke($"Couldn't connect to {name}: {Short(ex)}");
                        continue;
                    }

                    everConnected = true;
                    Status?.Invoke($"Connected to {link.PeerName}");
                    Log.Info($"Connected to {link.PeerName} ({link.PeerAddress})");
                    _link = link;
                    _injector.RequestReturn = along => link.Send(Protocol.Return(along));
                    link.Start(_injector.Handle);
                    await link.Completion;
                    _link = null;
                    _injector.ReleaseAll();
                    if (ct.IsCancellationRequested) return;
                    Log.Info($"Disconnected from {link.PeerName}");
                    Status?.Invoke($"Lost connection to {link.PeerName}. Reconnecting…");
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Client loop", ex);
                Status?.Invoke($"Error: {Short(ex)}. Retrying…");
            }

            try { await Task.Delay(everConnected ? 1000 : 2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<List<(IPEndPoint, string)>> FindTargetsAsync(CancellationToken ct)
    {
        if (_address.Length == 0) return await Discovery.FindHostsAsync(ct);

        string hostPart = _address;
        int port = Protocol.TcpPort;
        int colon = _address.LastIndexOf(':');
        if (colon > 0 && _address.IndexOf(':') == colon && int.TryParse(_address[(colon + 1)..], out int p))
        {
            hostPart = _address[..colon];
            port = p;
        }

        var list = new List<(IPEndPoint, string)>();
        try
        {
            IPAddress[] addrs = IPAddress.TryParse(hostPart, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(hostPart, ct);
            foreach (var a in addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                list.Add((new IPEndPoint(a, port), hostPart));
        }
        catch (SocketException)
        {
        }
        return list;
    }

    private async Task<SecureLink> ConnectAsync(IPEndPoint ep, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(ep, timeout.Token);
            return await SecureLink.EstablishAsync(tcp, _code, isHost: false, timeout.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static string Short(Exception ex) => ex switch
    {
        OperationCanceledException => "timed out",
        SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused => "connection refused",
        _ => ex.Message,
    };

    public void Dispose()
    {
        _cts.Cancel();
        _link?.Dispose();
        _injector.ReleaseAll();
    }
}
