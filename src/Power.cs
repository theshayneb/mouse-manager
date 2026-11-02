namespace KeyShare;

/// <summary>
/// Keeps the two PCs on one idle timer. While you use either PC, it tells the
/// other one every so often, and the other one resets its own screen-off and
/// sleep timers. Walk away from both and they time out and lock as usual.
/// </summary>
internal static class Power
{
    /// <summary>How recent input must be to count as "in use".</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);

    private static volatile bool _enabled = true;
    private static long _lastForwarded;

    public static bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>The host calls this for input it forwards, which Windows may not count as use of the host.</summary>
    public static void NoteForwardedInput() => Interlocked.Exchange(ref _lastForwarded, Environment.TickCount64);

    /// <summary>Has someone used this PC (directly or through KeyShare) within <see cref="ActiveWindow"/>?</summary>
    public static bool InUse()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastForwarded) < ActiveWindow.TotalMilliseconds) return true;

        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return false;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return idleMs < ActiveWindow.TotalMilliseconds;
    }

    /// <summary>The other PC is in use: reset this PC's display and sleep timers once.</summary>
    /// <remarks>
    /// This doesn't count as input, so it never makes this PC report "in use"
    /// back. That keeps the two from holding each other awake forever.
    /// </remarks>
    public static void OtherPcActive()
    {
        if (_enabled) Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);
    }
}
