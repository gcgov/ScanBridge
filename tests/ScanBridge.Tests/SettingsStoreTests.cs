using System.Globalization;
using ScanBridge.Localization;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Xunit;

namespace ScanBridge.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ScanBridgeTests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("US", "letter")]
    [InlineData("CA", "letter")]
    [InlineData("MX", "letter")]
    [InlineData("IT", "a4")]
    [InlineData("GB", "a4")]
    [InlineData("DE", "a4")]
    public void DefaultPageSizeFollowsTheRegion(string region, string expected)
    {
        Assert.Equal(expected, ScanDefaults.DefaultPageSizeFor(new RegionInfo(region)));
    }

    [Fact]
    public void NewSettingsFollowTheWindowsLanguage()
    {
        Assert.Equal(UiLanguage.Automatic, new AppSettings().Language);
    }

    [Fact]
    public void SettingsFileWithoutNewKeysKeepsWorking()
    {
        // A settings file from version 1.0 has no "language" and no "jpegQuality".
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"),
            """{ "port": 7300, "defaults": { "pageSize": "legal" } }""");

        var loaded = new SettingsStore(_tempDir).Load();

        Assert.Equal(7300, loaded.Port);
        Assert.Equal("legal", loaded.Defaults.PageSize);
        Assert.Equal(UiLanguage.Automatic, loaded.Language);
        Assert.Equal(ScanFormats.DefaultJpegQuality, loaded.Defaults.JpegQuality);
    }

    [Fact]
    public void LoadWithoutFileReturnsDefaultsAndFlagsFirstRun()
    {
        var store = new SettingsStore(_tempDir);
        var settings = store.Load();

        Assert.True(store.IsFirstRun);
        Assert.Equal(AppSettings.DefaultPort, settings.Port);
        Assert.Empty(settings.AllowedOrigins);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new SettingsStore(_tempDir);
        store.Load();

        var settings = new AppSettings
        {
            Port = 9111,
            AllowedOrigins = ["https://apps.example.gov"],
            ScannerDriver = "twain",
            ScannerDeviceId = "dev-1",
            ScannerDeviceName = "Test Scanner",
            RunAtLogin = false,
        };
        settings.Defaults.Dpi = 200;
        settings.Defaults.PaperSource = "duplex";
        settings.Language = "it";
        store.Save(settings);

        var reloaded = new SettingsStore(_tempDir).Load();
        Assert.Equal(9111, reloaded.Port);
        Assert.Equal(new[] { "https://apps.example.gov" }, reloaded.AllowedOrigins);
        Assert.Equal("twain", reloaded.ScannerDriver);
        Assert.Equal("dev-1", reloaded.ScannerDeviceId);
        Assert.Equal("Test Scanner", reloaded.ScannerDeviceName);
        Assert.Equal(200, reloaded.Defaults.Dpi);
        Assert.Equal("duplex", reloaded.Defaults.PaperSource);
        Assert.Equal("it", reloaded.Language);
        Assert.False(reloaded.RunAtLogin);
    }

    [Fact]
    public void SaveRaisesChanged()
    {
        var store = new SettingsStore(_tempDir);
        store.Load();

        AppSettings? observed = null;
        store.Changed += s => observed = s;
        store.Save(new AppSettings { Port = 9112 });

        Assert.NotNull(observed);
        Assert.Equal(9112, observed!.Port);
        Assert.False(store.IsFirstRun);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "{not json!!");

        var settings = new SettingsStore(_tempDir).Load();
        Assert.Equal(AppSettings.DefaultPort, settings.Port);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
