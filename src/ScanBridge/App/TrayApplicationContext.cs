using System.Diagnostics;
using ScanBridge.Hosting;
using ScanBridge.Localization;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;

namespace ScanBridge.App;

/// <summary>
/// The tray icon and its menu. ScanBridge has no main window — the tray icon is the app.
/// </summary>
/// <remarks>
/// Every text is built on the UI thread, in the desktop language. Events from scan jobs
/// and from the web host arrive on other threads, so the handlers go through
/// <see cref="PostToUi"/>, which also drops the caller's culture (a web request can carry
/// another language in its Accept-Language header).
/// </remarks>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly ScannerService _scannerService;
    private readonly ScanJobManager _jobManager;
    private readonly WebHostRunner _webHostRunner;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly SynchronizationContext _syncContext;
    private SettingsForm? _settingsForm;
    private bool _testScanRunning;
    private string _language;

    public TrayApplicationContext(
        SettingsStore settingsStore,
        ScannerService scannerService,
        ScanJobManager jobManager,
        WebHostRunner webHostRunner,
        bool openSettingsOnStartup)
    {
        _settingsStore = settingsStore;
        _scannerService = scannerService;
        _jobManager = jobManager;
        _webHostRunner = webHostRunner;
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _language = settingsStore.Current.Language;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ScanBridge",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _jobManager.JobStarted += OnJobStarted;
        _jobManager.JobFinished += OnJobFinished;
        _webHostRunner.StatusChanged += OnListenerStatusChanged;
        _settingsStore.Changed += OnSettingsChanged;
        UpdateStatus();

        if (_webHostRunner.LastError is { } startupError)
        {
            ShowBalloon("ScanBridge", startupError.ToString(), ToolTipIcon.Warning);
        }

        if (openSettingsOnStartup)
        {
            // Let the message loop start before opening the window.
            PostToUi(ShowSettings);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.TrayMenuSettings, null, (_, _) => ShowSettings());
        menu.Items.Add(Strings.TrayMenuTestScan, null, (_, _) => RunTestScan());
        menu.Items.Add(Strings.OpenLogFolder, null, (_, _) => LogFolder.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.TrayMenuExit, null, (_, _) => ExitApplication());
        return menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "scanbridge.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the tray icon; falling back to the stock icon");
        }

        return SystemIcons.Application;
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settingsStore, _scannerService, _webHostRunner);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private static async Task DeleteTempFileLaterAsync(string path)
    {
        // Retry: a viewer that keeps the file locked makes the first attempts fail.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Log.Warning("Could not delete temporary test scan {Path}", path);
    }

    private async void RunTestScan()
    {
        if (_testScanRunning)
        {
            return;
        }

        var settings = _settingsStore.Current;
        if (string.IsNullOrEmpty(settings.ScannerDeviceId))
        {
            ShowBalloon("ScanBridge", Strings.TrayNoScannerConfigured, ToolTipIcon.Warning);
            ShowSettings();
            return;
        }

        // The test scan proves the path a web app uses with no options: a PDF.
        var defaults = settings.Defaults;
        var request = new ScanRequest(
            DeviceId: settings.ScannerDeviceId,
            DeviceName: settings.ScannerDeviceName,
            Driver: settings.ScannerDriver ?? "wia",
            PaperSource: defaults.PaperSource,
            Dpi: defaults.Dpi,
            ColorMode: defaults.ColorMode,
            PageSize: defaults.PageSize,
            ExcludeBlankPages: defaults.ExcludeBlankPages,
            AutoDeskew: defaults.AutoDeskew);

        var job = _jobManager.TryStart(request, out _);
        if (job is null)
        {
            ShowBalloon("ScanBridge", Strings.ScanScannerBusy, ToolTipIcon.Warning);
            return;
        }

        _testScanRunning = true;
        try
        {
            ShowBalloon("ScanBridge", Strings.TrayTestScanStarted, ToolTipIcon.Info);
            while (!job.IsTerminal)
            {
                await Task.Delay(500);
            }

            if (job.Status == ScanJobStatus.Completed && job.Files is [var pdfBytes, ..])
            {
                var pdfPath = Path.Combine(Path.GetTempPath(), $"scanbridge-test-{job.Id}.pdf");
                await File.WriteAllBytesAsync(pdfPath, pdfBytes);
                Process.Start(new ProcessStartInfo(pdfPath) { UseShellExecute = true });

                // The PDF may contain personal data: don't leave it in %TEMP%. The viewer
                // needs a moment to open the file, so delete after a delay (best effort).
                _ = DeleteTempFileLaterAsync(pdfPath);
            }

            // Failure balloons come from OnJobFinished, shared with browser-initiated jobs.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Test scan failed");
            ShowBalloon("ScanBridge", Strings.TrayTestScanFailed(ex.Message), ToolTipIcon.Error);
        }
        finally
        {
            _testScanRunning = false;
            _jobManager.CancelOrDiscard(job.Id);
        }
    }

    private void OnJobStarted(ScanJob job) => UpdateStatus();

    private void OnJobFinished(ScanJob job) => PostToUi(() =>
    {
        switch (job.Status)
        {
            case ScanJobStatus.Completed:
                ShowBalloon("ScanBridge", Strings.TrayScanCompleted, ToolTipIcon.Info);
                break;
            case ScanJobStatus.Failed:
                ShowBalloon(Strings.TrayScanFailedTitle, job.ErrorMessage?.ToString() ?? string.Empty, ToolTipIcon.Error);
                break;
        }

        UpdateStatus();
    });

    private void OnListenerStatusChanged() => PostToUi(() =>
    {
        if (_webHostRunner.LastError is { } error)
        {
            ShowBalloon("ScanBridge", error.ToString(), ToolTipIcon.Warning);
        }

        UpdateStatus();
    });

    /// <summary>Rebuilds the menu when the language changes. The settings window applies it the next time it opens.</summary>
    private void OnSettingsChanged(AppSettings settings) => PostToUi(() =>
    {
        if (settings.Language == _language)
        {
            return;
        }

        _language = settings.Language;
        var oldMenu = _notifyIcon.ContextMenuStrip;
        oldMenu?.Items.Remove(_statusItem);
        _notifyIcon.ContextMenuStrip = BuildMenu();
        oldMenu?.Dispose();
        UpdateStatus();
    });

    /// <summary>Refreshes the tooltip on the tray icon and the status line at the top of the menu.</summary>
    private void UpdateStatus() => PostToUi(() =>
    {
        string state;
        if (!_webHostRunner.IsRunning)
        {
            state = _webHostRunner.LastError is null ? Strings.NotListening : Strings.TrayStatusServerError;
        }
        else if (_jobManager.ActiveJob is not null)
        {
            state = Strings.TrayStatusScanning;
        }
        else
        {
            state = Strings.TrayStatusReady(_settingsStore.Current.Port);
        }

        var text = Strings.TrayStatusLine(AppInfo.Version, state);
        _statusItem.Text = text;
        // NotifyIcon.Text is limited to 127 characters.
        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    });

    private void ShowBalloon(string title, string message, ToolTipIcon icon) =>
        PostToUi(() => _notifyIcon.ShowBalloonTip(5000, title, message, icon));

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, in the UI thread's own execution
    /// context. Suppressing the flow keeps the culture of a web request out of the tray.
    /// </summary>
    private void PostToUi(Action action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _syncContext.Post(_ => action(), null);
        }
    }

    private void ExitApplication()
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _jobManager.JobStarted -= OnJobStarted;
            _jobManager.JobFinished -= OnJobFinished;
            _webHostRunner.StatusChanged -= OnListenerStatusChanged;
            _settingsStore.Changed -= OnSettingsChanged;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
