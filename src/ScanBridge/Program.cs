using Microsoft.Extensions.Logging;
using ScanBridge.App;
using ScanBridge.Hosting;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;
using Serilog.Extensions.Logging;

namespace ScanBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\ScanBridge.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another ScanBridge is already running (and owns the port); just exit quietly.
            return;
        }

        var logDirectory = ScanBridge.App.LogFolder.Path;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "scanbridge-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            Log.Information("ScanBridge starting");

            var settingsStore = new SettingsStore();
            settingsStore.Load();

            try
            {
                StartupRegistration.Apply(settingsStore.Current.RunAtLogin);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not update the run-at-login registration");
            }

            using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
            using var scannerService = new ScannerService(loggerFactory.CreateLogger("ScanBridge.Scanning"));
            using var jobManager = new ScanJobManager(scannerService.ScanAsync);
            var webHostRunner = new WebHostRunner(settingsStore, scannerService, jobManager, Log.Logger);

            settingsStore.Changed += settings =>
            {
                try
                {
                    StartupRegistration.Apply(settings.RunAtLogin);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not update the run-at-login registration");
                }

                // Port or origins may have changed; rebuild the listener.
                _ = webHostRunner.StartAsync();
            };

            // No message loop yet, so blocking here cannot deadlock the UI.
            webHostRunner.StartAsync().GetAwaiter().GetResult();

            var openSettingsOnStartup = settingsStore.IsFirstRun && !args.Contains("--minimized");

            ApplicationConfiguration.Initialize();
            using var trayContext = new TrayApplicationContext(
                settingsStore, scannerService, jobManager, webHostRunner, openSettingsOnStartup);
            Application.Run(trayContext);

            webHostRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Log.Information("ScanBridge exited cleanly");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ScanBridge crashed");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
