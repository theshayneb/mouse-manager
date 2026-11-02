namespace KeyShare;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\KeyShare-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("KeyShare is already running. Look for its icon in the system tray.", "KeyShare",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Stay alive no matter what: log the problem and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Background task", e.Exception); e.SetObserved(); };

        // Per-monitor DPI awareness keeps hook and cursor coordinates in real pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Log.Info("KeyShare started");
        Application.Run(new MainForm(startMinimized: args.Contains("--minimized")));
    }
}
