using System.Runtime.InteropServices;

namespace KeyShare;

/// <summary>The key or mouse button (plus optional modifiers) that switches PCs.</summary>
internal sealed record SwitchKey(int Mods, int Vk, int Button)
{
    public const int Ctrl = 1, Alt = 2, Shift = 4, Win = 8;
    public const int ButtonMiddle = 2, ButtonBack = 3, ButtonForward = 4; // InputCapture button indexes

    public static readonly SwitchKey Default = new(Ctrl | Alt, 0x7B /* F12 */, 0);

    public bool IsMouse => Button != 0;

    public bool IsValid =>
        (Vk is > 0 and < 256 && Button == 0 && !IsModifierVk(Vk)) ||
        (Vk == 0 && Button is ButtonMiddle or ButtonBack or ButtonForward);

    public static bool IsModifierVk(int vk) =>
        vk is >= 0x10 and <= 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    /// <summary>Plain typing keys that would be lost for typing if used alone.</summary>
    public bool StealsTypingKey =>
        Mods == 0 && !IsMouse &&
        (Vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or 0x20 or 0x0D or 0x08 or 0x09 or >= 0xBA and <= 0xE2);

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((Mods & Alt) != 0) parts.Add("Alt");
        if ((Mods & Shift) != 0) parts.Add("Shift");
        if ((Mods & Win) != 0) parts.Add("Win");
        parts.Add(Button switch
        {
            ButtonMiddle => "Middle mouse button",
            ButtonBack => "Mouse Back button",
            ButtonForward => "Mouse Forward button",
            _ => KeyName(Vk),
        });
        return string.Join("+", parts);
    }

    private static string KeyName(int vk) => vk switch
    {
        0x91 => "Scroll Lock",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x90 => "Num Lock",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x5D => "Menu",
        0xC0 => "`",
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x09 => "Tab",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        _ => ((Keys)vk).ToString(),
    };

    // RegisterHotKey flags, for the backup path.
    public uint HotKeyModifiers =>
        ((Mods & Alt) != 0 ? 0x1u : 0) | ((Mods & Ctrl) != 0 ? 0x2u : 0) |
        ((Mods & Shift) != 0 ? 0x4u : 0) | ((Mods & Win) != 0 ? 0x8u : 0) | 0x4000u /* MOD_NOREPEAT */;
}

/// <summary>"Press the key you want" dialog.</summary>
internal sealed class SwitchKeyDialog : Form
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private readonly Label _prompt = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f),
        Text = "Press the key you want to switch PCs with.\n" +
               "Hold Ctrl, Alt, Shift or Win with it if you like.\n" +
               "You can also click the middle, Back or Forward mouse button here.\n\n" +
               "Good choices for games: Scroll Lock, Pause, or a side mouse button.\n\n" +
               "Esc cancels.",
    };

    public SwitchKey? Result { get; private set; }

    public SwitchKeyDialog()
    {
        Text = "Choose switch key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 220);
        KeyPreview = true;
        Controls.Add(_prompt);
        _prompt.MouseDown += (_, e) => OnMouse(e.Button);
        MouseDown += (_, e) => OnMouse(e.Button);
    }

    private static int HeldMods()
    {
        bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        return (Down(0x11) ? SwitchKey.Ctrl : 0) | (Down(0x12) ? SwitchKey.Alt : 0) |
               (Down(0x10) ? SwitchKey.Shift : 0) | (Down(0x5B) || Down(0x5C) ? SwitchKey.Win : 0);
    }

    private void OnMouse(MouseButtons b)
    {
        int button = b switch
        {
            MouseButtons.Middle => SwitchKey.ButtonMiddle,
            MouseButtons.XButton1 => SwitchKey.ButtonBack,
            MouseButtons.XButton2 => SwitchKey.ButtonForward,
            _ => 0,
        };
        if (button != 0) Pick(new SwitchKey(HeldMods(), 0, button));
    }

    // ProcessCmdKey sees every key, including Tab, arrows, Enter and Alt combos.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (msg.Msg is not (WM_KEYDOWN or WM_SYSKEYDOWN)) return base.ProcessCmdKey(ref msg, keyData);
        int vk = (int)(keyData & Keys.KeyCode);
        int mods = HeldMods();
        if (vk == (int)Keys.Escape && mods == 0) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (SwitchKey.IsModifierVk(vk)) return true; // wait for the real key
        Pick(new SwitchKey(mods, vk, 0));
        return true;
    }

    private void Pick(SwitchKey key)
    {
        if (!key.IsValid) return;
        if (key.StealsTypingKey &&
            MessageBox.Show(this, $"{key} on its own would stop typing that key on this PC. Use it anyway?",
                "KeyShare", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        if (key is { IsMouse: true, Mods: 0, Button: SwitchKey.ButtonMiddle } &&
            MessageBox.Show(this, "The middle button on its own would stop middle-clicking on this PC. Use it anyway?",
                "KeyShare", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        Result = key;
        DialogResult = DialogResult.OK;
        Close();
    }
}
