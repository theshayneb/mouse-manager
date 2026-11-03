using System.Runtime.InteropServices;
using static KeyShare.Native;

namespace KeyShare;

/// <summary>
/// Global keyboard and mouse hooks on the host. While control is on the other
/// PC, local input is swallowed and forwarded instead.
///
/// Must be created on the UI thread: low-level hooks are called on the thread
/// that installed them, through its message loop. Callbacks only do a few
/// array operations and queue a message, so Windows never times them out.
/// </summary>
internal sealed class InputCapture : IDisposable
{
    private const byte None = 0, Local = 1, Remote = 2, Swallow = 3;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly Func<bool> _isConnected;
    private readonly Action<byte[]> _send;
    private readonly HookProc _keyProc, _mouseProc; // held in fields so the GC never collects them
    private IntPtr _keyHook, _mouseHook;

    private bool _remote;
    private POINT _park;

    // Which side got the "down" of each key and button, so the matching "up"
    // goes to the same side even if control switched in between. That is what
    // keeps keys from getting stuck when you switch while holding Ctrl or drag.
    private readonly byte[] _keyOwner = new byte[256];
    private readonly bool[] _keyDown = new bool[256];
    private readonly byte[] _buttonOwner = new byte[5];

    public Side OtherSide { get; set; } = Side.Right;
    public SwitchKey SwitchKey { get; set; } = SwitchKey.Default;
    /// <summary>The cursor came back to this PC by the switch key (so it may be hard to spot).</summary>
    public event Action? ReturnedByKey;
    public bool IsRemote => _remote;
    public event Action<bool>? RemoteChanged;

    public InputCapture(Func<bool> isConnected, Action<byte[]> send)
    {
        _isConnected = isConnected;
        _send = send;
        _keyProc = KeyboardProc;
        _mouseProc = MouseProc;
        Install();
    }

