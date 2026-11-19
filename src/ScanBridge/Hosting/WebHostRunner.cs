using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScanBridge.Api;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;

namespace ScanBridge.Hosting;

/// <summary>
/// Hosts Kestrel inside the WinForms process, bound to 127.0.0.1 only. Rebuilds the
/// host when settings (port / allowed origins) change.
/// </summary>
public sealed class WebHostRunner : IAsyncDisposable
{
    private const string CorsPolicyName = "browser";

    private readonly SettingsStore _settingsStore;
    private readonly ScannerService _scannerService;
    private readonly ScanJobManager _jobManager;
    private readonly Serilog.ILogger _logger;
    private WebApplication? _app;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public WebHostRunner(SettingsStore settingsStore, ScannerService scannerService, ScanJobManager jobManager, Serilog.ILogger logger)
    {
        _settingsStore = settingsStore;
        _scannerService = scannerService;
        _jobManager = jobManager;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised on start/stop/failure so the tray and settings window can show listener state.</summary>
    public event Action? StatusChanged;

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var settings = _settingsStore.Current;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                });
                builder.Logging.ClearProviders();
                builder.Logging.AddSerilog(_logger);
                builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
                    .WithOrigins(settings.AllowedOrigins.ToArray())
                    .WithMethods("GET", "POST", "DELETE")
                    .WithHeaders(RequestGuards.RequiredHeaderName, "Content-Type")));
                builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, settings.Port));

                var app = builder.Build();
                app.UseHostValidation(settings.Port);
                app.UseCors(CorsPolicyName);
                app.UseRequiredHeader();
                ApiEndpoints.Map(app, _settingsStore, _scannerService, _jobManager);

                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                IsRunning = true;
                LastError = null;
                _logger.Information("ScanBridge API listening on http://127.0.0.1:{Port}", settings.Port);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = ex is IOException
                    ? $"Impossibile mettersi in ascolto sulla porta {settings.Port}: potrebbe essere usata da un altro programma."
                    : ex.Message;
                _logger.Error(ex, "Failed to start the ScanBridge API on port {Port}", settings.Port);
            }
        }
        finally
        {
            _lifecycleLock.Release();
            StatusChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error stopping the ScanBridge API");
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
            IsRunning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }
}
