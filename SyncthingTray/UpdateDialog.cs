using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SyncthingTray;

/// <summary>
/// Manual update checker — no telemetry, no background requests.
/// User clicks the button, we check GitHub once, download if needed.
/// </summary>
internal sealed class UpdateDialog : Form
{
    private static readonly HttpClient _http = CreateHttpClient();

    private readonly Label _lblStatus;
    private readonly Label _lblDetail;
    private readonly Panel _progressOuter;
    private readonly Panel _progressFill;
    private readonly Button _btnAction;
    private readonly Button _btnCancel;
    private CancellationTokenSource? _cts;

    private string? _remoteVersion;
    private string? _downloadUrl;
    private string? _hashFileUrl;

    private readonly Font _boldFont;
    private readonly Font _italicFont;
    private readonly Font _btnFont;

    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueePos;
    private bool _marqueeForward = true;

    private const string AppName = "SyncthingTray";
    private const string GitHubRepo = "itsnateai/synctray";

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DimColor = Color.FromArgb(0xA0, 0xA0, 0xC0);
    private static readonly Color WarnColor = Color.FromArgb(255, 152, 0);
    private static readonly Color ProgressBg = Color.FromArgb(0x2A, 0x2A, 0x3E);
    private static readonly Color ProgressFg = Color.FromArgb(76, 175, 80);

    public UpdateDialog()
    {
        Text = $"{AppName} — Update";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(420, 180);
        BackColor = BgColor;
        ForeColor = FgColor;
        ShowInTaskbar = false;

        _boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _italicFont = new Font("Segoe UI", 7.5f, FontStyle.Italic);
        _btnFont = new Font("Segoe UI", 8f);

        _lblStatus = new Label
        {
            Text = "Checking GitHub for new version...",
            Location = new Point(20, 20),
            Size = new Size(370, 24),
            Font = _boldFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblStatus);

        _lblDetail = new Label
        {
            Text = "",
            Location = new Point(20, 48),
            Size = new Size(370, 20),
            ForeColor = DimColor,
            BackColor = BgColor,
            Font = _italicFont,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblDetail);

        _progressOuter = new Panel
        {
            Location = new Point(30, 80),
            Size = new Size(350, 18),
            BackColor = ProgressBg,
            BorderStyle = BorderStyle.None
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 18),
            BackColor = ProgressFg
        };
        _progressOuter.Controls.Add(_progressFill);
        Controls.Add(_progressOuter);

