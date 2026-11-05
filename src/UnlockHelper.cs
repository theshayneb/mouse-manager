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
    public const byte OpNone = 0;   // liveness check, ignored
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
        Log.Info("Unlock helper started");

        // The main thread is STA, and COM gives STA threads a hidden window.
        // Windows refuses to move a thread that owns a window to another desktop
        // (SetThreadDesktop fails), so the helper could never reach the sign-in
        // box. It runs on a fresh thread instead, which owns no windows.
        var worker = new Thread(ServeLoop) { IsBackground = false, Name = "unlock helper" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
    }

    private static void ServeLoop()
    {
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
                Log.Info("Unlock helper: KeyShare connected");
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

            if (r[0] == UnlockPipe.OpNone) continue; // "are you there?" check
            if (!IsLocked()) continue; // only ever types on the lock screen
            FollowInputDesktop();
            _replayed++;

            int a = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(1));
            int b = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5));
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(9));
            uint d = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(13));
            switch (r[0])
            {
                case UnlockPipe.OpKey:
                    SendChecked(new Native.INPUT
                    {
                        type = Native.INPUT_KEYBOARD,
                        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)a, wScan = (ushort)b, dwFlags = c } },
                    });
                    break;
                case UnlockPipe.OpMouse:
                    SendChecked(new Native.INPUT
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

    private static int _replayed;
    private static bool _loggedFailure;

    private static void SendChecked(Native.INPUT input)
    {
        if (Native.SendChecked(input) || _loggedFailure) return;
        _loggedFailure = true; // once per lock, so the log stays readable
        Log.Info($"Unlock helper: SendInput failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}) on desktop '{_desktopName}'");
    }

    /// <summary>Is this session locked? Asks Windows directly, and also accepts LogonUI being up.</summary>
    private static bool IsLocked()
    {
        long now = Environment.TickCount64;
        if (now - _lockCheckedAt > 500)
        {
            int session = Process.GetCurrentProcess().SessionId;
            bool locked = SessionReportsLocked((uint)session);
            if (!locked)
            {
                var procs = Process.GetProcessesByName("LogonUI");
                locked = procs.Any(p => p.SessionId == session);
                foreach (var p in procs) p.Dispose();
            }
            if (locked != _locked)
            {
                Log.Info(locked ? "Unlock helper: session locked, typing on the lock screen" : $"Unlock helper: session unlocked ({_replayed} inputs replayed)");
                _replayed = 0;
                _loggedFailure = false;
            }
            _locked = locked;
            _lockCheckedAt = now;
        }
        return _locked;
    }

    private static bool SessionReportsLocked(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, WTSSessionInfoEx, out var buffer, out int bytes)) return false;
        try
        {
            // WTSINFOEXW: Level (4 bytes), then WTSINFOEX_LEVEL1: SessionId, SessionState, SessionFlags.
            if (bytes < 16 || System.Runtime.InteropServices.Marshal.ReadInt32(buffer) != 1) return false;
            int flags = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 12);
            return flags == WTS_SESSIONSTATE_LOCK;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string _desktopName = "?";

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
            string name = DesktopName(d);
            if (name != _desktopName) Log.Info($"Unlock helper: now on desktop '{name}'");
            _desktopName = name;
        }
        else
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            CloseDesktop(d);
            if (!_loggedFailure) { _loggedFailure = true; Log.Info($"Unlock helper: can't switch desktop (error {err})"); }
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

    public static bool SessionLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            if (value) Log.Info(TryConnect() ? "Locked: passing input to the lock-screen helper" : "Locked, but the lock-screen helper isn't reachable");
        }
    }

    /// <summary>Is the lock-screen helper running and connected?</summary>
    public static bool HelperConnected
    {
        get { lock (Gate) return _pipe is { IsConnected: true }; }
    }

    /// <summary>Connects to the helper ahead of time, so it's ready the moment the PC locks.</summary>
    public static bool TryConnect()
    {
        lock (Gate)
        {
            if (_pipe is { IsConnected: true })
            {
                // Check it's really still there (a pipe only notices when a write fails).
                try { _pipe.Write(UnlockPipe.Record(UnlockPipe.OpNone, 0, 0, 0, 0)); _pipe.Flush(); return true; }
                catch (IOException) { }
            }
            _pipe?.Dispose();
            _pipe = null;
            _retryAt = 0;
            return Connect();
        }
    }

    private static bool Connect()
    {
        if (Environment.TickCount64 < _retryAt) return false;
        var p = new NamedPipeClientStream(".", UnlockPipe.NameFor(Process.GetCurrentProcess().SessionId), PipeDirection.Out);
        try { p.Connect(100); _pipe = p; return true; }
        catch
        {
            p.Dispose();
            _retryAt = Environment.TickCount64 + 3000; // service probably not installed
            return false;
        }
    }

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
            if (_pipe == null && !Connect()) return false;
            try
            {
                _pipe!.Write(record);
                _pipe.Flush();
                return true;
            }
            catch (IOException)
            {
                _pipe?.Dispose();
                _pipe = null;
                return false;
            }
        }
    }
}
