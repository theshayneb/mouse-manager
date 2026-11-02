using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace KeyShare;

/// <summary>
/// Runs on the PC whose keyboard and mouse are shared: listens for the other PC,
/// checks its share code, and keeps one link open at a time.
/// </summary>
internal sealed class HostService : IDisposable
{
    private readonly string _code;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private SecureLink? _current;

    public event Action<string>? Status;
    public event Action<string>? Connected;    // peer name
    public event Action? Disconnected;
    public event Action<float>? ReturnRequested;

    public HostService(string code) => _code = code;

    public bool IsConnected => Volatile.Read(ref _current) != null;

    public void Send(byte[] message) => Volatile.Read(ref _current)?.Send(message);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, Protocol.TcpPort);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => Discovery.RespondAsync(s => Status?.Invoke(s), _cts.Token));
        Status?.Invoke("Waiting for the other PC to join…");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Error("Accept", ex); await Task.Delay(500, CancellationToken.None); continue; }

            string from = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var link = await SecureLink.EstablishAsync(tcp, _code, isHost: true, timeout.Token);
                Adopt(link);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcp.Dispose();
                break;
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                Log.Info($"Refused {from}: {ex.Message}");
                if (ex is WrongCodeException) Status?.Invoke($"Refused {from}: wrong share code.");
                // Slow down guessing: at most one failed attempt per second.
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Adopt(SecureLink link)
    {
        var old = Interlocked.Exchange(ref _current, link);
        old?.Dispose();
        link.Start(OnMessage);
        Log.Info($"Connected to {link.PeerName} ({link.PeerAddress})");
        Connected?.Invoke(link.PeerName);
        link.Completion.ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref _current, null, link) == link)
            {
                Log.Info($"Disconnected from {link.PeerName}");
                Disconnected?.Invoke();
            }
        }, TaskScheduler.Default);
    }

    private void OnMessage(byte[] m)
    {
        if (m[0] == (byte)Msg.Return && m.Length >= 5)
            ReturnRequested?.Invoke(BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(1)));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        Interlocked.Exchange(ref _current, null)?.Dispose();
    }
}
