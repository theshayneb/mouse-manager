using System.Buffers.Binary;
using static KeyShare.Native;

namespace KeyShare;

/// <summary>Plays the host's input on this PC with SendInput.</summary>
internal sealed class InputInjector
{
    private readonly object _gate = new();
    private bool _active;
    private Side _hostSide; // the host's setting: where THIS PC sits relative to the host
    private readonly Dictionary<int, (ushort vk, ushort scan, bool ext)> _keysDown = new();
    private readonly bool[] _buttonsDown = new bool[5];

    /// <summary>Called when the cursor runs off the edge that leads back to the host.</summary>
    public Action<float>? RequestReturn { get; set; }
    public event Action<bool>? ActiveChanged;

    public void Handle(byte[] m)
    {
        lock (_gate)
        {
            switch ((Msg)m[0])
            {
                case Msg.Enter when m.Length >= 7:
                    Enter(m[1] == 1, (Side)m[2], BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(3)));
                    break;
                case Msg.Leave:
                    SetActive(false);
                    break;
                case Msg.Move when m.Length >= 5:
                    Move(BinaryPrimitives.ReadInt16LittleEndian(m.AsSpan(1)), BinaryPrimitives.ReadInt16LittleEndian(m.AsSpan(3)));
                    break;
                case Msg.Button when m.Length >= 3:
                    Button(m[1], m[2] == 1);
                    break;
                case Msg.Wheel when m.Length >= 4:
                    Wheel(BinaryPrimitives.ReadInt16LittleEndian(m.AsSpan(1)), m[3] == 1);
                    break;
                case Msg.Key when m.Length >= 6:
                    Key(BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(1)), BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(3)), m[5]);
                    break;
            }
        }
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        ActiveChanged?.Invoke(active);
    }

    private void Enter(bool center, Side hostSide, float along)
    {
        _hostSide = hostSide;
        var vs = VirtualScreen();
        int x, y;
        if (center)
        {
            var s = Screen.PrimaryScreen?.Bounds ?? vs;
            x = s.Left + s.Width / 2;
            y = s.Top + s.Height / 2;
        }
        else
        {
            along = Math.Clamp(along, 0f, 1f);
            int ax = vs.Left + (int)(along * (vs.Width - 1));
            int ay = vs.Top + (int)(along * (vs.Height - 1));
            // The host's right edge leads to our left edge, and so on.
            (x, y) = hostSide switch
            {
                Side.Right => (vs.Left + 2, ay),
                Side.Left => (vs.Right - 3, ay),
                Side.Bottom => (ax, vs.Top + 2),
                _ => (ax, vs.Bottom - 3),
            };
        }
        SetCursorPos(x, y);
        NudgeMouse();
        SetActive(true);
    }

    private void Move(int dx, int dy)
    {
        if (!_active) return;
        GetCursorPos(out var p);
        int nx = p.X + dx, ny = p.Y + dy;
        var vs = VirtualScreen();

        bool back = _hostSide switch
        {
            Side.Right => nx < vs.Left,
            Side.Left => nx > vs.Right - 1,
            Side.Bottom => ny < vs.Top,
            _ => ny > vs.Bottom - 1,
        };
        if (back && !AnyButtonDown())
        {
            float along = _hostSide is Side.Left or Side.Right
                ? (p.Y - vs.Top) / (float)vs.Height
                : (p.X - vs.Left) / (float)vs.Width;
            SetActive(false);
            RequestReturn?.Invoke(along);
            return;
        }

        nx = Math.Clamp(nx, vs.Left, vs.Right - 1);
        ny = Math.Clamp(ny, vs.Top, vs.Bottom - 1);
        SetCursorPos(nx, ny);
        NudgeMouse();
    }

    /// <summary>A zero-distance mouse move so apps see real mouse input at the new position (hover, drag and drop).</summary>
    private static void NudgeMouse() => Native.Send(Mouse(MOUSEEVENTF_MOVE, 0));

    private bool AnyButtonDown() => _buttonsDown.Any(b => b);

    private void Button(int index, bool down)
    {
        if (index is < 0 or > 4) return;
        _buttonsDown[index] = down;
        uint flags = index switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
        };
        uint data = index switch { 3 => 1u, 4 => 2u, _ => 0u };
        Native.Send(Mouse(flags, data));
    }

    private static void Wheel(int delta, bool horizontal) =>
        Native.Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, unchecked((uint)delta)));

    private void Key(ushort vk, ushort scan, byte flags)
    {
        bool up = (flags & 1) != 0, ext = (flags & 2) != 0;
        if (up) _keysDown.Remove(vk); else _keysDown[vk] = (vk, scan, ext);
        Native.Send(KeyInput(vk, scan, ext, up));
    }

    private static INPUT KeyInput(ushort vk, ushort scan, bool ext, bool up)
    {
        // Scan codes reproduce the physical key, so the other PC's own keyboard
        // layout applies. Media keys and Pause only work by virtual-key code.
        bool byVk = scan == 0 || vk == 0x13 || (vk >= 0xA6 && vk <= 0xB7);
        var ki = new KEYBDINPUT
        {
            wVk = byVk ? vk : (ushort)0,
            wScan = scan,
            dwFlags = (byVk ? 0 : KEYEVENTF_SCANCODE) | (ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
        };
        return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = ki } };
    }

    private static INPUT Mouse(uint flags, uint data) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };

    /// <summary>Lets go of everything the host was holding, so nothing stays stuck after a disconnect.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var (vk, scan, ext) in _keysDown.Values) Native.Send(KeyInput(vk, scan, ext, up: true));
            _keysDown.Clear();
            for (int i = 0; i < _buttonsDown.Length; i++) if (_buttonsDown[i]) Button(i, false);
            SetActive(false);
        }
    }
}
