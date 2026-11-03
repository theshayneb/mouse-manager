using System.Text.Json;
using Microsoft.Win32;

namespace KeyShare;

internal sealed class Settings
{
    public bool IsHost { get; set; } = true;
    public string HostCode { get; set; } = "";
    public Side OtherSide { get; set; } = Side.Right;
    public string JoinCode { get; set; } = "";
    public string JoinAddress { get; set; } = "";
    public bool ShareClipboard { get; set; } = true;
    public bool StayAwakeTogether { get; set; } = true;
    public int SwitchMods { get; set; } = SwitchKey.Default.Mods;
    public int SwitchVk { get; set; } = SwitchKey.Default.Vk;
    public int SwitchButton { get; set; } = SwitchKey.Default.Button;

    [System.Text.Json.Serialization.JsonIgnore]
    public SwitchKey SwitchKey
    {
        get { var k = new SwitchKey(SwitchMods, SwitchVk, SwitchButton); return k.IsValid ? k : SwitchKey.Default; }
        set { SwitchMods = value.Mods; SwitchVk = value.Vk; SwitchButton = value.Button; }
    }
    /// <summary>Was sharing/connected when the app closed; resume on next start.</summary>
    public bool Running { get; set; }

    private static readonly string FilePath = Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("Reading settings", ex);
            s = new Settings();
        }
        if (!Protocol.IsValidCode(Protocol.Normalize(s.HostCode))) s.HostCode = Protocol.NewCode();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings", ex);
        }
    }

    // ---- Start with Windows (per-user Run key, no admin needed) ----------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "KeyShare";

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue(RunValue, false);
        }
    }
}