    private void Install()
    {
        IntPtr module = GetModuleHandle(null);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Dispose();
            throw new InvalidOperationException($"Could not install input hooks (error {err}).");
        }
    }

    /// <summary>
    /// Puts the hooks back. Windows silently drops a hook that is slow to answer,
    /// which can happen while the PC wakes up, so this runs after sleep and unlock.
    /// </summary>
    public void Reinstall()
    {
        Dispose();
        Install();
    }

    // ---- Switching ------------------------------------------------------

    private void EnterRemote(bool center, float along)
    {
        Log.Info(center ? "Control to the other PC (switch key)" : "Control to the other PC (edge)");
        _remote = true;
        var screen = Screen.PrimaryScreen?.Bounds ?? VirtualScreen();
        _park = new POINT(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        ClipCursor(IntPtr.Zero); // a game here may have fenced the cursor in
        SetCursorPos(_park.X, _park.Y);
        _send(Protocol.Enter(center, OtherSide, along));
        RemoteChanged?.Invoke(true);
    }

    /// <summary>Gives control back to this PC. <paramref name="along"/> &lt; 0 puts the cursor in the middle.</summary>
    public void ReturnToLocal(float along, bool tellOther)
    {
        if (!_remote) return;
        _remote = false;
        Log.Info(along < 0 ? "Control back to this PC (switch key)" : "Control back to this PC (edge)");
        if (tellOther) _send(Protocol.Leave);

        var vs = VirtualScreen();
        int x, y;
        if (along < 0)
        {
            var screen = Screen.PrimaryScreen?.Bounds ?? vs;
            x = screen.Left + screen.Width / 2;
            y = screen.Top + screen.Height / 2;
        }
        else
        {
            along = Math.Clamp(along, 0f, 1f);
            int ax = vs.Left + (int)(along * (vs.Width - 1));
            int ay = vs.Top + (int)(along * (vs.Height - 1));
            (x, y) = OtherSide switch
            {
                Side.Left => (vs.Left + 3, ay),
                Side.Right => (vs.Right - 4, ay),
                Side.Top => (ax, vs.Top + 3),
                _ => (ax, vs.Bottom - 4),
            };
        }
        SetCursorPos(x, y);

        // Keys held on the other PC keep sending their "up" there (see _keyOwner).
        RemoteChanged?.Invoke(false);
        if (along < 0) ReturnedByKey?.Invoke();
    }

    /// <summary>The link dropped: take control back without talking to the other PC.</summary>
    public void ForceLocal()
    {
        if (_remote) ReturnToLocal(-1, tellOther: false);
        for (int i = 0; i < _keyOwner.Length; i++) if (_keyOwner[i] == Remote) _keyOwner[i] = None;
        for (int i = 0; i < _buttonOwner.Length; i++) if (_buttonOwner[i] == Remote) _buttonOwner[i] = None;
    }

    public void Toggle()
    {
        if (_remote) ReturnToLocal(-1, tellOther: true);
        else if (_isConnected()) EnterRemote(center: true, 0.5f);
        else Log.Info("Switch key pressed, but the other PC isn't connected");
    }

    private bool HitsEdge(POINT p, out float along)
    {
        var vs = VirtualScreen();
        switch (OtherSide)
        {
            case Side.Left: along = (p.Y - vs.Top) / (float)vs.Height; return p.X <= vs.Left;
            case Side.Right: along = (p.Y - vs.Top) / (float)vs.Height; return p.X >= vs.Right - 1;
            case Side.Top: along = (p.X - vs.Left) / (float)vs.Width; return p.Y <= vs.Top;
            default: along = (p.X - vs.Left) / (float)vs.Width; return p.Y >= vs.Bottom - 1;
        }
    }

    private bool AnyButtonDown()
    {
        foreach (var o in _buttonOwner) if (o != None) return true;
        return false;
    }

    // ---- Hooks ----------------------------------------------------------

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        try
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((m.flags & LLMHF_INJECTED) == 0 && HandleMouse((int)wParam, m))
                return 1; // swallowed
        }
        catch (Exception ex)
        {
            Log.Error("Mouse hook", ex);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <returns>true to swallow the event locally.</returns>
    private bool HandleMouse(int msg, MSLLHOOKSTRUCT m)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (!_remote)
                {
                    // No switching mid-drag, so snapping a window to the edge still works.
                    if (!AnyButtonDown() && HitsEdge(m.pt, out float along) && _isConnected())
                    {
                        EnterRemote(center: false, along);
                        return true;
                    }
                    return false;
                }
                // Measure from where the cursor really is, not from where we parked it:
                // a game on this PC may keep moving it back to the middle of its window.
                if (!GetCursorPos(out var cur)) cur = _park;
                int dx = m.pt.X - cur.X, dy = m.pt.Y - cur.Y;
                if (dx != 0 || dy != 0) { _send(Protocol.Move(dx, dy)); Power.NoteForwardedInput(); }
                KeepParked(cur);
                return true; // the local cursor stays put

            case WM_LBUTTONDOWN: return Button(0, true);
            case WM_LBUTTONUP: return Button(0, false);
            case WM_RBUTTONDOWN: return Button(1, true);
            case WM_RBUTTONUP: return Button(1, false);
            case WM_MBUTTONDOWN: return Button(2, true);
            case WM_MBUTTONUP: return Button(2, false);
            case WM_XBUTTONDOWN: return Button((m.mouseData >> 16) == 2 ? 4 : 3, true);
            case WM_XBUTTONUP: return Button((m.mouseData >> 16) == 2 ? 4 : 3, false);

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (!_remote) return false;
                _send(Protocol.Wheel((short)(m.mouseData >> 16), msg == WM_MOUSEHWHEEL));
                return true;
        }
        return false;
    }

    /// <summary>
    /// Keeps the cursor away from the screen edges and out of a game's mouse
    /// fence while the other PC has control. Otherwise movement towards an edge
    /// or fence would get cut off.
    /// </summary>
    private void KeepParked(POINT cur)
    {
        if (GetClipCursor(out var clip))
        {
            var vs = VirtualScreen();
            if (clip.Left > vs.Left || clip.Top > vs.Top || clip.Right < vs.Right || clip.Bottom < vs.Bottom)
                ClipCursor(IntPtr.Zero);
        }
        if (Math.Abs(cur.X - _park.X) > 100 || Math.Abs(cur.Y - _park.Y) > 100)
            SetCursorPos(_park.X, _park.Y);
    }

    private bool Button(int index, bool down)
    {
        if (down && SwitchKey.IsMouse && index == SwitchKey.Button && _buttonOwner[index] == None && ModsMatch())
        {
            _buttonOwner[index] = Swallow; // and its release goes nowhere too
            Log.Info($"Switch key ({SwitchKey}) pressed");
            Toggle();
            return true;
        }
        if (_buttonOwner[index] == Swallow)
        {
            if (!down) _buttonOwner[index] = None;
            return true;
        }

        byte owner = _buttonOwner[index];
        if (owner == None) owner = _remote ? Remote : Local;
        _buttonOwner[index] = down ? owner : None;
        if (owner != Remote) return false;
        _send(Protocol.Button(index, down));
        return true;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0 && HandleKey(k))
                return 1;
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook", ex);
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private bool HandleKey(KBDLLHOOKSTRUCT k)
    {
        int vk = (int)(k.vkCode & 0xFF);
        bool up = (k.flags & LLKHF_UP) != 0;

        if (!up && !SwitchKey.IsMouse && vk == SwitchKey.Vk && _keyOwner[vk] == None && ModsMatch())
        {
            // The switch key. The key itself goes nowhere, and neither does its release.
            _keyOwner[vk] = Swallow;
            _keyDown[vk] = true;
            Log.Info($"Switch key ({SwitchKey}) pressed");
            Toggle();
            return true;
        }

        byte owner = _keyOwner[vk];
        if (owner == None) owner = _remote ? Remote : Local;
        _keyOwner[vk] = up ? None : owner;
        _keyDown[vk] = !up;

        if (owner == Swallow) return true;
        if (owner != Remote) return false;
        _send(Protocol.Key(vk, (int)k.scanCode, up, (k.flags & LLKHF_EXTENDED) != 0));
        Power.NoteForwardedInput();
        return true;
    }

    /// <summary>Exactly the switch key's modifiers are held (no more, no fewer).</summary>
    private bool ModsMatch()
    {
        int held = (_keyDown[VK_LCONTROL] || _keyDown[VK_RCONTROL] ? SwitchKey.Ctrl : 0) |
                   (_keyDown[VK_LMENU] || _keyDown[VK_RMENU] ? SwitchKey.Alt : 0) |
                   (_keyDown[VK_LSHIFT] || _keyDown[VK_RSHIFT] ? SwitchKey.Shift : 0) |
                   (_keyDown[VK_LWIN] || _keyDown[VK_RWIN] ? SwitchKey.Win : 0);
        return held == SwitchKey.Mods;
    }

    public void Dispose()
    {
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}
