using System.Diagnostics;

namespace SyncthingTray;

/// <summary>
/// Dark-themed Settings GUI matching the AHK version layout.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly SyncthingApi _api;
    private readonly Action _onSaved;
    private readonly OsdToolTip _osd;
    private bool _disposed;

    // Controls we need to read on Save (assigned in Build* methods)
    private ComboBox _cboDblClick = null!;
    private ComboBox _cboMiddleClick = null!;
    private CheckBox _cbRunOnStartup = null!;
    private CheckBox _cbStartBrowser = null!;
    private CheckBox _cbNetPause = null!;
    private CheckBox _cbAutoUpdates = null!;
    private TextBox _edApiKey = null!;
    private TextBox _edSyncExe = null!;
    private TextBox _edWebUI = null!;
    private TextBox _edDelay = null!;
    private CheckBox _cbGlobal = null!;
    private CheckBox _cbLocal = null!;
    private CheckBox _cbRelay = null!;
    private CheckBox _cbSoundNotify = null!;
    private CheckBox _cbStopOnExit = null!;

    private readonly Font _boldFont;
    private readonly Font _normalFont;
    private readonly Font _sectionFont;
    private readonly Font _monoFont;
    private readonly Font _btnFont;
    private readonly Font _subFont;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DimColor = Color.FromArgb(0xA0, 0xA0, 0xC0);
    private static readonly Color DividerColor = Color.FromArgb(0x40, 0x40, 0x50);
    private static readonly Color EditBgColor = Color.FromArgb(0x2A, 0x2A, 0x3E);
    private static readonly Color ComboSelectedBgColor = Color.FromArgb(0x35, 0x35, 0x50);

    // CLAUDE.md: cache GDI in paint paths — combo item draw fires per item per paint.
    private static readonly SolidBrush ComboBgBrush = new(EditBgColor);
    private static readonly SolidBrush ComboSelectedBrush = new(ComboSelectedBgColor);

    public SettingsForm(AppConfig config, SyncthingApi api, OsdToolTip osd, Action onSaved)
    {
        _config = config;
        _api = api;
        _osd = osd;
        _onSaved = onSaved;

        _boldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        _normalFont = new Font("Segoe UI", 9f);
        _sectionFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        _monoFont = new Font("Consolas", 8f);
        _btnFont = new Font("Segoe UI", 8f);
        _subFont = new Font("Segoe UI", 8f);

        Text = $"SyncthingTray v{AppConfig.Version} \u2014 Settings";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = BgColor;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        int sw = 410;
        int y = 10;

        BuildClickActionsSection(ref y, sw);
        BuildGeneralSection(ref y, sw);
        BuildPathsSection(ref y, sw);
        BuildApiSection(ref y, sw);
        BuildDiscoverySection(ref y, sw);
        BuildUpdatesSection(ref y, sw);
        BuildButtonRow(ref y, sw);

        y += 40;
        ClientSize = new Size(sw, y);
    }

    private void BuildClickActionsSection(ref int y, int sw)
    {
        AddSectionHeader("Tray Click Actions", 16, ref y, sw);

        AddLabel("Double-click:", 16, y + 2, 0, _normalFont, DimColor);
        _cboDblClick = AddComboBox(112, y, 250, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.DblClickAction));
        y += 30;

        AddLabel("Middle-click:", 16, y + 2, 0, _normalFont, DimColor);
        _cboMiddleClick = AddComboBox(112, y, 250, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.MiddleClickAction));
        y += 30;
    }

    private void BuildGeneralSection(ref int y, int sw)
    {
        AddSectionHeader("General", 16, ref y, sw);

        _cbRunOnStartup = AddCheckBox("Run on startup", 16, y, _config.RunOnStartup);
        if (_config.IsPortable)
        {
            _cbRunOnStartup.Enabled = false;
            AddLabel("(not available in portable mode)", 36, y + 18, 300, _subFont, Color.FromArgb(0x80, 0x80, 0x90));
            y += 16;
        }
        y += 26;

        _cbStartBrowser = AddCheckBox("Start browser when Syncthing launches", 16, y, _config.StartBrowser);
        y += 26;

        _cbNetPause = AddCheckBox("Auto-pause on public networks", 16, y, _config.NetworkAutoPause);
        y += 26;

        _cbSoundNotify = AddCheckBox("Play sounds on events", 16, y, _config.SoundNotifications);
        y += 26;

        _cbStopOnExit = AddCheckBox("Stop Syncthing when tray exits", 16, y, _config.StopOnExit);
        y += 26;

        AddLabel("Startup Delay:", 16, y, 0, _normalFont, DimColor);
        _edDelay = AddTextBox(110, y - 2, 50, _config.StartupDelay.ToString());
        AddLabel("seconds", 166, y, 0, _normalFont, DimColor);
        y += 30;
    }

    private void BuildPathsSection(ref int y, int sw)
    {
        AddSectionHeader("Paths", 16, ref y, sw);

        AddLabel("Syncthing:", 16, y, 0, _normalFont, DimColor);
        _edSyncExe = AddTextBox(90, y - 2, 220, _config.SyncExe, true);
        var btnBrowse = new Button
        {
            Text = "...",
            Font = _btnFont,
            Location = new Point(314, y - 3),
            Size = new Size(50, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnBrowse.Click += OnBrowseSyncExe;
        Controls.Add(btnBrowse);
        y += 28;

        AddLabel("Web UI:", 16, y, 0, _normalFont, DimColor);
        _edWebUI = AddTextBox(90, y - 2, 220, _config.WebUI, true);
        var btnOpenWebUI = new Button
        {
            Text = "Open",
            Font = _btnFont,
            Location = new Point(314, y - 3),
            Size = new Size(50, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnOpenWebUI.Click += (_, _) =>
        {
            var url = _edWebUI.Text.Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                _osd.ShowMessage("URL is not valid — use http:// or https://", 4000);
                return;
            }
            try
            {
                using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _osd.ShowMessage("Could not open browser — check Windows default browser", 5000);
                TrayLog.Warn("SettingsForm OpenWebUI failed: " + ex.Message);
            }
        };
        Controls.Add(btnOpenWebUI);
        y += 30;
    }

    private void BuildApiSection(ref int y, int sw)
    {
        AddSectionHeader("API", 16, ref y, sw);

        AddLabel("API Key:", 16, y, 0, _normalFont, DimColor);
        _edApiKey = AddTextBox(90, y - 2, 272, _config.ApiKey, true);
        y += 30;
    }

    private void BuildDiscoverySection(ref int y, int sw)
    {
        AddSectionHeader("Discovery", 16, ref y, sw);

        bool curGlobal = false, curLocal = false, curRelay = false;
        bool discoveryRead = false;
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                var (status, body) = _api.Get("/rest/config/options");
                if (status == 200)
                {
                    curGlobal = ParseJsonBool(body, "globalAnnounceEnabled", false);
                    curLocal = ParseJsonBool(body, "localAnnounceEnabled", false);
                    curRelay = ParseJsonBool(body, "relaysEnabled", false);
                    discoveryRead = true;
                }
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Discovery read failed: " + ex.Message);
            }
        }

        _cbGlobal = AddCheckBox("Global Discovery", 16, y, curGlobal);
        y += 24;
        _cbLocal = AddCheckBox("Local Discovery", 16, y, curLocal);
        y += 24;
        _cbRelay = AddCheckBox("NAT Traversal (Relaying)", 16, y, curRelay);
        y += 24;

        // If the API read failed, the three boxes above do NOT reflect Syncthing's
        // real state. Disable + warn so Save doesn't silently destroy the user's
        // existing discovery config by PATCHing a default-false payload.
        _discoveryReadOk = discoveryRead;
        if (!discoveryRead)
        {
            _cbGlobal.Enabled = _cbLocal.Enabled = _cbRelay.Enabled = false;
            AddLabel(
                string.IsNullOrEmpty(_config.ApiKey)
                    ? "(set API Key above to manage discovery)"
                    : "(could not read current state — API unreachable)",
                36, y, 320, _subFont, Color.FromArgb(0x80, 0x80, 0x90));
            y += 18;
        }
        y += 6;
    }

    private bool _discoveryReadOk;

    private void BuildUpdatesSection(ref int y, int sw)
    {
        AddSectionHeader("Updates", 16, ref y, sw);

        _cbAutoUpdates = AddCheckBox("Check for Syncthing updates (daily)", 16, y, _config.AutoCheckUpdates);
        _cbAutoUpdates.Width = 220;
        var btnCheckNow = new Button
        {
            Text = "Check Now",
            Font = _btnFont,
            Location = new Point(240, y - 2),
            Size = new Size(90, 22),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnCheckNow.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _osd.ShowMessage("API Key required \u2014 set above", 3000);
                return;
            }
            _osd.ShowMessage("Checking for updates...", 2000);
            try
            {
                var (status, body) = _api.Get("/rest/system/upgrade");
                if (status == 200)
                {
                    bool newer = ParseJsonBool(body, "newer", false);
                    if (newer)
                    {
                        int idx = body.IndexOf("\"latest\"", StringComparison.Ordinal);
                        string ver = "unknown";
                        if (idx >= 0)
                        {
                            int q1 = body.IndexOf('"', idx + 10);
                            int q2 = body.IndexOf('"', q1 + 1);
                            if (q1 >= 0 && q2 > q1)
                                ver = body[(q1 + 1)..q2];
                        }
                        _osd.ShowMessage($"Update available: {ver}", 5000);
                    }
                    else
                    {
                        _osd.ShowMessage("Syncthing is up to date", 3000);
                    }
                }
                else
                {
                    _osd.ShowMessage($"Check failed (HTTP {status})", 3000);
                }
            }
            catch
            {
                _osd.ShowMessage("Could not reach Syncthing API", 3000);
            }
        };
        Controls.Add(btnCheckNow);
        y += 30;

        AddDivider(0, y, sw);
        y += 8;
    }

    private void BuildButtonRow(ref int y, int sw)
    {
        AddLinkButton("GitHub", 16, y, 68, "https://github.com/itsnateai/synctray");

        var btnUpdate = new Button
        {
            Text = "Update",
            Font = _btnFont,
            Location = new Point(88, y),
            Size = new Size(58, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };
        Controls.Add(btnUpdate);

        AddLinkButton("Syncthing", 150, y, 68, "https://github.com/syncthing/syncthing");

        var btnHelp = new Button
        {
            Text = "Help",
            Font = _btnFont,
            Location = new Point(222, y),
            Size = new Size(58, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnHelp.Click += (_, _) =>
        {
            using var hf = new HelpForm();
            hf.ShowDialog(this);
        };
        Controls.Add(btnHelp);

        var btnCheck = new Button
        {
            Text = "Check Config",
            Font = _btnFont,
            Location = new Point(284, y),
            Size = new Size(100, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnCheck.Click += OnCheckConfig;
        Controls.Add(btnCheck);
        y += 34;

        var btnSave = new Button
        {
            Text = "Save",
            Font = _normalFont,
            Location = new Point(16, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnSave.Click += OnSave;
        Controls.Add(btnSave);

        var btnApply = new Button
        {
            Text = "Apply",
            Font = _normalFont,
            Location = new Point(136, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnApply.Click += OnApply;
        Controls.Add(btnApply);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Font = _normalFont,
            Location = new Point(256, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            DialogResult = DialogResult.Cancel,
        };
        btnCancel.Click += (_, _) => Close();
        Controls.Add(btnCancel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private void OnBrowseSyncExe(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select syncthing.exe",
            Filter = "Executables (*.exe)|*.exe",
            FileName = _edSyncExe.Text,
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            _edSyncExe.Text = ofd.FileName;
    }

    private void OnCheckConfig(object? sender, EventArgs e)
    {
        var results = string.Empty;

        if (File.Exists(_config.SyncExe))
            results += "\u2713 Syncthing exe: Found\r\n";
        else
            results += $"\u2717 Syncthing exe: NOT FOUND at {_config.SyncExe}\r\n";

        if (IsSyncthingRunning())
            results += "\u2713 Process: Running\r\n";
        else
            results += "\u2717 Process: Not running\r\n";

        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            results += "\u2717 API Key: Not set\r\n";
        }
        else
        {
            try
            {
                var (status, _) = _api.Get("/rest/system/status");
                results += status == 200
                    ? "\u2713 API: Connected (HTTP 200)\r\n"
                    : $"\u2717 API: HTTP {status}\r\n";
            }
            catch
            {
                results += "\u2717 API: Unreachable\r\n";
            }

            try
            {
                var (status2, body2) = _api.Get("/rest/config/options");
                if (status2 == 200)
                {
                    var gd = ParseJsonBool(body2, "globalAnnounceEnabled", false) ? "on" : "off";
                    var ld = ParseJsonBool(body2, "localAnnounceEnabled", false) ? "on" : "off";
                    var rl = ParseJsonBool(body2, "relaysEnabled", false) ? "on" : "off";
                    results += $"  Discovery: Global={gd} Local={ld} NAT={rl}\r\n";
                }
            }
            catch { /* best-effort */ }
        }

        _osd.ShowMessage(results.Replace("\r\n", " | ").TrimEnd(' ', '|'), 5000);
    }

    private void ApplySettings()
    {
        _config.DblClickAction = AppConfig.ActionIndexToValue(_cboDblClick.SelectedIndex);
        _config.MiddleClickAction = AppConfig.ActionIndexToValue(_cboMiddleClick.SelectedIndex);
        _config.RunOnStartup = _config.IsPortable ? false : _cbRunOnStartup.Checked;
        _config.StartBrowser = _cbStartBrowser.Checked;
        _config.NetworkAutoPause = _cbNetPause.Checked;
        _config.AutoCheckUpdates = _cbAutoUpdates.Checked;
        _config.SoundNotifications = _cbSoundNotify.Checked;
        _config.StopOnExit = _cbStopOnExit.Checked;
        _config.ApiKey = _edApiKey.Text;
        _config.SyncExe = _edSyncExe.Text;
        _config.WebUI = AppConfig.ValidateWebUI(_edWebUI.Text);

        if (int.TryParse(_edDelay.Text, out int delay) && delay >= 0 && delay <= 3600)
            _config.StartupDelay = delay;
        else
            _osd.ShowMessage($"Startup delay must be a number 0-3600 — kept previous value ({_config.StartupDelay}s)", 5000);

        if (!_config.Save())
        {
            _osd.ShowMessage("Could not save settings \u2014 file may be locked", 5000);
            return;
        }

        // Save discovery settings via API — but only if we actually read the current
        // state when the dialog opened. Otherwise we'd be PATCHing a lie.
        if (_discoveryReadOk && !string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                var g = _cbGlobal.Checked ? "true" : "false";
                var l = _cbLocal.Checked ? "true" : "false";
                var r = _cbRelay.Checked ? "true" : "false";
                var (status, _) = _api.Patch("/rest/config/options",
                    $"{{\"globalAnnounceEnabled\":{g},\"localAnnounceEnabled\":{l},\"relaysEnabled\":{r}}}");
                if (status != 200)
                {
                    _osd.ShowMessage($"Discovery settings not saved to Syncthing (HTTP {status})", 5000);
                    TrayLog.Warn($"Discovery PATCH returned HTTP {status}.");
                }
            }
            catch (Exception ex)
            {
                _osd.ShowMessage("Discovery settings not saved to Syncthing", 5000);
                TrayLog.Warn("Discovery PATCH threw: " + ex.Message);
            }
        }

        // Apply startup shortcut
        if (!_config.IsPortable)
        {
            var iconPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty,
                "Resources", "sync.ico");
            bool ok;
            try
            {
                ok = StartupShortcut.Apply(_config.RunOnStartup, iconPath);
            }
            catch (Exception ex)
            {
                ok = false;
                TrayLog.Warn("StartupShortcut.Apply threw: " + ex.Message);
            }
            if (!ok)
            {
                _osd.ShowMessage(
                    _config.RunOnStartup
                        ? "Could not create startup shortcut — check Windows permissions"
                        : "Could not remove startup shortcut — it may be locked",
                    5000);
            }
        }

        // Warn if network auto-pause is enabled but WMI is unavailable
        if (_config.NetworkAutoPause)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\StandardCimv2",
                    "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
                using var results = searcher.Get();
                bool found = false;
                foreach (var obj in results)
                {
                    using (obj) { found = true; break; }
                }
                if (!found)
                    _osd.ShowMessage("Network auto-pause may not work on this system", 5000);
            }
            catch
            {
                _osd.ShowMessage("Network auto-pause may not work on this system", 5000);
            }
        }

        _onSaved();
    }

    private void OnSave(object? sender, EventArgs e)
    {
        ApplySettings();
        Close();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        ApplySettings();
        _osd.ShowMessage("Settings applied", 3000);
    }

    // --- UI Helpers ---

    private Label AddLabel(string text, int x, int y, int w, Font font, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = BgColor,
            Location = new Point(x, y),
            AutoSize = w <= 0,
        };
        if (w > 0) lbl.Width = w;
        Controls.Add(lbl);
        return lbl;
    }

    private Label AddDivider(int x, int y, int w)
    {
        var lbl = new Label
        {
            Location = new Point(x, y),
            Size = new Size(w, 1),
            BackColor = DividerColor,
        };
        Controls.Add(lbl);
        return lbl;
    }

    private CheckBox AddCheckBox(string text, int x, int y, bool isChecked)
    {
        var cb = new CheckBox
        {
            Text = text,
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            Location = new Point(x, y),
            Width = 320,
            Checked = isChecked,
        };
        Controls.Add(cb);
        return cb;
    }

    private TextBox AddTextBox(int x, int y, int w, string text, bool useMono = false)
    {
        var tb = new TextBox
        {
            Text = text,
            Font = useMono ? _monoFont : _normalFont,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            Location = new Point(x, y),
            Width = w,
            Height = 22,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(tb);
        return tb;
    }

    private ComboBox AddComboBox(int x, int y, int w, string[] items, int selectedIndex)
    {
        var cb = new ComboBox
        {
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            Location = new Point(x, y),
            Width = w,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 20,
        };
        cb.DrawItem += OnDrawComboItem;
        cb.Items.AddRange(items);
        cb.SelectedIndex = selectedIndex;
        Controls.Add(cb);
        return cb;
    }

    private static void OnDrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || sender is not ComboBox cb) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(selected ? ComboSelectedBrush : ComboBgBrush, e.Bounds);

        var text = cb.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, cb.Font, e.Bounds, FgColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void AddSectionHeader(string text, int x, ref int y, int sw)
    {
        y += 4;
        var lbl = AddLabel(text, x, y, 0, _sectionFont, DimColor); // AutoSize
        int labelWidth = TextRenderer.MeasureText(text, _sectionFont).Width;
        int labelEnd = x + labelWidth + 4;
        AddDivider(labelEnd, y + 7, sw - labelEnd - 10);
        y += 20;
    }

    private void AddLinkButton(string text, int x, int y, int w, string url)
    {
        var btn = new Button
        {
            Text = text,
            Font = _btnFont,
            Location = new Point(x, y),
            Size = new Size(w, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btn.Click += (_, _) =>
        {
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
        Controls.Add(btn);
    }

    private static bool IsSyncthingRunning()
    {
        var procs = Process.GetProcessesByName("syncthing");
        bool running = false;
        foreach (var p in procs)
        {
            using (p)
            {
                running = true;
            }
        }
        return running;
    }

    private static bool ParseJsonBool(string json, string key, bool defaultValue)
    {
        // Simple pattern: "key" : true/false
        int idx = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (idx < 0) return defaultValue;
        int colon = json.IndexOf(':', idx + key.Length + 2);
        if (colon < 0) return defaultValue;
        var after = json.AsSpan(colon + 1).TrimStart();
        if (after.StartsWith("true")) return true;
        if (after.StartsWith("false")) return false;
        return defaultValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _boldFont.Dispose();
                _normalFont.Dispose();
                _sectionFont.Dispose();
                _monoFont.Dispose();
                _btnFont.Dispose();
                _subFont.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
