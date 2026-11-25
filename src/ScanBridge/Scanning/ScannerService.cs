using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Pdf;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using ScanBridge.Localization;

namespace ScanBridge.Scanning;

public sealed record ScannerInfo(string Id, string Name, string Driver);

/// <summary>
/// The only class that talks to NAPS2. Owns a long-lived <see cref="ScanningContext"/>
/// (with the 32-bit worker set up so 32-bit TWAIN drivers work from this 64-bit process)
/// and turns a <see cref="ScanRequest"/> into a PDF, or into one JPEG per page.
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

    public async Task<ScanOutput> ScanAsync(ScanRequest request, IProgress<int> pageProgress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.DeviceId))
        {
            throw new ScanBridgeException(ScanErrorCodes.NoScannerConfigured,
                new LocalizedMessage(nameof(Strings.ScanNoScannerConfigured)));
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
                    new LocalizedMessage(nameof(Strings.ScanNoPages)));
            }

            if (request.Format == ScanFormats.Jpg)
            {
                // One JPEG per page, so a feeder or duplex scan loses nothing. Render()
                // applies the processing (deskew) that the PDF export also applies.
                var jpegs = new List<byte[]>(images.Count);
                var saveOptions = new ImageSaveOptions { Quality = request.JpegQuality };
                foreach (var image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var rendered = image.Render();
                    using var jpeg = new MemoryStream();
                    rendered.Save(jpeg, ImageFileFormat.Jpeg, saveOptions);
                    jpegs.Add(jpeg.ToArray());
                }

                _logger.LogInformation("Scan finished: {Pages} page(s) as JPEG, {Bytes} bytes",
                    jpegs.Count, jpegs.Sum(j => (long)j.Length));
                return new ScanOutput(ScanFormats.Jpg, jpegs);
            }

            using var stream = new MemoryStream();
            var exporter = new PdfExporter(_scanningContext);
            await exporter.Export(stream, images, new PdfExportParams()).ConfigureAwait(false);
            _logger.LogInformation("Scan finished: {Pages} page(s), {Bytes} bytes of PDF", images.Count, stream.Length);
            return ScanOutput.Pdf(stream.ToArray());
        }
        catch (DeviceOfflineException ex)
        {
            throw new ScanBridgeException(ScanErrorCodes.DeviceOffline,
                new LocalizedMessage(nameof(Strings.ScanDeviceOffline), ex.Message), ex);
        }
        catch (DeviceException ex)
        {
            throw new ScanBridgeException(ScanErrorCodes.ScanFailed,
                new LocalizedMessage(nameof(Strings.ScanFailed), ex.Message), ex);
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
        _ => PageSize.Letter,
    };

    public void Dispose()
    {
        _deviceCacheLock.Dispose();
        _scanningContext.Dispose();
    }
}
