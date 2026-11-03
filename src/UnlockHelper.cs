using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using static KeyShare.UnlockNative;

namespace KeyShare;

/// <summary>
/// Pipe format shared by the KeyShare app (sender) and the helper (receiver):
/// fixed 17-byte records of [op][int32 a][int32 b][uint32 c][uint32 d].
/// </summary>
internal static class UnlockPipe
{
    public const int RecordSize = 17;
    public const byte OpKey = 1;    // a = vk, b = scan, c = KEYEVENTF_* flags
    public const byte OpMouse = 2;  // a = dx, b = dy, c = mouseData, d = MOUSEEVENTF_* flags
    public const byte OpCursor = 3; // a = x, b = y

    public static string NameFor(int session) => $"KeyShare-unlock-{session}";

    public static byte[] Record(byte op, int a, int b, uint c, uint d)
    {
        var r = new byte[RecordSize];
        r[0] = op;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(1), a);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(5), b);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(9), c);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(13), d);
        return r;
    }
}

/// <summary>
/// Runs as SYSTEM in the user's session (started by <see cref="UnlockServiceHost"/>).
/// Takes input from the KeyShare app over a named pipe and types it on the lock
/// screen. Only the signed-in user can open the pipe, and input is only
/// replayed while the session is actually locked, so it can't be used to click
/// through admin (UAC) prompts or control elevated windows.
/// </summary>
internal static class UnlockHelper
{
    private static IntPtr _desktop;
    private static long _desktopCheckedAt;
    private static bool _locked;
    private static long _lockCheckedAt;

    public static void Run()
    {
        using var single = new Mutex(true, @"Local\KeyShare-unlock-helper", out bool first);
        if (!first) return;

        int session = Process.GetCurrentProcess().SessionId;
        while (true)
        {
            try
            {
                var user = SignedInUser((uint)session);
                if (user == null) { Thread.Sleep(2000); continue; } // nobody signed in yet

                using var pipe = NamedPipeServerStreamAcl.Create(UnlockPipe.NameFor(session), PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, Security(user));
                pipe.WaitForConnection();
                Serve(pipe);
            }
            catch (Exception ex)
            {
                Log.Error("Unlock helper", ex);
                Thread.Sleep(1000);
            }
        }
    }

    private static SecurityIdentifier? SignedInUser(uint session)
    {
        if (!WTSQueryUserToken(session, out var token)) return null;
        try { using var id = new WindowsIdentity(token); return id.User; }
        finally { CloseHandle(token); }
    }

    private static PipeSecurity Security(SecurityIdentifier user)
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        var r = new byte[UnlockPipe.RecordSize];
        while (true)
        {
            try { pipe.ReadExactly(r); }
            catch (EndOfStreamException) { return; }
            catch (IOException) { return; }

            if (!IsLocked()) continue; // only ever types on the lock screen
            FollowInputDesktop();

            int a = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(1));
            int b = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5));
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(9));
            uint d = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(13));
            switch (r[0])
            {
                case UnlockPipe.OpKey:
                    Native.Send(new Native.INPUT
                    {
                        type = Native.INPUT_KEYBOARD,
                        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)a, wScan = (ushort)b, dwFlags = c } },
                    });
                    break;
                case UnlockPipe.OpMouse:
                    Native.Send(new Native.INPUT
                    {
                        type = Native.INPUT_MOUSE,
                        u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = a, dy = b, mouseData = c, dwFlags = d } },
                    });
                    break;
                case UnlockPipe.OpCursor:
                    Native.SetCursorPos(a, b);
                    break;
            }
        }
    }

    /// <summary>Is the lock screen up? (LogonUI runs in the session only while it is.)</summary>
    private static bool IsLocked()
    {
        long now = Environment.TickCount64;
        if (now - _lockCheckedAt > 500)
        {
            int session = Process.GetCurrentProcess().SessionId;
            var procs = Process.GetProcessesByName("LogonUI");
            _locked = procs.Any(p => p.SessionId == session);
            foreach (var p in procs) p.Dispose();
            _lockCheckedAt = now;
        }
        return _locked;
    }

    /// <summary>Input only reaches the desktop it's sent from, so switch to the one on screen (the lock screen's).</summary>
    private static void FollowInputDesktop()
    {
        long now = Environment.TickCount64;
        if (_desktop != IntPtr.Zero && now - _desktopCheckedAt < 250) return;
        _desktopCheckedAt = now;
        IntPtr d = OpenInputDesktop(0, false, GENERIC_ALL);
        if (d == IntPtr.Zero) return;
        if (SetThreadDesktop(d))
        {
            if (_desktop != IntPtr.Zero) CloseDesktop(_desktop);
            _desktop = d;
        }
        else
        {
            CloseDesktop(d);
        }
    }
}

/// <summary>
/// Where the app's injected input goes: straight to Windows normally, or to the
/// lock-screen helper while this PC is locked (if the service is installed).
/// </summary>
internal static class InputSink
{
    private static readonly object Gate = new();
    private static volatile bool _locked;
    private static NamedPipeClientStream? _pipe;
    private static long _retryAt;

    public static bool SessionLocked { get => _locked; set => _locked = value; }

    public static void Send(Native.INPUT input)
    {
        if (_locked)
        {
            var rec = input.type == Native.INPUT_KEYBOARD
                ? UnlockPipe.Record(UnlockPipe.OpKey, input.u.ki.wVk, input.u.ki.wScan, input.u.ki.dwFlags, 0)
                : UnlockPipe.Record(UnlockPipe.OpMouse, input.u.mi.dx, input.u.mi.dy, input.u.mi.mouseData, input.u.mi.dwFlags);
            if (ToHelper(rec)) return;
        }
        Native.Send(input);
    }

    public static void SetCursor(int x, int y)
    {
        if (_locked && ToHelper(UnlockPipe.Record(UnlockPipe.OpCursor, x, y, 0, 0))) return;
        Native.SetCursorPos(x, y);
    }

    private static bool ToHelper(byte[] record)
    {
        lock (Gate)
        {
            if (_pipe == null)
            {
                if (Environment.TickCount64 < _retryAt) return false;
                var p = new NamedPipeClientStream(".", UnlockPipe.NameFor(Process.GetCurrentProcess().SessionId), PipeDirection.Out);
                try { p.Connect(100); _pipe = p; }
                catch
                {
                    p.Dispose();
                    _retryAt = Environment.TickCount64 + 3000; // service probably not installed
                    return false;
                }
            }
            try
            {
                _pipe.Write(record);
                _pipe.Flush();
                return true;
            }
            catch (IOException)
            {
                _pipe.Dispose();
                _pipe = null;
                return false;
            }
        }
    }
}
