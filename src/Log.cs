namespace KeyShare;

/// <summary>Tiny file log in %APPDATA%\KeyShare\log.txt, capped at ~1 MB.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyShare");
    private static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
