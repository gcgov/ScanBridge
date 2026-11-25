using System.Diagnostics;
using ScanBridge.Hosting;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;

namespace ScanBridge.App;

/// <summary>
/// The tray icon and its menu. ScanBridge has no main window — the tray icon is the app.
/// </summary>
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

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Impostazioni…", null, (_, _) => ShowSettings());
        menu.Items.Add("Scansione di prova", null, (_, _) => RunTestScan());
        menu.Items.Add("Apri cartella dei log", null, (_, _) => LogFolder.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ScanBridge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _jobManager.JobStarted += OnJobStarted;
        _jobManager.JobFinished += OnJobFinished;
        _webHostRunner.StatusChanged += OnListenerStatusChanged;
        UpdateStatus();

        if (_webHostRunner.LastError is { } startupError)
        {
            ShowBalloon("ScanBridge", startupError, ToolTipIcon.Warning);
        }

        if (openSettingsOnStartup)
        {
            // Let the message loop start before opening the window.
            _syncContext.Post(_ => ShowSettings(), null);
        }
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
            ShowBalloon("ScanBridge", "Nessuno scanner configurato: apri prima le Impostazioni.", ToolTipIcon.Warning);
            ShowSettings();
            return;
        }

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
            AutoDeskew: defaults.AutoDeskew,
            Format: ScanFormats.TryNormalize(defaults.Format, out var format) ? format : ScanFormats.Pdf,
            JpegQuality: ScanFormats.IsValidJpegQuality(defaults.JpegQuality) ? defaults.JpegQuality : ScanFormats.DefaultJpegQuality);

        var job = _jobManager.TryStart(request, out var errorCode);
        if (job is null)
        {
            ShowBalloon("ScanBridge", "Una scansione è già in corso.", ToolTipIcon.Warning);
            return;
        }

        _testScanRunning = true;
        try
        {
            ShowBalloon("ScanBridge", "Scansione di prova in corso…", ToolTipIcon.Info);
            while (!job.IsTerminal)
            {
                await Task.Delay(500);
            }

            if (job.Status == ScanJobStatus.Completed && job.ContentBytes is { } pdfBytes)
            {
                var extension = Path.GetExtension(ScanFormats.FileNameForContentType(job.ContentType));
                var pdfPath = Path.Combine(Path.GetTempPath(), $"scanbridge-test-{job.Id}{extension}");
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
            ShowBalloon("ScanBridge", $"Scansione di prova non riuscita: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _testScanRunning = false;
            _jobManager.CancelOrDiscard(job.Id);
        }
    }

    private void OnJobStarted(ScanJob job) => UpdateStatus();

    private void OnJobFinished(ScanJob job)
    {
        switch (job.Status)
        {
            case ScanJobStatus.Completed:
                ShowBalloon("ScanBridge", "Scansione completata.", ToolTipIcon.Info);
                break;
            case ScanJobStatus.Failed:
                ShowBalloon("ScanBridge — scansione non riuscita", job.ErrorMessage ?? "La scansione non è riuscita.", ToolTipIcon.Error);
                break;
        }

        UpdateStatus();
    }

    private void OnListenerStatusChanged()
    {
        if (_webHostRunner.LastError is { } error)
        {
            ShowBalloon("ScanBridge", error, ToolTipIcon.Warning);
        }

        UpdateStatus();
    }

    /// <summary>Refreshes the tooltip on the tray icon and the status line at the top of the menu.</summary>
    private void UpdateStatus()
    {
        _syncContext.Post(_ =>
        {
            string state;
            if (!_webHostRunner.IsRunning)
            {
                state = _webHostRunner.LastError is null ? "Non in ascolto" : "Errore: server non avviato";
            }
            else if (_jobManager.ActiveJob is not null)
            {
                state = "Scansione in corso…";
            }
            else
            {
                state = $"Pronto · porta {_settingsStore.Current.Port}";
            }

            var text = $"ScanBridge v{AppInfo.Version} · {state}";
            _statusItem.Text = text;
            // NotifyIcon.Text is limited to 127 characters.
            _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
        }, null);
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        // Events can fire on worker threads; NotifyIcon belongs to the UI thread.
        _syncContext.Post(_ => _notifyIcon.ShowBalloonTip(5000, title, message, icon), null);
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
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
