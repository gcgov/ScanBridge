using ScanBridge.Settings;
using Xunit;

namespace ScanBridge.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ScanBridgeTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultPageSizeIsA4AndDefaultFormatIsPdf()
    {
        var defaults = new AppSettings().Defaults;

        Assert.Equal("a4", defaults.PageSize);
        Assert.Equal("pdf", defaults.Format);
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
        settings.Defaults.Format = "jpg";
        store.Save(settings);

        var reloaded = new SettingsStore(_tempDir).Load();
        Assert.Equal(9111, reloaded.Port);
        Assert.Equal(new[] { "https://apps.example.gov" }, reloaded.AllowedOrigins);
        Assert.Equal("twain", reloaded.ScannerDriver);
        Assert.Equal("dev-1", reloaded.ScannerDeviceId);
        Assert.Equal("Test Scanner", reloaded.ScannerDeviceName);
        Assert.Equal(200, reloaded.Defaults.Dpi);
        Assert.Equal("duplex", reloaded.Defaults.PaperSource);
        Assert.Equal("jpg", reloaded.Defaults.Format);
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
