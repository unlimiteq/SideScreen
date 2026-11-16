using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SideScreen;

public sealed class MainForm : Form
{
    public const string ShowSignal = "SideScreen.Signal.Show", ExitSignal = "SideScreen.Signal.Exit";

    static readonly Color Bg = Color.FromArgb(0x14, 0x16, 0x1B);
    static readonly Color Card = Color.FromArgb(0x1E, 0x21, 0x29);
    static readonly Color Line = Color.FromArgb(0x2E, 0x32, 0x3D);
    static readonly Color TextMain = Color.FromArgb(0xF2, 0xF4, 0xF8);
    static readonly Color TextDim = Color.FromArgb(0x8D, 0x94, 0xA3);
    static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    static readonly Color Good = Color.FromArgb(0x3D, 0xD6, 0x8C);
    static readonly Color Warn = Color.FromArgb(0xFF, 0xB0, 0x4D);
    static readonly Color Stop = Color.FromArgb(0xE5, 0x55, 0x5E);

    static readonly int[] Rates = [120, 90, 60];
    static readonly int[] Mbps = [150, 80, 40, 20];
    static readonly (int w, int h)[] Resolutions = [(0, 0), (2560, 1600), (1920, 1200)];

    readonly StatusCard _status = new();
    readonly DarkCombo _mode = new(), _res = new(), _pos = new(), _rate = new(), _quality = new();
    readonly DarkCheck _touch = new() { Text = "Touch input" };
    readonly DarkCheck _auto = new() { Text = "Connect automatically" };
    readonly DarkCheck _login = new() { Text = "Start with Windows" };
    readonly DarkCheck _closeTray = new() { Text = "Close to tray" };
    readonly DarkCheck _notify = new() { Text = "Notifications" };
    readonly DarkCheck _hotkey = new() { Text = "Hotkey  Ctrl+Alt+S" };
    readonly LinkLabel _footer = new();
    readonly PillButton _start = new();
    readonly LinkLabel _driver = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 2500 };

    readonly NotifyIcon _tray = new();
    readonly ToolStripMenuItem _trayToggle = new("Start");
    readonly Icon _iconIdle, _iconLive, _iconWarn;
    string? _lastSerial = Unknown; // tablet seen on the previous poll (for connect / disconnect alerts)
    const string Unknown = "\0";
    bool _wasLive;             // the session was streaming (for "tablet disconnected" alerts)
    const int HotkeyId = 0x5353;
    readonly Settings _settings = Settings.Load();

    StreamSession? _session;
    List<DisplayInfo> _displays = [];
    bool _driverInstalled, _stopRequested;
    bool _loading = true;      // suppresses saving while the controls are being filled
    bool _exiting;             // a real exit (tray menu / --exit), not "close to tray"
    bool _startHidden;         // launched with --tray
    bool _autoBlocked;         // auto-connect already used for the tablet that is plugged in now
    float _scale = 1f;
    int S(int v) => (int)Math.Round(v * _scale);

    /// <summary>Set by "SideScreen.exe --start": begin streaming as soon as the tablet is seen.</summary>
    public bool AutoStart;
    /// <summary>With --start: "--mirror" mirrors the first real display instead of extending.</summary>
    public bool AutoMirror;

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        Text = "SideScreen";
        AutoScaleMode = AutoScaleMode.None; // layout is scaled by hand (S) so custom-drawn controls stay crisp
        _scale = DeviceDpi / 96f;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(S(400), S(578));
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var title = new Label { Text = "SideScreen", Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, Location = new Point(S(22), S(16)), ForeColor = TextMain, BackColor = Bg };
        var sub = new Label { Text = "Your tablet as a second monitor, over USB", AutoSize = true, Location = new Point(S(24), S(54)), ForeColor = TextDim, BackColor = Bg };

        const int left = 22, full = 356, half = 172, right = left + half + 12;
        _status.SetBounds(S(left), S(88), S(full), S(72));

        AddField("Display", _mode, left, 178, full);
        AddField("Resolution", _res, left, 238, half);
        AddField("Tablet position", _pos, right, 238, half);
        AddField("Refresh rate", _rate, left, 298, half);
        AddField("Quality", _quality, right, 298, half);

        _res.Items.AddRange(["Native", "2560 × 1600", "1920 × 1200", "Custom…"]);
        _pos.Items.AddRange(["Right of PC", "Left of PC", "Above PC", "Below PC"]);
        _rate.Items.AddRange(["120 Hz", "90 Hz", "60 Hz", "Custom…"]);
        _quality.Items.AddRange(["Max · 150 Mbps", "High · 80 Mbps", "Balanced · 40 Mbps", "Light · 20 Mbps"]);

        _touch.SetBounds(S(left), S(364), S(half), S(24));
        _auto.SetBounds(S(right), S(364), S(half), S(24));
        _login.SetBounds(S(left), S(392), S(half), S(24));
        _closeTray.SetBounds(S(right), S(392), S(half), S(24));
        _notify.SetBounds(S(left), S(420), S(half), S(24));
        _hotkey.SetBounds(S(right), S(420), S(half), S(24));

        _start.SetBounds(S(left), S(460), S(full), S(44));
        _start.Text = "Start";
        _start.Fill = Accent;
        _start.Click += (_, _) => Toggle();

        _driver.SetBounds(S(left), S(512), S(full), S(22));
        _driver.TextAlign = ContentAlignment.MiddleCenter;
        _driver.LinkColor = Accent; _driver.ActiveLinkColor = TextMain; _driver.BackColor = Bg; _driver.ForeColor = TextDim;
        _driver.LinkBehavior = LinkBehavior.HoverUnderline;
        _driver.LinkClicked += (_, _) => InstallDriver();

        // footer: version · log · updates
        var version = AppInfo.Version;
        const string logText = "Open log", updText = "Check for updates";
        _footer.Text = $"v{version}   ·   {logText}   ·   {updText}";
        _footer.SetBounds(S(left), S(540), S(full), S(22));
        _footer.TextAlign = ContentAlignment.MiddleCenter;
        _footer.LinkColor = TextDim; _footer.ActiveLinkColor = TextMain; _footer.BackColor = Bg; _footer.ForeColor = TextDim;
        _footer.LinkBehavior = LinkBehavior.HoverUnderline;
        _footer.Font = new Font("Segoe UI", 8.5f);
        _footer.Links.Clear();
        _footer.Links.Add(_footer.Text.IndexOf(logText), logText.Length, "log");
        _footer.Links.Add(_footer.Text.IndexOf(updText), updText.Length, "update");
        _footer.LinkClicked += (_, e) =>
        {
            if ((string?)e.Link?.LinkData == "log") OpenLog();
            else CheckForUpdates();
        };

        Controls.AddRange([title, sub, _status, _touch, _auto, _login, _closeTray, _notify, _hotkey, _start, _driver, _footer]);

        // remembered choices
        _res.SelectedIndex = Math.Clamp(_settings.Resolution, 0, _res.Items.Count - 1);
        _pos.SelectedIndex = Math.Clamp(_settings.Position, 0, _pos.Items.Count - 1);
        _rate.SelectedIndex = Math.Clamp(_settings.Rate, 0, _rate.Items.Count - 1);
        _quality.SelectedIndex = Math.Clamp(_settings.Quality, 0, _quality.Items.Count - 1);
        _touch.Checked = _settings.Touch;
        _auto.Checked = _settings.AutoConnect;
        _closeTray.Checked = _settings.CloseToTray;
        _notify.Checked = _settings.Notifications;
        _hotkey.Checked = _settings.Hotkey;
        _login.Checked = Settings.RunAtLogin;
        UpdateCustomLabels();
        _resPrev = _res.SelectedIndex;
        _ratePrev = _rate.SelectedIndex;
        _loading = false;

        // "Custom…" opens a small dialog; cancelling it goes back to the previous choice
        _res.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_res.SelectedIndex == CustomIndex)
            {
                var r = CustomModeDialog.AskResolution(this, _settings.CustomWidth, _settings.CustomHeight);
                if (r == null) { SetQuietly(_res, _resPrev); return; }
                (_settings.CustomWidth, _settings.CustomHeight) = r.Value;
                UpdateCustomLabels();
            }
            _resPrev = _res.SelectedIndex;
        };
        _rate.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_rate.SelectedIndex == CustomIndex)
            {
                var hz = CustomModeDialog.AskRefreshRate(this, _settings.CustomHz);
                if (hz == null) { SetQuietly(_rate, _ratePrev); return; }
                _settings.CustomHz = hz.Value;
                UpdateCustomLabels();
            }
            _ratePrev = _rate.SelectedIndex;
        };

        foreach (var c in new[] { _mode, _res, _pos, _rate, _quality }) c.SelectedIndexChanged += (_, _) => { SaveSettings(); UpdateEnabled(); };
        foreach (var c in new[] { _touch, _auto, _closeTray, _notify }) c.CheckedChanged += (_, _) => SaveSettings();
        _hotkey.CheckedChanged += (_, _) => { SaveSettings(); ApplyHotkey(); };
        _login.CheckedChanged += (_, _) => Settings.RunAtLogin = _login.Checked;

        // tray
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SideScreen", null, (_, _) => ShowFromTray());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayToggle.Click += (_, _) => Toggle();
        var baseIcon = Icon ?? SystemIcons.Application;
        _iconIdle = baseIcon;
        _iconLive = TrayIcon(baseIcon, Good);
        _iconWarn = TrayIcon(baseIcon, Warn);
        _tray.Icon = _iconIdle;
        _tray.Text = "SideScreen";
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.Visible = true;

        _poll.Tick += (_, _) => { if (_session == null) RefreshIdleAsync(); };

        // The window may never be shown (--tray), so start up from here rather than OnShown.
        _ = Handle;
        ApplyHotkey();
        ListenForSignals();
        _status.Set("Checking…", "", TextDim);
        Task.Run(() =>
        {
            // A leftover virtual monitor with nothing showing it would just swallow windows.
            var vd = Displays.FindVirtual();
            if (vd is { Attached: true }) Displays.Detach(vd.DeviceName);
        }).ContinueWith(_ => RefreshIdleAsync());
        _poll.Start();
    }

    void AddField(string caption, DarkCombo combo, int x, int y, int width)
    {
        Controls.Add(new Label { Text = caption.ToUpperInvariant(), Font = new Font("Segoe UI Semibold", 7.5f), ForeColor = TextDim, BackColor = Bg, AutoSize = true, Location = new Point(S(x + 1), S(y)) });
        combo.SetBounds(S(x), S(y + 19), S(width), S(30));
        combo.ItemHeight = S(24);
        Controls.Add(combo);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4);
        int color = Bg.R | (Bg.G << 8) | (Bg.B << 16);
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref color, 4);
    }

    // ---------- custom resolution / refresh rate ----------

    const int CustomIndex = 3;
    int _resPrev, _ratePrev;

    void UpdateCustomLabels()
    {
        bool was = _loading;
        _loading = true;
        _res.Items[CustomIndex] = _res.SelectedIndex == CustomIndex || _settings.Resolution == CustomIndex
            ? $"Custom · {_settings.CustomWidth} × {_settings.CustomHeight}" : "Custom…";
        _rate.Items[CustomIndex] = _rate.SelectedIndex == CustomIndex || _settings.Rate == CustomIndex
            ? $"Custom · {_settings.CustomHz} Hz" : "Custom…";
        _loading = was;
        _res.Invalidate();
        _rate.Invalidate();
    }

    void SetQuietly(ComboBox box, int index)
    {
        bool was = _loading;
        _loading = true;
        box.SelectedIndex = Math.Max(0, index);
        _loading = was;
    }

    // ---------- settings ----------

    void SaveSettings()
    {
        if (_loading) return;
        if (_mode.SelectedIndex >= 0 && _mode.SelectedIndex < _displays.Count)
        {
            var d = _displays[_mode.SelectedIndex];
            _settings.Mode = d.IsVirtual ? "extend" : d.DeviceName;
        }
        _settings.Resolution = _res.SelectedIndex;
        _settings.Position = _pos.SelectedIndex;
        _settings.Rate = _rate.SelectedIndex;
        _settings.Quality = _quality.SelectedIndex;
        _settings.Touch = _touch.Checked;
        _settings.AutoConnect = _auto.Checked;
        _settings.CloseToTray = _closeTray.Checked;
        _settings.Notifications = _notify.Checked;
        _settings.Hotkey = _hotkey.Checked;
        _settings.Save();
    }

    void UpdateEnabled()
    {
        bool idle = _session == null;
        bool extend = _mode.SelectedIndex >= 0 && _mode.SelectedIndex < _displays.Count && _displays[_mode.SelectedIndex].IsVirtual;
        _mode.Enabled = _rate.Enabled = _quality.Enabled = _touch.Enabled = idle;
        _res.Enabled = _pos.Enabled = idle && extend; // only a virtual display can be sized and placed
    }

    // ---------- tray ----------

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden) { _startHidden = false; value = false; } // --tray: stay in the tray at launch
        base.SetVisibleCore(value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) HideToTray();
    }

    void HideToTray()
    {
        Hide();
        if (_settings.TrayHintShown) return;
        _settings.TrayHintShown = true;
        _settings.Save();
        _tray.ShowBalloonTip(3000, "SideScreen is still running", "Click the tray icon to open it again. Right-click for Start, Stop and Exit.", ToolTipIcon.Info);
    }

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void SetTrayText(string text) => _tray.Text = text.Length > 100 ? text[..100] : text;

    /// <summary>Lets a second launch of the exe (or "--exit") talk to this running instance.</summary>
    void ListenForSignals()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal);
        new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny([show, exit]);
                if (IsDisposed) return;
                SafeUi(which == 0 ? ShowFromTray : ExitApp);
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "signals" }.Start();
    }

    // ---------- idle state ----------

    void RefreshIdleAsync()
    {
        Task.Run(() =>
        {
            var displays = Displays.List();
            bool driver = displays.Any(d => d.IsVirtual) || DriverInstaller.IsInstalled();
            var (serial, info) = Adb.FindDevice();
            SafeUi(() =>
            {
                if (_session != null || IsDisposed) return;
                _driverInstalled = driver;
                UpdateDisplays(displays);
                if (serial != null) _status.Set($"{info} connected", _hotkey.Checked ? $"Ready – press Start or {HotkeyText}" : "Ready – press Start", Good);
                else _status.Set("No tablet", info, Warn);
                SetTrayText(serial != null ? $"SideScreen – {info} connected" : "SideScreen – no tablet");
                _driver.Text = driver ? "Virtual display driver installed" : "Install virtual display driver (needed for Extend)";
                _driver.LinkArea = driver ? new LinkArea(0, 0) : new LinkArea(0, _driver.Text.Length);

                if (_lastSerial == Unknown) _lastSerial = serial; // first look after launch: no alert
                else if (serial != _lastSerial)
                {
                    if (serial != null) Notify("Tablet connected", $"{info} is ready. Press Start or {HotkeyText}.");
                    else if (_lastSerial != null) Notify("Tablet disconnected", "Plug the USB cable back in to continue.");
                    _lastSerial = serial;
                }
                if (serial == null) { _autoBlocked = false; return; } // unplugged: auto-connect is armed again
                if (AutoStart)
                {
                    AutoStart = false;
                    int mirror = _displays.FindIndex(d => !d.IsVirtual);
                    if (AutoMirror && mirror >= 0) _mode.SelectedIndex = mirror;
                    Toggle();
                }
                else if (_auto.Checked && !_autoBlocked) Toggle();
            });
        });
    }

    void UpdateDisplays(List<DisplayInfo> displays)
    {
        var items = displays.Where(d => d.IsVirtual).Take(1).Concat(displays.Where(d => !d.IsVirtual && d.Attached)).ToList();
        if (items.Select(d => d.Label).SequenceEqual(_displays.Select(d => d.Label))) return;
        // keep the current choice, or on first fill the remembered one
        string? wanted = _mode.SelectedIndex >= 0 && _mode.SelectedIndex < _displays.Count
            ? (_displays[_mode.SelectedIndex].IsVirtual ? "extend" : _displays[_mode.SelectedIndex].DeviceName)
            : _settings.Mode;
        _loading = true;
        _displays = items;
        _mode.Items.Clear();
        // "Extend" and "Mirror"; only name the screen when there is more than one to mirror
        bool severalScreens = items.Count(d => !d.IsVirtual) > 1;
        foreach (var d in items) _mode.Items.Add(d.IsVirtual || !severalScreens ? d.Label : d.MirrorDetail);
        int idx = items.FindIndex(d => (d.IsVirtual ? "extend" : d.DeviceName) == wanted);
        if (items.Count > 0) _mode.SelectedIndex = idx >= 0 ? idx : 0;
        _loading = false;
        UpdateEnabled();
    }

    // ---------- start / stop ----------

    void Toggle()
    {
        if (_session != null)
        {
            _stopRequested = true;
            _start.Enabled = _trayToggle.Enabled = false;
            _status.Set("Stopping…", "", TextDim);
            _session.Stop();
            return;
        }
        if (_mode.SelectedIndex < 0 || _mode.SelectedIndex >= _displays.Count) return;
        var target = _displays[_mode.SelectedIndex];
        var (resW, resH) = _res.SelectedIndex == CustomIndex
            ? (_settings.CustomWidth, _settings.CustomHeight)
            : Resolutions[Math.Clamp(_res.SelectedIndex, 0, Resolutions.Length - 1)];
        var opt = new StreamOptions
        {
            MirrorDevice = target.IsVirtual ? null : target.DeviceName,
            Fps = _rate.SelectedIndex == CustomIndex ? _settings.CustomHz : Rates[Math.Clamp(_rate.SelectedIndex, 0, Rates.Length - 1)],
            BitrateMbps = Mbps[Math.Max(0, _quality.SelectedIndex)],
            Width = resW, Height = resH,
            Position = Math.Max(0, _pos.SelectedIndex),
            Touch = _touch.Checked,
        };
        var s = new StreamSession(opt);
        string headline = "Starting…";
        s.Status += msg => SafeUi(() =>
        {
            headline = msg;
            bool live = msg.StartsWith("Streaming");
            _status.Set(msg, live ? _status.Detail : "", live ? Good : msg.Contains("fail", StringComparison.OrdinalIgnoreCase) || msg.StartsWith("Error") ? Stop : Warn);
            SetTrayText("SideScreen – " + msg);
            _tray.Icon = live ? _iconLive : _iconWarn;
            if (live && !_wasLive && !Visible) Notify("Streaming to your tablet", msg.Replace("Streaming ", ""));
            if (!live && _wasLive && msg.StartsWith("Waiting"))
                Notify("Tablet disconnected", "Streaming resumes automatically when the tablet is back.");
            if (live) _wasLive = true;
            else if (msg.StartsWith("Waiting")) _wasLive = false;
        });
        s.Stats += (fps, mbit, encMs) => SafeUi(() =>
        {
            if (_session == s && headline.StartsWith("Streaming"))
                _status.Set(headline, $"{fps:0} fps   ·   {mbit:0.0} Mbps   ·   encode {encMs:0.0} ms", Good);
        });
        s.Stopped += () => SafeUi(() =>
        {
            bool failed = !_stopRequested;
            _session = null;
            SetRunning(false);
            _tray.Icon = _iconIdle;
            _wasLive = false;
            if (failed)
            {
                _status.Set(headline, "Stopped", Stop);
                SetTrayText("SideScreen – stopped");
                Notify("Streaming stopped", headline);
            }
            else RefreshIdleAsync();
        });
        _stopRequested = false;
        _autoBlocked = true; // after a manual Stop, do not reconnect until the tablet is plugged in again
        _session = s;
        SetRunning(true);
        _status.Set("Starting…", "", TextDim);
        s.Start();
    }

    void SetRunning(bool running)
    {
        _start.Enabled = _trayToggle.Enabled = true;
        _start.Text = _trayToggle.Text = running ? "Stop" : "Start";
        _start.Fill = running ? Stop : Accent;
        UpdateEnabled();
        _start.Invalidate();
    }

    void SafeUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _closeTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // the X button only hides the window; Exit is in the tray menu
            HideToTray();
            return;
        }
        if (_session != null)
        {
            var s = _session;
            using var done = new ManualResetEventSlim();
            s.Stopped += done.Set;
            s.Stop();
            done.Wait(4000); // lets the session remove the virtual monitor again
        }
        SaveSettings();
        UnregisterHotKey(Handle, HotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    // ---------- hotkey, notifications, tray icon, about ----------

    const string HotkeyText = "Ctrl+Alt+S";

    void ApplyHotkey()
    {
        UnregisterHotKey(Handle, HotkeyId);
        if (!_hotkey.Checked) return;
        // MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, 'S'
        bool ok = RegisterHotKey(Handle, HotkeyId, 0x1 | 0x2 | 0x4000, (uint)Keys.S);
        Log.Write(ok ? $"hotkey {HotkeyText} registered" : $"hotkey {HotkeyText} unavailable");
        if (!ok) Notify("Hotkey unavailable", $"{HotkeyText} is already used by another app.");
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            Log.Write($"hotkey pressed (session {(_session == null ? "idle" : "running")}, displays {_displays.Count})");
            Toggle();
            return;
        }
        base.WndProc(ref m);
    }

    void Notify(string title, string text)
    {
        if (!_notify.Checked) return;
        Log.Write($"notify: {title} – {text}");
        _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.None);
    }

    /// <summary>The app icon with a small status dot in the corner.</summary>
    static Icon TrayIcon(Icon baseIcon, Color dot)
    {
        int size = SystemInformation.SmallIconSize.Width * 2;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var src = new Icon(baseIcon, size, size)) g.DrawIcon(src, new Rectangle(0, 0, size, size));
            float d = size * 0.46f, x = size - d - 1, y = size - d - 1;
            using (var ring = new SolidBrush(Bg)) g.FillEllipse(ring, x - size * 0.06f, y - size * 0.06f, d + size * 0.12f, d + size * 0.12f);
            using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, x, y, d, d);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.File)) Log.Write("log opened");
            Process.Start(new ProcessStartInfo(Log.File) { UseShellExecute = true });
        }
        catch { }
    }

    void CheckForUpdates()
    {
        Task.Run(async () =>
        {
            var result = await Updater.CheckAsync();
            SafeUi(() =>
            {
                if (result.Error != null)
                {
                    MessageBox.Show(this, "Could not check for updates:\n" + result.Error, "SideScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!result.IsNewer)
                {
                    MessageBox.Show(this, $"You have the latest version (v{AppInfo.Version}).", "SideScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(this, $"SideScreen {result.Tag} is available (you have v{AppInfo.Version}).\n\nOpen the download page?",
                        "SideScreen update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(result.Url!) { UseShellExecute = true });
            });
        });
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    // ---------- driver ----------

    void InstallDriver()
    {
        if (_driverInstalled) return;
        if (DriverInstaller.DriverFolder == null)
        {
            MessageBox.Show(this, "The driver folder was not found next to SideScreen.exe.", "SideScreen");
            return;
        }
        _driver.Text = "Installing driver…";
        _driver.LinkArea = new LinkArea(0, 0);
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, "--install-driver") { UseShellExecute = true, Verb = "runas" };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                Thread.Sleep(2500); // give Windows a moment to bring the new monitor up
                var vd = Displays.FindVirtual();
                if (vd is { Attached: true }) Displays.Detach(vd.DeviceName);
            }
            catch { /* UAC declined */ }
            SafeUi(RefreshIdleAsync);
        });
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ================= small custom controls =================

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    sealed class StatusCard : Control
    {
        string _head = "", _detail = "";
        Color _dot = TextDim;
        public string Detail => _detail;

        public StatusCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Set(string head, string detail, Color dot)
        {
            if (head == _head && detail == _detail && dot == _dot) return;
            _head = head; _detail = detail; _dot = dot;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            float s = DeviceDpi / 96f;
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10 * s))
            using (var fill = new SolidBrush(Card))
            using (var pen = new Pen(Line))
            { g.FillPath(fill, path); g.DrawPath(pen, path); }

            using (var glow = new SolidBrush(Color.FromArgb(50, _dot)))
                g.FillEllipse(glow, 15 * s, Height / 2f - 9 * s, 18 * s, 18 * s);
            using (var dot = new SolidBrush(_dot))
                g.FillEllipse(dot, 19 * s, Height / 2f - 5 * s, 10 * s, 10 * s);

            var textRect = new Rectangle((int)(46 * s), 0, Width - (int)(56 * s), Height);
            using var headFont = new Font("Segoe UI Semibold", 10f);
            using var detFont = new Font("Segoe UI", 9f);
            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (_detail.Length == 0)
                TextRenderer.DrawText(g, _head, headFont, textRect, TextMain, Card, Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            else
            {
                int half = Height / 2;
                TextRenderer.DrawText(g, _head, headFont, new Rectangle(textRect.X, half - (int)(22 * s), textRect.Width, (int)(22 * s)), TextMain, Card, Flags | TextFormatFlags.Bottom);
                TextRenderer.DrawText(g, _detail, detFont, new Rectangle(textRect.X, half + (int)(1 * s), textRect.Width, (int)(22 * s)), TextDim, Card, Flags | TextFormatFlags.Top);
            }
        }
    }

    sealed class PillButton : Control
    {
        public Color Fill = Accent;
        bool _hover, _down;

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.StandardDoubleClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 11f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            var c = !Enabled ? Line : _down ? ControlPaint.Dark(Fill, 0.08f) : _hover ? ControlPaint.Light(Fill, 0.25f) : Fill;
            using var path = Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 10 * DeviceDpi / 96f);
            using var b = new SolidBrush(c);
            g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Color.White : TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Card;
            ForeColor = TextMain;
            ItemHeight = 24;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            float s = DeviceDpi / 96f;
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 7 * s))
            using (var fill = new SolidBrush(Card))
            using (var pen = new Pen(Focused ? Accent : Line))
            { g.FillPath(fill, path); g.DrawPath(pen, path); }
            var text = SelectedIndex >= 0 ? Items[SelectedIndex]?.ToString() ?? "" : "";
            TextRenderer.DrawText(g, text, Font, new Rectangle((int)(10 * s), 0, Width - (int)(36 * s), Height),
                Enabled ? TextMain : TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // chevron
            float cx = Width - 18 * s, cy = Height / 2f;
            using var chev = new Pen(TextDim, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(chev, [new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 2 * s), new PointF(cx + 4 * s, cy - 2 * s)]);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var b = new SolidBrush(sel ? Line : Card);
            e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.X += 8; r.Width -= 8;
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString(), Font, r, TextMain,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    sealed class DarkCheck : Control
    {
        bool _checked, _hover;
        public event EventHandler? CheckedChanged;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }

        public DarkCheck()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            float s = DeviceDpi / 96f;
            float box = 16 * s, top = (Height - box) / 2f;
            using (var path = Rounded(new RectangleF(0.5f, top, box, box), 4 * s))
            {
                using var fill = new SolidBrush(_checked ? (Enabled ? Accent : Line) : Card);
                using var pen = new Pen(_checked ? (Enabled ? Accent : Line) : _hover ? TextDim : Line);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (_checked)
            {
                using var tick = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, [new PointF(4 * s, top + 8.5f * s), new PointF(7 * s, top + 11.5f * s), new PointF(12.5f * s, top + 5 * s)]);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(box + 8 * s), 0, Width - (int)(box + 8 * s), Height),
                Enabled ? TextMain : TextDim, Bg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