        _btnAction = new Button
        {
            Text = "Upgrade Now",
            Location = new Point(155, 112),
            Size = new Size(110, 32),
            Visible = false,
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _btnFont,
        };
        _btnAction.Click += OnActionClick;
        Controls.Add(_btnAction);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(295, 112),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _btnFont,
        };
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _marqueeTimer.Tick += (_, _) =>
        {
            const int step = 4, barW = 80;
            if (_marqueeForward) _marqueePos += step; else _marqueePos -= step;
            if (_marqueePos + barW >= _progressOuter.Width) _marqueeForward = false;
            if (_marqueePos <= 0) _marqueeForward = true;
            _progressFill.Location = new Point(_marqueePos, 0);
            _progressFill.Size = new Size(barW, 18);
        };

        Shown += async (_, _) =>
        {
            if (IsWingetManaged())
            {
                ShowWingetNotice();
                return;
            }
            await CheckForUpdateAsync();
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppName, version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ─── Check GitHub ───────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        _cts = new CancellationTokenSource();
        _marqueeTimer.Start();

        try
        {
            var response = await _http.GetAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest",
                _cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                    ? vals.FirstOrDefault() : null;
                ShowError(remaining == "0"
                    ? "GitHub API rate limit reached." : "GitHub API access denied (403).",
                    remaining == "0" ? "Try again in a few minutes." : "Check your network connection.");
                return;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ShowError("No releases found on GitHub.", "The repository may not have any published releases.");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _remoteVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Equals("SyncthingTray.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        _downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    }
                    if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        _hashFileUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    }
                }
            }

            if (string.IsNullOrEmpty(_downloadUrl))
            {
                ShowError("No update package found in the latest release.", "The release may be incomplete.");
                return;
            }

            ShowVersionComparison();
        }
        catch (TaskCanceledException)
        {
            if (_cts?.IsCancellationRequested != true)
                ShowError("Request timed out.", "Check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            ShowError("Could not reach GitHub.", ex.Message);
        }
        catch (JsonException)
        {
            ShowError("Unexpected response from GitHub.", "The API response format may have changed.");
        }
        catch (Exception ex)
        {
            ShowError("Update check failed.", ex.Message);
        }
    }

    // ─── Compare Versions ───────────────────────────────────────

    private void ShowVersionComparison()
    {
        _marqueeTimer.Stop();
        _progressFill.Size = new Size(0, 18);
        _progressFill.Location = new Point(0, 0);

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var isNewer = Version.TryParse(_remoteVersion, out var remote)
                   && Version.TryParse(localVersion, out var local)
                   && remote > local;

        _lblDetail.Text = $"Current: {localVersion}  →  GitHub: {_remoteVersion}";
        _progressOuter.Visible = false;

        if (isNewer)
        {
            _lblStatus.Text = "A new version is available!";
            _btnAction.Text = "Upgrade Now";
            _btnAction.Visible = true;
            _btnCancel.Text = "Cancel";
        }
        else
        {
            _lblStatus.Text = "You're on the latest version!";
            _btnAction.Visible = false;
            _btnCancel.Text = "OK";
            _btnCancel.Location = new Point(170, 112);
        }
    }

    // ─── Download & Apply ───────────────────────────────────────

    private async void OnActionClick(object? sender, EventArgs e)
    {
        _btnAction.Enabled = false;
        _btnCancel.Text = "Cancel";
        _progressOuter.Visible = true;
        _progressFill.Location = new Point(0, 0);
        _lblStatus.Text = $"Downloading {AppName} {_remoteVersion}...";

        // Validate download URL origin before downloading
        if (!_downloadUrl!.StartsWith("https://github.com/itsnateai/", StringComparison.OrdinalIgnoreCase) &&
            !_downloadUrl.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Update failed: download URL is not from the expected source.", _downloadUrl);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        var newPath = exePath + ".new";
        var oldPath = exePath + ".old";

        try
        {
            if (!await DownloadFileAsync(_downloadUrl!, newPath))
                return;

            // SHA256 verification is mandatory — releases MUST publish SHA256SUMS
            if (string.IsNullOrEmpty(_hashFileUrl))
            {
                TryDelete(newPath);
                ShowError("Integrity verification unavailable.",
                    "This release does not publish SHA256SUMS. Update aborted.");
                return;
            }

            _lblStatus.Text = "Verifying integrity...";
            string hashContent;
            try
            {
                hashContent = await _http.GetStringAsync(_hashFileUrl, _cts!.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception hashEx)
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "Could not fetch SHA256SUMS: " + hashEx.Message);
                return;
            }

            string? expectedHash = ParseShaSum(hashContent, "SyncthingTray.exe");

            if (string.IsNullOrEmpty(expectedHash))
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "SHA256SUMS has no entry for SyncthingTray.exe.");
                return;
            }

            var actualHash = ComputeFileHash(newPath);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "Downloaded file does not match the expected SHA256 checksum.");
                return;
            }

            _lblStatus.Text = "Applying update...";
            _progressOuter.Visible = false;

            // Crash sentinel: written BEFORE the file swap. The new version's
            // TrayApplicationContext deletes this file after 30s of stable operation.
            // If the next launch sees this sentinel still present, the previous boot
            // crashed before proving itself stable and the user is told to manually
            // restore the .old backup.
            WriteCrashSentinel();

            TryDelete(oldPath);
            if (File.Exists(exePath))
                File.Move(exePath, oldPath);
            File.Move(newPath, exePath);

            using var _ = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--after-update",
                UseShellExecute = true
            });
            Application.Exit();
        }
        catch (IOException ex)
        {
            TryRollback(exePath, oldPath, newPath, ex);
        }
        catch (TaskCanceledException)
        {
            TryRollback(exePath, oldPath, newPath, null);
            if (!IsDisposed) ShowVersionComparison();
        }
        catch (Exception ex)
        {
            TryRollback(exePath, oldPath, newPath, ex);
        }
    }

    private void TryRollback(string exePath, string oldPath, string newPath, Exception? cause)
    {
        bool rollbackOk = true;
        if (File.Exists(oldPath))
        {
            TryDelete(exePath);
            try
            {
                File.Move(oldPath, exePath);
            }
            catch (Exception rbEx)
            {
                rollbackOk = false;
                if (!IsDisposed)
                    ShowError("Update failed AND rollback failed.",
                        $"Manually rename \"{Path.GetFileName(oldPath)}\" back to \"{Path.GetFileName(exePath)}\". ({rbEx.Message})");
            }
        }
        TryDelete(newPath);
        TryDeleteCrashSentinel();

        if (rollbackOk && !IsDisposed)
        {
            if (cause is IOException ioEx &&
                ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Cannot replace the executable.",
                    "Your antivirus may be locking the file. Try again.");
            }
            else if (cause is not null)
            {
                ShowError("Update failed.", cause.Message);
            }
        }
    }

    // ─── Crash-sentinel helpers (static, callable from Program + TrayContext) ──

    internal static string CrashSentinelPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncthingTray");
            try { Directory.CreateDirectory(dir); } catch { /* fall back below */ }
            return Path.Combine(dir, ".crashguard");
        }
    }

    private static void WriteCrashSentinel()
    {
        try
        {
            File.WriteAllText(CrashSentinelPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort — absence of sentinel just disables auto-rollback detection */ }
    }

    internal static void TryDeleteCrashSentinel()
    {
        try
        {
            var path = CrashSentinelPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    private async Task<bool> DownloadFileAsync(string url, string destPath)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts!.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            downloaded += read;

            if (totalBytes > 0 && !IsDisposed) BeginInvoke(() =>
            {
                if (IsDisposed) return;
                int pct = (int)(downloaded * 100 / totalBytes);
                _progressFill.Size = new Size(
                    (int)(_progressOuter.Width * downloaded / totalBytes), 18);
                var dlMB = downloaded / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);
                _lblDetail.Text = totalMB < 1
                    ? $"{pct}% ({downloaded / 1024.0:F0} / {totalBytes / 1024.0:F0} KB)"
                    : $"{pct}% ({dlMB:F0} / {totalMB:F0} MB)";
            });
        }

        if (totalBytes > 0 && downloaded != totalBytes)
        {
            TryDelete(destPath);
            ShowError("Download was incomplete.",
                      $"Expected {totalBytes:N0} bytes, got {downloaded:N0}.");
            return false;
        }

        // Minimum size sanity check — reject truncated/empty downloads
        if (downloaded < 100_000)
        {
            TryDelete(destPath);
            ShowError("Downloaded file is too small.",
                      $"Got {downloaded:N0} bytes — expected a valid executable.");
            return false;
        }

        return true;
    }

    // ─── Error ──────────────────────────────────────────────────

    private void ShowError(string message, string detail)
    {
        _marqueeTimer.Stop();
        _progressOuter.Visible = false;
        _lblStatus.Text = message;
        _lblStatus.ForeColor = WarnColor;
        _lblDetail.Text = detail;
        _btnAction.Visible = false;
        _btnCancel.Text = "OK";
        _btnCancel.Location = new Point(170, 112);
    }

    // ─── Static Helpers (called from Program.cs) ────────────────

    /// <summary>
    /// Torn-state recovery only: if the update was interrupted between moving
    /// exe→.old and .new→exe, the exe is gone but .old still has the previous
    /// version. Restore it so the tray can launch. Called from Program.Main
    /// before any tray UI.
    /// </summary>
    internal static void RecoverFromTornUpdate()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || File.Exists(exePath)) return;

        var oldPath = exePath + ".old";
        if (File.Exists(oldPath))
        {
            try { File.Move(oldPath, exePath); } catch { }
        }
    }

    /// <summary>
    /// Proactive cleanup of stale .old/.new files. Safe to call ONLY after the
    /// current version has proven itself stable (see TrayApplicationContext's
    /// stability timer) — otherwise a post-update crash leaves the user with
    /// no backup to roll back to.
    /// </summary>
    internal static void CleanupStaleUpdateArtifacts()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

        foreach (var suffix in new[] { ".old", ".new" })
        {
            var path = exePath + suffix;
            if (!File.Exists(path)) continue;
            try { File.Delete(path); } catch { /* locked; try again next stable boot */ }
        }
    }

    /// <summary>Show a brief floating toast near the system tray after a successful update.</summary>
    internal static void ShowUpdateToast()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            var toast = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = BgColor,
                ForeColor = FgColor,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 8, 12, 8)
            };
            var toastFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var lbl = new Label
            {
                Text = $"\u2705 {AppName} updated to v{version}!",
                AutoSize = true,
                Font = toastFont,
                ForeColor = FgColor,
                BackColor = BgColor,
            };
            toast.Controls.Add(lbl);
            toast.FormClosed += (_, _) => toastFont.Dispose();

            var screen = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            toast.Load += (_, _) =>
                toast.Location = new Point(screen.Right - toast.Width - 20, screen.Bottom - toast.Height - 20);
            toast.Show();

            var dismiss = new System.Windows.Forms.Timer { Interval = 5000 };
            dismiss.Tick += (_, _) =>
            {
                dismiss.Stop();
                dismiss.Dispose();
                toast.Close();
            };
            dismiss.Start();
        };
        timer.Start();
    }

    // ─── Winget Detection ──────────────────────────────────────

    private static bool IsWingetManaged() =>
        (Environment.ProcessPath ?? "").Contains(@"Microsoft\WinGet\Packages", StringComparison.OrdinalIgnoreCase);

    private void ShowWingetNotice()
    {
        _marqueeTimer.Stop();
        _progressOuter.Visible = false;
        _lblStatus.Text = "This installation is managed by winget.";
        _lblStatus.ForeColor = WarnColor;
        _lblDetail.Text = "Use:  winget upgrade itsnateai.SyncthingTray";
        _btnAction.Visible = false;
        _btnCancel.Text = "OK";
        _btnCancel.Location = new Point(170, 112);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Parse a GNU-style SHA256SUMS file body and return the hex digest for the
    /// named file, or null if not found. Accepts "hash  name" and "hash *name"
    /// formats, is case-insensitive on the filename, and ignores blank/comment lines.
    /// </summary>
    internal static string? ParseShaSum(string content, string fileName)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fileName)) return null;
        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var name = parts[1].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].Trim();
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont.Dispose();
            _italicFont.Dispose();
            _btnFont.Dispose();
            _marqueeTimer.Stop();
            _marqueeTimer.Dispose();
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
        }
        base.Dispose(disposing);
    }
}
