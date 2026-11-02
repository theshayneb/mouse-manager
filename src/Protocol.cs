using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KeyShare;

/// <summary>Where the other PC sits relative to the host's screen.</summary>
internal enum Side : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

internal enum Msg : byte
{
    Move = 1,     // host -> client: int16 dx, int16 dy
    Button = 2,   // host -> client: byte button (0 L, 1 R, 2 M, 3 X1, 4 X2), byte down
    Wheel = 3,    // host -> client: int16 delta, byte horizontal
    Key = 4,      // host -> client: uint16 vk, uint16 scan, byte flags (1 = up, 2 = extended)
    Enter = 5,    // host -> client: byte center, byte side, float position along the edge (0..1)
    Leave = 6,    // host -> client: control went back to the host
    Return = 7,   // client -> host: float position along the edge (0..1)
    Ping = 8,     // either way, keeps the link alive
    Hello = 0x10, // handshake: UTF-8 machine name
}

internal static class Protocol
{
    public const int TcpPort = 47832;
    public const int DiscoveryPort = 47833;
    public static readonly byte[] DiscoverQuery = "KSDISCOVER1"u8.ToArray();
    public const string DiscoverReplyPrefix = "KSHOST1|";

    public static readonly byte[] Ping = [(byte)Msg.Ping];
    public static readonly byte[] Leave = [(byte)Msg.Leave];

    public static byte[] Move(int dx, int dy)
    {
        var b = new byte[5];
        b[0] = (byte)Msg.Move;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return b;
    }

    public static byte[] Button(int button, bool down) => [(byte)Msg.Button, (byte)button, down ? (byte)1 : (byte)0];

    public static byte[] Wheel(int delta, bool horizontal)
    {
        var b = new byte[4];
        b[0] = (byte)Msg.Wheel;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)delta);
        b[3] = horizontal ? (byte)1 : (byte)0;
        return b;
    }

    public static byte[] Key(int vk, int scan, bool up, bool extended)
    {
        var b = new byte[6];
        b[0] = (byte)Msg.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)vk);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), (ushort)scan);
        b[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return b;
    }

    public static byte[] Enter(bool center, Side side, float along)
    {
        var b = new byte[7];
        b[0] = (byte)Msg.Enter;
        b[1] = center ? (byte)1 : (byte)0;
        b[2] = (byte)side;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(3), along);
        return b;
    }

    public static byte[] Return(float along)
    {
        var b = new byte[5];
        b[0] = (byte)Msg.Return;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), along);
        return b;
    }

    public static byte[] Hello(string name)
    {
        var n = Encoding.UTF8.GetBytes(name.Length > 60 ? name[..60] : name);
        var b = new byte[1 + n.Length];
        b[0] = (byte)Msg.Hello;
        n.CopyTo(b, 1);
        return b;
    }

    public static string ParseHello(byte[] m)
    {
        if (m.Length < 1 || m[0] != (byte)Msg.Hello) throw new InvalidDataException("Bad hello");
        return Encoding.UTF8.GetString(m, 1, m.Length - 1);
    }

    // ---- Share codes ----------------------------------------------------

    // No 0/O or 1/I so codes are easy to read aloud and type.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>A new random code, 8 characters (40 bits), shown as XXXX-XXXX.</summary>
    public static string NewCode()
    {
        Span<char> c = stackalloc char[8];
        for (int i = 0; i < c.Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Normalize(new string(c));
    }

    /// <summary>Uppercases, drops spaces and dashes, and formats as XXXX-XXXX.</summary>
    public static string Normalize(string code)
    {
        var sb = new StringBuilder();
        foreach (char c in code.ToUpperInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        var s = sb.ToString();
        return s.Length == 8 ? s[..4] + "-" + s[4..] : s;
    }

    public static bool IsValidCode(string normalized) =>
        normalized.Length == 9 && normalized[4] == '-' &&
        normalized.Replace("-", "").All(c => Alphabet.Contains(c));
}
