using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using static KeyShare.UnlockNative;

namespace KeyShare;

/// <summary>
/// Optional Windows service that lets the other PC type on this PC's lock screen.
///
/// Normal apps can't send input to the lock screen; Windows only allows that
/// for SYSTEM. The service runs as SYSTEM and keeps one small helper running in
/// the signed-in user's session (<see cref="UnlockHelper"/>). While this PC is
/// locked, the KeyShare app hands the keys it receives to that helper instead
/// of typing them itself.
///
/// The service runs from a copy in Program Files, which only administrators can
/// change, so nobody can swap in a different program to run as SYSTEM.
/// </summary>
internal static class UnlockService
{
    public const string Name = "KeyShareUnlock";
    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KeyShare");
    private static string InstalledExe => Path.Combine(InstallDir, "KeyShare.exe");

    public static bool IsInstalled()
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName == Name); }
        catch { return false; }
    }

    /// <summary>Runs this exe with <paramref name="argument"/> as administrator (shows the UAC prompt) and waits.</summary>
    /// <returns>true on success, false if cancelled or failed.</returns>
    public static async Task<bool> RunElevatedAsync(string argument)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, argument) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user said no at the UAC prompt
        }
    }

    // ---- Install / uninstall (run elevated, from the command line) -------

    public static int InstallFromCommandLine()
    {
        try
        {
            StopIfRunning();
            Directory.CreateDirectory(InstallDir);
            string source = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                CopyWithRetry(source, InstalledExe);

            string binPath = $"binPath= \"\\\"{InstalledExe}\\\" --service\"";
            if (IsInstalled()) Sc($"config {Name} {binPath} start= auto");
            else Sc($"create {Name} {binPath} start= auto DisplayName= \"KeyShare unlock helper\"");
            Sc($"description {Name} \"Lets KeyShare type on this PC's lock screen. Turn it off in KeyShare.\"");
            Sc($"failure {Name} reset= 86400 actions= restart/5000/restart/5000/restart/60000");

            using var sc = new ServiceController(Name);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            Log.Info("Unlock service installed");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Installing unlock service", ex);
            MessageBox.Show("Couldn't set up lock-screen typing:\n\n" + ex.Message, "KeyShare", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static int UninstallFromCommandLine()
    {
        try
        {
            StopIfRunning();
            if (IsInstalled()) Sc($"delete {Name}");
            for (int i = 0; i < 20 && File.Exists(InstalledExe); i++)
            {
                try { File.Delete(InstalledExe); }
                catch (Exception) when (i < 19) { Thread.Sleep(250); }
            }
            try { Directory.Delete(InstallDir); } catch { }
            Log.Info("Unlock service removed");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Removing unlock service", ex);
            MessageBox.Show("Couldn't remove lock-screen typing:\n\n" + ex.Message, "KeyShare", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void StopIfRunning()
    {
        if (!IsInstalled()) return;
        using var sc = new ServiceController(Name);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        try { sc.Stop(); } catch (InvalidOperationException) { }
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        Thread.Sleep(500); // let the helper process exit and release the exe
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }
        }
    }

    private static void Sc(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc {arguments.Split(' ')[0]} failed ({p.ExitCode}): {output.Trim()}");
    }
}

/// <summary>The service itself: keeps a helper running in whichever session is on the screen.</summary>
internal sealed class UnlockServiceHost : ServiceBase
{
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private Process? _helper;
    private uint _helperSession = NoSession;

    public UnlockServiceHost()
    {
        ServiceName = UnlockService.Name;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "helper watchdog" };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _stop.Set();
        _thread?.Join(5000);
        KillHelper();
    }

    private void Loop()
    {
        do
        {
            try
            {
                uint session = WTSGetActiveConsoleSessionId();
                bool valid = session != NoSession && session != 0;
                if (!valid || session != _helperSession || _helper == null || _helper.HasExited)
                {
                    KillHelper();
                    if (valid) Launch(session);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Unlock service", ex);
            }
        }
        while (!_stop.WaitOne(3000));
    }

    /// <summary>Starts the helper as SYSTEM inside <paramref name="session"/>.</summary>
    private void Launch(uint session)
    {
        IntPtr token = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out token)) throw new Win32Exception();
            if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup)) throw new Win32Exception();
            if (!SetTokenInformation(dup, TokenSessionId, ref session, sizeof(uint))) throw new Win32Exception();

            string exe = Environment.ProcessPath!;
            var cmd = new StringBuilder($"\"{exe}\" --unlock-helper");
            var si = new STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUser(dup, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW,
                    IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            _helper = Process.GetProcessById(pi.dwProcessId);
            _helperSession = session;
        }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private void KillHelper()
    {
        try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { }
        _helper?.Dispose();
        _helper = null;
        _helperSession = NoSession;
    }
}
