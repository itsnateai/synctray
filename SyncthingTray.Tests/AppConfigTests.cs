using System.Text;

namespace SyncthingTray.Tests;

[TestClass]
public class AppConfigTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SyncthingTray_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void FirstRun_NoIniFile_IsFirstRunTrue()
    {
        var config = new AppConfig(_tempDir);
        Assert.IsTrue(config.IsFirstRun);
    }

    [TestMethod]
    public void FirstRun_DefaultValues()
    {
        var config = new AppConfig(_tempDir);
        Assert.AreEqual("webui", config.DblClickAction);
        Assert.AreEqual("pause", config.MiddleClickAction);
        Assert.IsFalse(config.RunOnStartup);
        Assert.IsFalse(config.StartBrowser);
        Assert.AreEqual(string.Empty, config.ApiKey);
        Assert.AreEqual("http://localhost:8384", config.WebUI);
        Assert.AreEqual(20, config.StartupDelay);
        Assert.IsFalse(config.NetworkAutoPause);
        Assert.IsFalse(config.AutoCheckUpdates);
    }

    [TestMethod]
    public void Save_CreatesIniFile()
    {
        var config = new AppConfig(_tempDir);
        config.ApiKey = "test-key-123";
        bool result = config.Save();

        Assert.IsTrue(result);
        Assert.IsTrue(File.Exists(config.SettingsFilePath));
    }

    [TestMethod]
    public void SaveAndLoad_RoundTrip()
    {
        var config = new AppConfig(_tempDir);
        config.DblClickAction = "rescan";
        config.MiddleClickAction = "none";
        config.RunOnStartup = true;
        config.StartBrowser = true;
        config.ApiKey = "my-api-key";
        config.WebUI = "https://localhost:8443";
        config.StartupDelay = 30;
        config.NetworkAutoPause = true;
        config.AutoCheckUpdates = true;
        config.Save();

        // Load into a fresh config
        var config2 = new AppConfig(_tempDir);
        Assert.AreEqual("rescan", config2.DblClickAction);
        Assert.AreEqual("none", config2.MiddleClickAction);
        Assert.IsTrue(config2.RunOnStartup);
        Assert.IsTrue(config2.StartBrowser);
        Assert.AreEqual("my-api-key", config2.ApiKey);
        Assert.AreEqual("https://localhost:8443", config2.WebUI);
        Assert.AreEqual(30, config2.StartupDelay);
        Assert.IsTrue(config2.NetworkAutoPause);
        Assert.IsTrue(config2.AutoCheckUpdates);
    }

    [TestMethod]
    public void BackwardCompat_OldBooleanSettings_MigrateToActions()
    {
        // Simulate old v1.x INI format with DblClickOpen and MiddleClickEnabled
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "[Settings]\nDblClickOpen=0\nMiddleClickEnabled=0\nApiKey=abc\n",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        Assert.AreEqual("none", config.DblClickAction);   // DblClickOpen=0 → "none"
        Assert.AreEqual("none", config.MiddleClickAction); // MiddleClickEnabled=0 → "none"
    }

    [TestMethod]
    public void BackwardCompat_OldBooleanEnabled_MigratesToDefaults()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "[Settings]\nDblClickOpen=1\nMiddleClickEnabled=1\n",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        Assert.AreEqual("webui", config.DblClickAction);   // DblClickOpen=1 → "webui"
        Assert.AreEqual("pause", config.MiddleClickAction); // MiddleClickEnabled=1 → "pause"
    }

    [TestMethod]
    public void NewActionSettings_TakePrecedenceOverOldBooleans()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath,
            "[Settings]\nDblClickOpen=1\nDblClickAction=rescan\nMiddleClickEnabled=1\nMiddleClickAction=webui\n",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        Assert.AreEqual("rescan", config.DblClickAction);  // New key wins
        Assert.AreEqual("webui", config.MiddleClickAction); // New key wins
    }

    [TestMethod]
    public void SyncExe_DefaultsToAppDirectory()
    {
        // Place a dummy exe so the co-located default wins over discovery
        var dummyExe = Path.Combine(_tempDir, "syncthing.exe");
        File.WriteAllText(dummyExe, "dummy");
        var config = new AppConfig(_tempDir);
        Assert.AreEqual(dummyExe, config.SyncExe);
    }

    [TestMethod]
    public void SyncExe_DiscoversFallbackWhenNotCoLocated()
    {
        // No syncthing.exe next to the app — should either discover one or keep the default path
        var config = new AppConfig(_tempDir);
        Assert.IsTrue(config.SyncExe.EndsWith("syncthing.exe", StringComparison.OrdinalIgnoreCase));
        // If discovery found a real one, it must exist
        if (config.SyncExe != Path.Combine(_tempDir, "syncthing.exe"))
            Assert.IsTrue(File.Exists(config.SyncExe), $"Discovered path should exist: {config.SyncExe}");
    }

    [TestMethod]
    public void ActionValueToIndex_ValidValues()
    {
        Assert.AreEqual(0, AppConfig.ActionValueToIndex("webui"));
        Assert.AreEqual(1, AppConfig.ActionValueToIndex("rescan"));
        Assert.AreEqual(2, AppConfig.ActionValueToIndex("pause"));
        Assert.AreEqual(3, AppConfig.ActionValueToIndex("none"));
    }

    [TestMethod]
    public void ActionValueToIndex_Unknown_ReturnsZero()
    {
        Assert.AreEqual(0, AppConfig.ActionValueToIndex("unknown"));
    }

    [TestMethod]
    public void ActionIndexToValue_ValidIndices()
    {
        Assert.AreEqual("webui", AppConfig.ActionIndexToValue(0));
        Assert.AreEqual("rescan", AppConfig.ActionIndexToValue(1));
        Assert.AreEqual("pause", AppConfig.ActionIndexToValue(2));
        Assert.AreEqual("none", AppConfig.ActionIndexToValue(3));
    }

    [TestMethod]
    public void ActionIndexToValue_OutOfRange_ReturnsNone()
    {
        Assert.AreEqual("none", AppConfig.ActionIndexToValue(-1));
        Assert.AreEqual("none", AppConfig.ActionIndexToValue(99));
    }

    [TestMethod]
    public void AtomicWrite_TmpFileNotLeftBehind()
    {
        var config = new AppConfig(_tempDir);
        config.Save();

        var tmpPath = config.SettingsFilePath + ".tmp";
        Assert.IsFalse(File.Exists(tmpPath), "Temp file should not remain after save");
    }

    [TestMethod]
    public void CorruptIni_UsesDefaults()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "this is not a valid ini file\n\x00\x01\x02",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        // Should not crash, should use defaults
        Assert.AreEqual("webui", config.DblClickAction);
        Assert.AreEqual(string.Empty, config.ApiKey);
    }

    [TestMethod]
    public void CorruptIni_LoadResultIsCorrupt()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "this line has no equals sign\nneither does this one\n",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        Assert.AreEqual(AppConfigLoadResult.Corrupt, config.LoadResult);
    }

    [TestMethod]
    public void CorruptIni_SaveCreatesBackup()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "garbage content with no parseable keys\n",
            new UTF8Encoding(false));

        var config = new AppConfig(_tempDir);
        Assert.AreEqual(AppConfigLoadResult.Corrupt, config.LoadResult);
        config.Save();

        var backups = Directory.GetFiles(_tempDir, "*.corrupt.bak");
        Assert.AreEqual(1, backups.Length, "Exactly one .corrupt.bak should be produced on save");
    }

    [TestMethod]
    public void ValidIni_LoadResultIsNone()
    {
        var iniPath = Path.Combine(_tempDir, "SyncthingTray.ini");
        File.WriteAllText(iniPath, "[Settings]\nApiKey=abc\n", new UTF8Encoding(false));
        var config = new AppConfig(_tempDir);
        Assert.AreEqual(AppConfigLoadResult.None, config.LoadResult);
    }

    [TestMethod]
    public void FirstRunStub_DoesNotLookCorrupt()
    {
        // After SeedFirstRunStub, the next launch must see LoadResult.None, not Corrupt
        var config = new AppConfig(_tempDir);
        Assert.IsTrue(config.IsFirstRun);
        config.SeedFirstRunStub();

        var config2 = new AppConfig(_tempDir);
        Assert.IsFalse(config2.IsFirstRun);
        Assert.AreEqual(AppConfigLoadResult.None, config2.LoadResult);
    }

    [TestMethod]
    public void DiagnosticLogging_DefaultsOn()
    {
        var config = new AppConfig(_tempDir);
        Assert.IsTrue(config.DiagnosticLogging, "Default should be true so field reports have content");
    }

    // ─── Security validators ─────────────────────────────────────────

    [TestMethod]
    public void ValidateWebUI_AcceptsLocalhost()
    {
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("http://localhost:8384"));
        Assert.AreEqual("http://127.0.0.1:8384", AppConfig.ValidateWebUI("http://127.0.0.1:8384"));
        Assert.AreEqual("https://localhost:8443", AppConfig.ValidateWebUI("https://localhost:8443"));
    }

    [TestMethod]
    public void ValidateWebUI_RejectsRemoteHost()
    {
        // Anything non-localhost must be rewritten to the safe default
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("http://evil.example.com:8384"));
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("http://192.168.1.50:8384"));
    }

    [TestMethod]
    public void ValidateWebUI_RejectsNonHttpScheme()
    {
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("file:///C:/Windows/System32"));
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("ftp://localhost:8384"));
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("javascript:alert(1)"));
    }

    [TestMethod]
    public void ValidateWebUI_RejectsGarbage()
    {
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI(""));
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("not a url"));
        Assert.AreEqual("http://localhost:8384", AppConfig.ValidateWebUI("   "));
    }

    [TestMethod]
    public void ValidateSyncExe_RejectsPathTraversal()
    {
        var dummy = Path.Combine(_tempDir, "syncthing.exe");
        File.WriteAllText(dummy, "x");
        Assert.IsNull(AppConfig.ValidateSyncExe(Path.Combine(_tempDir, "..", "syncthing.exe")));
    }

    [TestMethod]
    public void ValidateSyncExe_RejectsWrongFilename()
    {
        var attacker = Path.Combine(_tempDir, "malware.exe");
        File.WriteAllText(attacker, "x");
        Assert.IsNull(AppConfig.ValidateSyncExe(attacker));
    }

    [TestMethod]
    public void ValidateSyncExe_RejectsMissingFile()
    {
        Assert.IsNull(AppConfig.ValidateSyncExe(Path.Combine(_tempDir, "does-not-exist", "syncthing.exe")));
    }

    [TestMethod]
    public void ValidateSyncExe_AcceptsValidFile()
    {
        var dummy = Path.Combine(_tempDir, "syncthing.exe");
        File.WriteAllText(dummy, "x");
        Assert.AreEqual(dummy, AppConfig.ValidateSyncExe(dummy));
    }

    [TestMethod]
    public void ValidateSyncExe_RejectsNullOrEmpty()
    {
        Assert.IsNull(AppConfig.ValidateSyncExe(null));
        Assert.IsNull(AppConfig.ValidateSyncExe(""));
        Assert.IsNull(AppConfig.ValidateSyncExe("   "));
    }

    // ─── SHA256SUMS parser (security-critical update path) ───────────

    [TestMethod]
    public void ParseShaSum_StandardFormat()
    {
        var content = "abc123def456  SyncthingTray.exe\n";
        Assert.AreEqual("abc123def456", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_StarredBinaryMode()
    {
        var content = "abc123  *SyncthingTray.exe\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_CaseInsensitiveFilename()
    {
        var content = "abc123  SYNCTHINGTRAY.EXE\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_CrlfLineEndings()
    {
        var content = "abc123  SyncthingTray.exe\r\notherfile  other.exe\r\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_MultipleEntries_PicksCorrect()
    {
        var content = "deadbeef  OtherFile.exe\nabc123  SyncthingTray.exe\nffffff  Third.exe\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_NotFound_ReturnsNull()
    {
        var content = "abc123  OtherFile.exe\n";
        Assert.IsNull(UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_EmptyContent_ReturnsNull()
    {
        Assert.IsNull(UpdateDialog.ParseShaSum("", "SyncthingTray.exe"));
        Assert.IsNull(UpdateDialog.ParseShaSum("\n\n\n", "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_SkipsCommentsAndBlankLines()
    {
        var content = "# header comment\n\nabc123  SyncthingTray.exe\n\n# footer\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }

    [TestMethod]
    public void ParseShaSum_MalformedLines_Skipped()
    {
        var content = "not enough parts\njust-one-token\nabc123  SyncthingTray.exe\n";
        Assert.AreEqual("abc123", UpdateDialog.ParseShaSum(content, "SyncthingTray.exe"));
    }
}
