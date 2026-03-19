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

    // Controls we need to read on Save
    private readonly ComboBox _cboDblClick;
    private readonly ComboBox _cboMiddleClick;
    private readonly CheckBox _cbRunOnStartup;
    private readonly CheckBox _cbStartBrowser;
    private readonly CheckBox _cbNetPause;
    private readonly CheckBox _cbAutoUpdates;
    private readonly TextBox _edApiKey;
    private readonly TextBox _edSyncExe;
    private readonly TextBox _edWebUI;
    private readonly TextBox _edDelay;
    private readonly CheckBox _cbGlobal;
    private readonly CheckBox _cbLocal;
    private readonly CheckBox _cbRelay;

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

        Text = "SyncthingTray Settings";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = BgColor;
        ShowInTaskbar = false;

        int sw = 360;
        int y = 14;

        // Title
        AddLabel($"SyncthingTray v{AppConfig.Version}", 16, y, 328, _boldFont, FgColor);
        y += 22;
        AddDivider(0, y, sw);
        y += 10;

        // Tray Click Actions
        AddSectionHeader("Tray Click Actions", 16, ref y, sw);

        AddLabel("Double-click:", 16, y, 90, _normalFont, DimColor);
        _cboDblClick = AddComboBox(110, y - 2, 220, AppConfig.ClickActions, AppConfig.ActionValueToIndex(config.DblClickAction));
        y += 28;

        AddLabel("Middle-click:", 16, y, 90, _normalFont, DimColor);
        _cboMiddleClick = AddComboBox(110, y - 2, 220, AppConfig.ClickActions, AppConfig.ActionValueToIndex(config.MiddleClickAction));
        y += 30;

        // General
        AddSectionHeader("General", 16, ref y, sw);

        _cbRunOnStartup = AddCheckBox("Run on startup", 16, y, config.RunOnStartup);
        if (config.IsPortable)
        {
            _cbRunOnStartup.Enabled = false;
            AddLabel("(not available in portable mode)", 36, y + 18, 300, _subFont, Color.FromArgb(0x80, 0x80, 0x90));
            y += 16;
        }
        y += 26;

        _cbStartBrowser = AddCheckBox("Start browser when Syncthing launches", 16, y, config.StartBrowser);
        y += 26;

        _cbNetPause = AddCheckBox("Auto-pause on public networks", 16, y, config.NetworkAutoPause);
        y += 26;

        AddLabel("Startup Delay:", 16, y, 90, _normalFont, DimColor);
        _edDelay = AddTextBox(110, y - 2, 50, config.StartupDelay.ToString());
        AddLabel("seconds", 166, y, 80, _normalFont, DimColor);
        y += 30;

        // Paths section
        AddSectionHeader("Paths", 16, ref y, sw);

        AddLabel("Syncthing:", 16, y, 90, _normalFont, DimColor);
        _edSyncExe = AddTextBox(80, y - 2, 210, config.SyncExe, true);
        var btnBrowse = new Button
        {
            Text = "...",
            Font = _btnFont,
            Location = new Point(294, y - 3),
            Size = new Size(50, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnBrowse.Click += OnBrowseSyncExe;
        Controls.Add(btnBrowse);
        y += 28;

        AddLabel("Web UI:", 16, y, 90, _normalFont, DimColor);
        _edWebUI = AddTextBox(80, y - 2, 210, config.WebUI, true);
        y += 30;

        // API section
        AddSectionHeader("API", 16, ref y, sw);

        AddLabel("API Key:", 16, y, 60, _normalFont, DimColor);
        _edApiKey = AddTextBox(80, y - 2, 260, config.ApiKey, true);
        y += 30;

        // Discovery section
        AddSectionHeader("Discovery", 16, ref y, sw);

        // Load current discovery settings
        bool curGlobal = true, curLocal = true, curRelay = true;
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            try
            {
                var (status, body) = _api.Get("/rest/config/options");
                if (status == 200)
                {
                    curGlobal = ParseJsonBool(body, "globalAnnounceEnabled", true);
                    curLocal = ParseJsonBool(body, "localAnnounceEnabled", true);
                    curRelay = ParseJsonBool(body, "relaysEnabled", true);
                }
            }
            catch { /* best-effort */ }
        }

        _cbGlobal = AddCheckBox("Global Discovery", 16, y, curGlobal);
        y += 24;
        _cbLocal = AddCheckBox("Local Discovery", 16, y, curLocal);
        y += 24;
        _cbRelay = AddCheckBox("NAT Traversal (Relaying)", 16, y, curRelay);
        y += 30;

        // Updates section
        AddSectionHeader("Updates", 16, ref y, sw);

        _cbAutoUpdates = AddCheckBox("Check for Syncthing updates (daily)", 16, y, config.AutoCheckUpdates);
        y += 30;

        // Divider
        AddDivider(0, y, sw);
        y += 8;

        // Link + utility buttons row
        AddLinkButton("GitHub", 16, y, 68, "https://github.com/itsnateai/synctray");
        AddLinkButton("Syncthing", 88, y, 68, "https://github.com/syncthing/syncthing");

        var btnHelp = new Button
        {
            Text = "Help",
            Font = _btnFont,
            Location = new Point(160, y),
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
            Location = new Point(222, y),
            Size = new Size(120, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnCheck.Click += OnCheckConfig;
        Controls.Add(btnCheck);
        y += 34;

        // Save / Apply / Cancel
        var btnSave = new Button
        {
            Text = "Save",
            Font = _normalFont,
            Location = new Point(16, y),
            Size = new Size(104, 30),
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
            Location = new Point(126, y),
            Size = new Size(104, 30),
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
            Location = new Point(236, y),
            Size = new Size(104, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            DialogResult = DialogResult.Cancel,
        };
        btnCancel.Click += (_, _) => Close();
        Controls.Add(btnCancel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        y += 40;
        ClientSize = new Size(sw, y);
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
        _config.ApiKey = _edApiKey.Text;
        _config.SyncExe = _edSyncExe.Text;
        _config.WebUI = _edWebUI.Text;

        if (int.TryParse(_edDelay.Text, out int delay))
            _config.StartupDelay = delay;

        _config.Save();

        // Save discovery settings via API
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                var g = _cbGlobal.Checked ? "true" : "false";
                var l = _cbLocal.Checked ? "true" : "false";
                var r = _cbRelay.Checked ? "true" : "false";
                _api.Patch("/rest/config/options",
                    $"{{\"globalAnnounceEnabled\":{g},\"localAnnounceEnabled\":{l},\"relaysEnabled\":{r}}}");
            }
            catch { /* best-effort */ }
        }

        // Apply startup shortcut
        if (!_config.IsPortable)
        {
            try
            {
                var iconPath = Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty,
                    "Resources", "sync.ico");
                StartupShortcut.Apply(_config.RunOnStartup, iconPath);
            }
            catch { /* shortcut failure is non-fatal */ }
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
        };
        cb.Items.AddRange(items);
        cb.SelectedIndex = selectedIndex;
        Controls.Add(cb);
        return cb;
    }

    private void AddSectionHeader(string text, int x, ref int y, int sw)
    {
        AddLabel(text, x, y, text.Length * 8 + 10, _sectionFont, DimColor);
        int labelEnd = x + text.Length * 8 + 10;
        AddDivider(labelEnd, y + 4, sw - labelEnd - 10);
        y += 16;
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
