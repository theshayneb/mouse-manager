namespace KeyShare;

internal sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly bool _startMinimized;

    private readonly RadioButton _rbHost = new() { Text = "Share this PC's keyboard and mouse", AutoSize = true };
    private readonly RadioButton _rbJoin = new() { Text = "Control this PC from the other one", AutoSize = true };

    private readonly GroupBox _hostBox = new() { Text = "Share", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
    private readonly Label _lblCode = new() { AutoSize = true, Font = new Font("Consolas", 22f, FontStyle.Bold), Margin = new Padding(3, 3, 12, 3) };
    private readonly Button _btnCopy = new() { Text = "Copy", AutoSize = true };
    private readonly Button _btnNewCode = new() { Text = "New code", AutoSize = true };
    private readonly ComboBox _cbSide = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Button _btnHost = new() { AutoSize = true, MinimumSize = new Size(140, 0) };

    private readonly GroupBox _joinBox = new() { Text = "Join", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
    private readonly TextBox _tbCode = new() { Width = 160, CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 14f), PlaceholderText = "XXXX-XXXX" };
    private readonly TextBox _tbAddress = new() { Width = 160, PlaceholderText = "automatic" };
    private readonly Button _btnJoin = new() { AutoSize = true, MinimumSize = new Size(140, 0) };

    private readonly Label _lblStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 10, 3, 6) };
    private readonly CheckBox _cbStartup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly CheckBox _cbClipboard = new() { Text = "Share clipboard (text and images)", AutoSize = true };

    private readonly ClipboardSync _clip = new();
    private readonly System.Windows.Forms.Timer _clipTimer = new() { Interval = 200 };
    private long _ignoreClipboardUntil;
    private volatile bool _shareClipboard;
    private readonly Label _lblTip = new()
    {
        AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText,
        Text = "Move the mouse off the screen edge to switch PCs, or press Ctrl+Alt+F12. " +
               "Minimizing sends KeyShare to the tray.",
    };

    private readonly NotifyIcon _tray = new();
    private HostService? _host;
    private InputCapture? _capture;
    private ClientService? _client;
    private string _baseStatus = "Not running.";

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        Text = "KeyShare";
        Icon = LoadIcon(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.75f);

        BuildLayout();

        _tray.Icon = LoadIcon(SystemInformation.SmallIconSize);
        _tray.Text = "KeyShare";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open KeyShare", null, (_, _) => ShowFromTray());
        menu.Items.Add("Switch PC  (Ctrl+Alt+F12)", null, (_, _) => _capture?.Toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        _lblCode.Text = _settings.HostCode;
        _cbSide.Items.AddRange(["Left", "Right", "Above", "Below"]);
        _cbSide.SelectedIndex = (int)_settings.OtherSide;
        _tbCode.Text = _settings.JoinCode;
        _tbAddress.Text = _settings.JoinAddress;
        (_settings.IsHost ? _rbHost : _rbJoin).Checked = true;
        try { _cbStartup.Checked = Settings.StartsWithWindows; } catch { }
        _cbClipboard.Checked = _shareClipboard = _settings.ShareClipboard;
        _cbClipboard.CheckedChanged += (_, _) =>
        {
            _shareClipboard = _settings.ShareClipboard = _cbClipboard.Checked;
            _settings.Save();
        };
        _clipTimer.Tick += (_, _) => SendClipboard();

        _rbHost.CheckedChanged += (_, _) => { if (_rbHost.Checked) SwitchRole(true); };
        _rbJoin.CheckedChanged += (_, _) => { if (_rbJoin.Checked) SwitchRole(false); };
        _btnCopy.Click += (_, _) => { try { Clipboard.SetText(_settings.HostCode); } catch { } };
        _btnNewCode.Click += (_, _) => NewCode();
        _cbSide.SelectedIndexChanged += (_, _) =>
        {
            _settings.OtherSide = (Side)_cbSide.SelectedIndex;
            if (_capture != null) _capture.OtherSide = _settings.OtherSide;
            _settings.Save();
        };
        _btnHost.Click += (_, _) => { if (_host == null) StartHost(); else StopAll(); };
        _btnJoin.Click += (_, _) => { if (_client == null) StartClient(); else StopAll(); };
        _tbCode.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_client == null) StartClient(); } };
        _cbStartup.CheckedChanged += (_, _) =>
        {
            try { Settings.StartsWithWindows = _cbStartup.Checked; }
            catch (Exception ex) { Log.Error("Startup setting", ex); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };

        UpdateUi();
    }

    private void BuildLayout()
    {
        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

        root.Controls.Add(_rbHost);
        root.Controls.Add(_rbJoin);

        var hostGrid = Grid();
        hostGrid.Controls.Add(Caption("Share code"), 0, 0);
        var codeRow = Row(_lblCode, _btnCopy, _btnNewCode);
        hostGrid.Controls.Add(codeRow, 1, 0);
        hostGrid.Controls.Add(Caption("Other PC is"), 0, 1);
        hostGrid.Controls.Add(Row(_cbSide, new Label { Text = "of this screen", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }), 1, 1);
        hostGrid.Controls.Add(_btnHost, 1, 2);
        _hostBox.Controls.Add(hostGrid);
        root.Controls.Add(_hostBox);

        var joinGrid = Grid();
        joinGrid.Controls.Add(Caption("Share code"), 0, 0);
        joinGrid.Controls.Add(_tbCode, 1, 0);
        joinGrid.Controls.Add(Caption("Host address"), 0, 1);
        joinGrid.Controls.Add(Row(_tbAddress, new Label { Text = "(optional IP)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3) }), 1, 1);
        joinGrid.Controls.Add(_btnJoin, 1, 2);
        _joinBox.Controls.Add(joinGrid);
        root.Controls.Add(_joinBox);

        root.Controls.Add(_lblStatus);
        root.Controls.Add(_cbClipboard);
        root.Controls.Add(_cbStartup);
        root.Controls.Add(_lblTip);
        Controls.Add(root);

        foreach (var box in new[] { _hostBox, _joinBox }) box.MinimumSize = new Size(440, 0);
    }

    private static TableLayoutPanel Grid() => new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in controls) { c.Anchor = AnchorStyles.Left; p.Controls.Add(c); }
        return p;
    }

    protected override void SetVisibleCore(bool value)
    {
        // Start in the tray when launched with Windows.
        if (_startMinimized && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
            OnShownOnce();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        OnShownOnce();
    }

    private bool _resumed;

    private void OnShownOnce()
    {
        if (_resumed) return;
        _resumed = true;
        if (!_settings.Running) return;
        BeginInvoke(() =>
        {
            if (_settings.IsHost) StartHost();
            else if (Protocol.IsValidCode(Protocol.Normalize(_settings.JoinCode))) StartClient();
        });
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---- Roles ----------------------------------------------------------

    private void SwitchRole(bool host)
    {
        if (_settings.IsHost == host) { UpdateUi(); return; }
        StopAll();
        _settings.IsHost = host;
        _settings.Save();
        UpdateUi();
    }

    private void NewCode()
    {
        bool wasRunning = _host != null;
        if (wasRunning) StopAll();
        _settings.HostCode = Protocol.NewCode();
        _settings.Save();
        _lblCode.Text = _settings.HostCode;
        if (wasRunning) StartHost();
    }

    private void StartHost()
    {
        StopAll();
        try
        {
            var host = new HostService(_settings.HostCode);
            var capture = new InputCapture(() => host.IsConnected, host.Send) { OtherSide = _settings.OtherSide };
            host.Status += s => OnUi(() => SetStatus(s));
            host.Connected += name => OnUi(() =>
            {
                SetStatus($"Connected to {name}. Move the mouse to the {SideWord()} edge to control it.");
                _tray.ShowBalloonTip(3000, "KeyShare", $"{name} connected.", ToolTipIcon.Info);
            });
            host.Disconnected += () => OnUi(() =>
            {
                _capture?.ForceLocal();
                SetStatus("The other PC disconnected. Waiting for it to come back…");
            });
            host.ReturnRequested += along => OnUi(() => _capture?.ReturnToLocal(along, tellOther: false));
            host.ClipboardChunk += ReceiveClipboard;
            capture.RemoteChanged += remote => OnUi(() => SetRemote(remote));

            host.Start();
            _host = host;
            _capture = capture;
            _settings.Running = true;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Start sharing", ex);
            StopAll();
            SetStatus(ex is System.Net.Sockets.SocketException
                ? $"Port {Protocol.TcpPort} is in use. Is another copy of KeyShare (or another app) using it?"
                : $"Couldn't start: {ex.Message}");
        }
        UpdateUi();
    }

    private void StartClient()
    {
        string code = Protocol.Normalize(_tbCode.Text);
        _tbCode.Text = code;
        if (!Protocol.IsValidCode(code))
        {
            SetStatus("Enter the 8-character share code shown on the other PC.");
            _tbCode.Focus();
            return;
        }
        StopAll();
        _settings.JoinCode = code;
        _settings.JoinAddress = _tbAddress.Text.Trim();
        _settings.Running = true;
        _settings.Save();

        var client = new ClientService(code, _settings.JoinAddress);
        client.Status += s => OnUi(() => SetStatus(s));
        client.ClipboardChunk += ReceiveClipboard;
        client.ActiveChanged += active => OnUi(() => _tray.Text = active ? "KeyShare: controlled remotely" : "KeyShare");
        _client = client;
        client.Start();
        UpdateUi();
    }

    private void StopAll()
    {
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        _client?.Dispose();
        _client = null;
        _settings.Running = false;
        _settings.Save();
        SetStatus("Not running.");
        UpdateUi();
    }

    // ---- Clipboard -------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE && Environment.TickCount64 >= _ignoreClipboardUntil)
        {
            // Apps often update the clipboard several times in a row; send once it settles.
            _clipTimer.Stop();
            _clipTimer.Start();
        }
        base.WndProc(ref m);
    }

    private void SendClipboard()
    {
        _clipTimer.Stop();
        if (!_shareClipboard) return;
        Action<byte[]>? send = null;
        if (_host is { IsConnected: true } h) send = h.SendBulk;
        else if (_client is { IsConnected: true } c) send = c.SendBulk;
        if (send == null) return;

        string? text, rtf;
        Image? image;
        try { (text, rtf, image) = ClipboardSync.Read(); }
        catch (Exception ex) { Log.Error("Reading clipboard", ex); return; }

        _ = Task.Run(() =>
        {
            try
            {
                var chunks = _clip.Pack(text, rtf, image);
                if (chunks != null) foreach (var chunk in chunks) send(chunk);
            }
            catch (Exception ex) { Log.Error("Sending clipboard", ex); }
            finally { image?.Dispose(); }
        });
    }

    /// <summary>Called on a network thread for each clipboard chunk from the other PC.</summary>
    private void ReceiveClipboard(byte[] chunk)
    {
        if (!_shareClipboard) return;
        var bundle = _clip.Accept(chunk);
        if (bundle == null) return;
        try
        {
            _clip.NoteReceived(bundle);
            var (text, rtf, image) = ClipboardSync.Unpack(bundle);
            OnUi(() =>
            {
                _ignoreClipboardUntil = Environment.TickCount64 + 1000; // don't echo it back
                try { ClipboardSync.Set(text, rtf, image); }
                catch (Exception ex) { Log.Error("Setting clipboard", ex); }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Receiving clipboard", ex);
        }
    }

    // ---- UI -------------------------------------------------------------

    private void UpdateUi()
    {
        bool host = _rbHost.Checked;
        _hostBox.Visible = host;
        _joinBox.Visible = !host;
        _btnHost.Text = _host == null ? "Start sharing" : "Stop sharing";
        _btnJoin.Text = _client == null ? "Connect" : "Disconnect";
        _tbCode.Enabled = _tbAddress.Enabled = _client == null;
    }

    private void SetStatus(string s)
    {
        _baseStatus = s;
        _lblStatus.Text = s;
        var tip = "KeyShare: " + s;
        _tray.Text = tip.Length > 63 ? tip[..62] + "…" : tip;
    }

    private void SetRemote(bool remote)
    {
        _lblStatus.Text = remote ? "Controlling the other PC. Ctrl+Alt+F12 comes back." : _baseStatus;
        _tray.Text = remote ? "KeyShare: controlling the other PC" : "KeyShare";
    }

    private string SideWord() => _settings.OtherSide switch
    {
        Side.Left => "left", Side.Right => "right", Side.Top => "top", _ => "bottom",
    };

    private void OnUi(Action a)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            // Always deferred, even on the UI thread, so hook callbacks return right away.
            BeginInvoke(a);
        }
        catch (InvalidOperationException)
        {
            // Window is closing.
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool wasRunning = _host != null || _client != null;
        _capture?.Dispose();
        _host?.Dispose();
        _client?.Dispose();
        _capture = null; _host = null; _client = null;
        _settings.Running = wasRunning; // resume next time
        _settings.Save();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>The app icon at the closest size to <paramref name="size"/>.</summary>
    private static Icon LoadIcon(Size size)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("KeyShare.ico");
        return stream == null ? SystemIcons.Application : new Icon(stream, size);
    }
}
