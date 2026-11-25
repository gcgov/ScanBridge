using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Pdf;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;

namespace ScanBridge.Scanning;

public sealed record ScannerInfo(string Id, string Name, string Driver);

/// <summary>
/// The only class that talks to NAPS2. Owns a long-lived <see cref="ScanningContext"/>
/// (with the 32-bit worker set up so 32-bit TWAIN drivers work from this 64-bit process)
/// and turns a <see cref="ScanRequest"/> into PDF or JPEG bytes.
/// </summary>
public sealed class ScannerService : IDisposable
{
    private readonly ILogger _logger;
    private readonly ScanningContext _scanningContext;
    private readonly ScanController _controller;
    private readonly Dictionary<Driver, List<ScannerInfo>> _deviceCache = new();
    private readonly SemaphoreSlim _deviceCacheLock = new(1, 1);

    public ScannerService(ILogger logger)
    {
        _logger = logger;
        _scanningContext = new ScanningContext(new GdiImageContext());
        try
        {
            // Spawns scans through a 32-bit worker process (NAPS2.Worker.exe) when needed,
            // which is required for 32-bit TWAIN drivers.
            _scanningContext.SetUpWin32Worker();
        }
        catch (Exception ex)
        {
            // WIA/ESCL still work without the worker; don't fail startup over TWAIN support.
            _logger.LogWarning(ex, "Could not set up the 32-bit TWAIN worker; TWAIN devices may be unavailable");
        }

        _controller = new ScanController(_scanningContext);
    }

    public async Task<List<ScannerInfo>> ListScannersAsync(string? driverName, bool refresh, CancellationToken cancellationToken)
    {
        var driver = ParseDriver(driverName);
        await _deviceCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _deviceCache.TryGetValue(driver, out var cached))
            {
                return cached;
            }

            var devices = await _controller.GetDeviceList(driver).ConfigureAwait(false);
            var result = devices
                .Select(d => new ScannerInfo(d.ID, d.Name, driver.ToString().ToLowerInvariant()))
                .ToList();
            _deviceCache[driver] = result;
            return result;
        }
        finally
        {
            _deviceCacheLock.Release();
        }
    }

    public async Task<byte[]> ScanAsync(ScanRequest request, IProgress<int> pageProgress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.DeviceId))
        {
            throw new ScanBridgeException(ScanErrorCodes.NoScannerConfigured,
                "Nessuno scanner configurato. Selezionane uno nelle impostazioni di ScanBridge oppure indica scannerId.");
        }

        var driver = ParseDriver(request.Driver);
        var options = new ScanOptions
        {
            Device = new ScanDevice(driver, request.DeviceId, request.DeviceName ?? request.DeviceId),
            Driver = driver,
            PaperSource = ParsePaperSource(request.PaperSource),
            Dpi = request.Dpi,
            BitDepth = ParseBitDepth(request.ColorMode),
            PageSize = ParsePageSize(request.PageSize),
            ExcludeBlankPages = request.ExcludeBlankPages,
            AutoDeskew = request.AutoDeskew,
            UseNativeUI = false,
        };

        var images = new List<ProcessedImage>();
        try
        {
            _logger.LogInformation(
                "Starting scan on {Device} ({Driver}, {Source}, {Dpi} dpi)",
                options.Device.Name, driver, options.PaperSource, options.Dpi);

            await foreach (var image in _controller.Scan(options, cancellationToken).ConfigureAwait(false))
            {
                images.Add(image);
                pageProgress.Report(images.Count);
            }

            if (images.Count == 0)
            {
                throw new ScanBridgeException(ScanErrorCodes.NoPages,
                    "Nessuna pagina scansionata. Controlla che il documento sia inserito nello scanner.");
            }

            using var stream = new MemoryStream();
            if (request.Format == ScanFormats.Jpg)
            {
                // Photos: only the first image is delivered, any others are discarded.
                using var rendered = images[0].Render();
                rendered.Save(stream, ImageFileFormat.Jpeg, new ImageSaveOptions { Quality = request.JpegQuality });
                _logger.LogInformation("Scan finished: {Pages} page(s) scanned, first one returned as JPEG ({Bytes} bytes)",
                    images.Count, stream.Length);
                return stream.ToArray();
            }

            var exporter = new PdfExporter(_scanningContext);
            await exporter.Export(stream, images, new PdfExportParams()).ConfigureAwait(false);
            _logger.LogInformation("Scan finished: {Pages} page(s), {Bytes} bytes of PDF", images.Count, stream.Length);
            return stream.ToArray();
        }
        catch (DeviceOfflineException ex)
        {
            throw new ScanBridgeException(ScanErrorCodes.DeviceOffline,
                "Lo scanner non risponde. Controlla che sia acceso e collegato al computer.", ex);
        }
        catch (DeviceException ex)
        {
            throw new ScanBridgeException(ScanErrorCodes.ScanFailed,
                $"La scansione non è riuscita: {ex.Message}", ex);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    internal static Driver ParseDriver(string? driver) => driver?.ToLowerInvariant() switch
    {
        "twain" => Driver.Twain,
        "escl" => Driver.Escl,
        _ => Driver.Wia,
    };

    internal static PaperSource ParsePaperSource(string? source) => source?.ToLowerInvariant() switch
    {
        "flatbed" => PaperSource.Flatbed,
        "duplex" => PaperSource.Duplex,
        _ => PaperSource.Feeder,
    };

    internal static BitDepth ParseBitDepth(string? colorMode) => colorMode?.ToLowerInvariant() switch
    {
        "grayscale" => BitDepth.Grayscale,
        "blackandwhite" => BitDepth.BlackAndWhite,
        _ => BitDepth.Color,
    };

    internal static PageSize ParsePageSize(string? pageSize) => pageSize?.ToLowerInvariant() switch
    {
        "legal" => PageSize.Legal,
        "a4" => PageSize.A4,
        _ => PageSize.A4,
    };

    public void Dispose()
    {
        _deviceCacheLock.Dispose();
        _scanningContext.Dispose();
    }
}
