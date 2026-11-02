using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace KeyShare;

/// <summary>The share code did not match on the other side.</summary>
internal sealed class WrongCodeException(string message) : Exception(message);

/// <summary>
/// An encrypted, authenticated TCP link between host and client.
///
/// Handshake: both sides send an ephemeral P-256 public key, derive a shared
/// secret with ECDH, and mix in the share code to get one AES-256-GCM key per
/// direction. The first encrypted frame each way is a Hello; if the codes
/// differ, the Hello does not decrypt and the link is refused. Someone who
/// only listens on the network learns nothing, and someone without the code
/// cannot connect.
/// </summary>
internal sealed class SecureLink : IDisposable
{
    private static readonly byte[] Magic = "KSH1"u8.ToArray();
    private const int TagSize = 16;
    private const int MaxFrame = 4096;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(2);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly AesGcm _enc, _dec;
    private readonly byte[] _sendNonce = new byte[12], _recvNonce = new byte[12];
    private ulong _sendCounter, _recvCounter;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private long _lastReceive = Environment.TickCount64;
    private int _disposed;

    public string PeerName { get; private set; } = "";
    public string PeerAddress { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    private SecureLink(TcpClient tcp, byte[] sendKey, byte[] recvKey)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _enc = new AesGcm(sendKey, TagSize);
        _dec = new AesGcm(recvKey, TagSize);
        PeerAddress = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public static async Task<SecureLink> EstablishAsync(TcpClient tcp, string code, bool isHost, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] mine = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        await WriteBlobAsync(stream, mine, ct);
        byte[] theirs = await ReadBlobAsync(stream, ct);

        var (sendKey, recvKey) = DeriveKeys(ecdh, mine, theirs, code, isHost);
        var link = new SecureLink(tcp, sendKey, recvKey);
        try
        {
            byte[] hello = Protocol.Hello(Environment.MachineName);
            if (isHost)
            {
                byte[] first;
                try { first = await link.ReadFrameAsync(ct); }
                catch (CryptographicException) { throw new WrongCodeException("The other PC used a different share code."); }
                link.PeerName = Protocol.ParseHello(first);
                await link.WriteFrameAsync(hello, ct);
            }
            else
            {
                await link.WriteFrameAsync(hello, ct);
                byte[] reply;
                try { reply = await link.ReadFrameAsync(ct); }
                catch (EndOfStreamException) { throw new WrongCodeException("The host rejected the share code."); }
                catch (IOException) { throw new WrongCodeException("The host rejected the share code."); }
                link.PeerName = Protocol.ParseHello(reply);
            }
            return link;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    private static (byte[] send, byte[] recv) DeriveKeys(ECDiffieHellman ecdh, byte[] mine, byte[] theirs, string code, bool isHost)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(theirs, out _);
        byte[] shared = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);

        byte[] clientPub = isHost ? theirs : mine;
        byte[] hostPub = isHost ? mine : theirs;
        byte[] transcript = SHA256.HashData(Concat(clientPub, hostPub));
        byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("KeyShare/1 share code " + code));

        byte[] c2h = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Concat("client->host"u8.ToArray(), transcript));
        byte[] h2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Concat("host->client"u8.ToArray(), transcript));
        return isHost ? (h2c, c2h) : (c2h, h2c);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static async Task WriteBlobAsync(NetworkStream s, byte[] data, CancellationToken ct)
    {
        var buf = new byte[Magic.Length + 2 + data.Length];
        Magic.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(Magic.Length), (ushort)data.Length);
        data.CopyTo(buf, Magic.Length + 2);
        await s.WriteAsync(buf, ct);
    }

    private static async Task<byte[]> ReadBlobAsync(NetworkStream s, CancellationToken ct)
    {
        var head = new byte[Magic.Length + 2];
        await s.ReadExactlyAsync(head, ct);
        if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The other side is not KeyShare (or is a different version).");
        int len = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(Magic.Length));
        if (len is < 16 or > 1024) throw new InvalidDataException("Bad key length.");
        var data = new byte[len];
        await s.ReadExactlyAsync(data, ct);
        return data;
    }

    // ---- Frames ---------------------------------------------------------

    private byte[] Seal(byte[] plain)
    {
        var frame = new byte[2 + plain.Length + TagSize];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)(plain.Length + TagSize));
        BinaryPrimitives.WriteUInt64LittleEndian(_sendNonce.AsSpan(4), _sendCounter++);
        _enc.Encrypt(_sendNonce, plain, frame.AsSpan(2, plain.Length), frame.AsSpan(2 + plain.Length, TagSize));
        return frame;
    }

    private byte[] Open(byte[] data)
    {
        var plain = new byte[data.Length - TagSize];
        BinaryPrimitives.WriteUInt64LittleEndian(_recvNonce.AsSpan(4), _recvCounter++);
        _dec.Decrypt(_recvNonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length, TagSize), plain);
        return plain;
    }

    private async Task WriteFrameAsync(byte[] plain, CancellationToken ct) => await _stream.WriteAsync(Seal(plain), ct);

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var head = new byte[2];
        await _stream.ReadExactlyAsync(head, ct);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(head);
        if (len is < TagSize + 1 or > MaxFrame) throw new InvalidDataException("Bad frame length.");
        var data = new byte[len];
        await _stream.ReadExactlyAsync(data, ct);
        return Open(data);
    }

    // ---- Running link ---------------------------------------------------

    /// <summary>Queue a message. Safe to call from any thread, never blocks.</summary>
    public void Send(byte[] message) => _outbox.Writer.TryWrite(message);

    /// <summary>Starts the reader, writer and watchdog. <see cref="Completion"/> finishes when the link drops.</summary>
    public void Start(Action<byte[]> onMessage) => Completion = RunAsync(onMessage);

    private async Task RunAsync(Action<byte[]> onMessage)
    {
        var ct = _cts.Token;
        var tasks = new[] { ReadLoopAsync(onMessage, ct), WriteLoopAsync(ct), WatchdogAsync(ct) };
        await Task.WhenAny(tasks);
        Dispose();
        try { await Task.WhenAll(tasks); } catch { /* already logged */ }
    }

    private async Task ReadLoopAsync(Action<byte[]> onMessage, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReadFrameAsync(ct);
                Interlocked.Exchange(ref _lastReceive, Environment.TickCount64);
                if (msg.Length == 0 || msg[0] == (byte)Msg.Ping) continue;
                try { onMessage(msg); }
                catch (Exception ex) { Log.Error("Handling message", ex); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or ObjectDisposedException or SocketException)
        {
            // Normal disconnect.
        }
        catch (Exception ex)
        {
            Log.Error("Link read", ex);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var reader = _outbox.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingEvery);
                bool hasData;
                try { hasData = await reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteFrameAsync(Protocol.Ping, ct);
                    continue;
                }
                if (!hasData) break;
                while (reader.TryRead(out var m)) await WriteFrameAsync(m, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Link write", ex);
        }
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastReceive) > IdleTimeout.TotalMilliseconds)
                {
                    Log.Info($"Link to {PeerName} timed out");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        _outbox.Writer.TryComplete();
        try { _tcp.Dispose(); } catch { }
        // The AES objects are left for the GC: a loop may still be finishing a frame.
    }
}
