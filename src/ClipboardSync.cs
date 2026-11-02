using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace KeyShare;

/// <summary>
/// Copies the clipboard between the two PCs: plain text, rich text (RTF)
/// and images. Contents are split into chunks so a big image never holds up
/// the mouse, and reassembled on the other side.
/// </summary>
internal sealed class ClipboardSync
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private const int ChunkSize = 32 * 1024;
    private const int HeaderSize = 13; // type, id, total, offset

    private const byte FormatText = 1, FormatRtf = 2, FormatPng = 3;

    private uint _nextId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly object _gate = new();
    private byte[]? _lastHash; // last contents sent or received

    // Receiving side. Only touched by the link's single reader thread.
    private uint _rxId;
    private byte[]? _rx;
    private int _rxGot;

    // ---- Sending ---------------------------------------------------------

    /// <summary>What's on the clipboard right now. Call on the UI thread.</summary>
    public static (string? text, string? rtf, Image? image) Read()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Password managers mark secrets with this so clipboard tools leave them alone.
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return (null, null, null);
                string? text = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
                string? rtf = Clipboard.ContainsText(TextDataFormat.Rtf) ? Clipboard.GetText(TextDataFormat.Rtf) : null;
                Image? image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
                return (text, rtf, image);
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 3)
            {
                Thread.Sleep(20); // another app has the clipboard open
            }
        }
    }

    /// <summary>
    /// Packs clipboard contents into chunk messages, or returns null if there is
    /// nothing to send, it's too big, or it's the same as last time. Encoding an
    /// image takes a moment, so call this off the UI thread (the input hooks run there).
    /// </summary>
    public List<byte[]>? Pack(string? text, string? rtf, Image? image)
    {
        using var bundle = new MemoryStream();
        void Add(byte format, byte[] data)
        {
            Span<byte> head = stackalloc byte[5];
            head[0] = format;
            BinaryPrimitives.WriteInt32LittleEndian(head[1..], data.Length);
            bundle.Write(head);
            bundle.Write(data);
        }

        if (!string.IsNullOrEmpty(text)) Add(FormatText, Encoding.UTF8.GetBytes(text));
        if (!string.IsNullOrEmpty(rtf)) Add(FormatRtf, Encoding.UTF8.GetBytes(rtf));
        if (image != null)
        {
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            Add(FormatPng, png.ToArray());
        }
        if (bundle.Length == 0 || bundle.Length > MaxBytes) return null;

        byte[] data = bundle.ToArray();
        byte[] hash = SHA256.HashData(data);
        uint id;
        lock (_gate)
        {
            if (_lastHash != null && hash.AsSpan().SequenceEqual(_lastHash)) return null;
            _lastHash = hash;
            id = unchecked(++_nextId);
        }

        var chunks = new List<byte[]>();
        for (int offset = 0; offset < data.Length; offset += ChunkSize)
        {
            int n = Math.Min(ChunkSize, data.Length - offset);
            var m = new byte[HeaderSize + n];
            m[0] = (byte)Msg.Clip;
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
            BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(5), data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(9), offset);
            data.AsSpan(offset, n).CopyTo(m.AsSpan(HeaderSize));
            chunks.Add(m);
        }
        return chunks;
    }

    /// <summary>Remembers contents that arrived from the other PC, so they aren't sent straight back.</summary>
    public void NoteReceived(byte[] bundle)
    {
        var hash = SHA256.HashData(bundle);
        lock (_gate) _lastHash = hash;
    }

    // ---- Receiving -------------------------------------------------------

    /// <summary>Takes one chunk; returns the whole bundle once its last chunk arrives.</summary>
    public byte[]? Accept(byte[] m)
    {
        if (m.Length < HeaderSize) return null;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(1));
        int total = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(5));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(9));
        int n = m.Length - HeaderSize;
        if (total is <= 0 or > MaxBytes) return null;

        if (offset == 0)
        {
            _rxId = id;
            _rx = new byte[total];
            _rxGot = 0;
        }
        if (_rx == null || id != _rxId || offset != _rxGot || offset + n > _rx.Length)
        {
            _rx = null; // out of step: drop this copy, the next one starts fresh
            return null;
        }

        m.AsSpan(HeaderSize).CopyTo(_rx.AsSpan(offset));
        _rxGot += n;
        if (_rxGot < _rx.Length) return null;

        var done = _rx;
        _rx = null;
        return done;
    }

    /// <summary>
    /// Unpacks a received bundle. Decoding an image takes a moment, so call this
    /// off the UI thread, then pass the result to <see cref="Set"/>.
    /// </summary>
    public static (string? text, string? rtf, Bitmap? image) Unpack(byte[] bundle)
    {
        string? text = null, rtf = null;
        Bitmap? image = null;
        int pos = 0;
        while (pos + 5 <= bundle.Length)
        {
            byte format = bundle[pos];
            int len = BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(pos + 1));
            pos += 5;
            if (len < 0 || pos + len > bundle.Length) break;
            switch (format)
            {
                case FormatText: text = Encoding.UTF8.GetString(bundle, pos, len); break;
                case FormatRtf: rtf = Encoding.UTF8.GetString(bundle, pos, len); break;
                case FormatPng when image == null:
                    using (var ms = new MemoryStream(bundle, pos, len, writable: false))
                    using (var decoded = Image.FromStream(ms))
                        image = new Bitmap(decoded); // detached from the stream
                    break;
            }
            pos += len;
        }
        return (text, rtf, image);
    }

    /// <summary>Puts unpacked contents on this PC's clipboard. Call on the UI thread.</summary>
    public static void Set(string? text, string? rtf, Bitmap? image)
    {
        try
        {
            var obj = new DataObject();
            if (text != null) obj.SetText(text, TextDataFormat.UnicodeText);
            if (rtf != null) obj.SetText(rtf, TextDataFormat.Rtf);
            if (image != null) obj.SetImage(image);
            Clipboard.SetDataObject(obj, copy: true, retryTimes: 10, retryDelay: 50);
        }
        finally
        {
            image?.Dispose();
        }
    }
}
