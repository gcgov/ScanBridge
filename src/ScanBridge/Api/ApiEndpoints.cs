using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ScanBridge.Scanning;
using ScanBridge.Settings;

namespace ScanBridge.Api;

/// <summary>
/// Maps the localhost HTTP surface. All responses are camelCase JSON except the
/// finished document, which is served as application/pdf or image/jpeg.
/// </summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app, SettingsStore settingsStore, ScannerService scannerService, ScanJobManager jobManager)
    {
        var version = AppInfo.Version;
        var api = app.MapGroup("/api/v1");

        api.MapGet("/status", () =>
        {
            var settings = settingsStore.Current;
            return Results.Ok(new StatusResponse(
                App: "ScanBridge",
                Version: version,
                ApiVersion: 1,
                ScannerConfigured: !string.IsNullOrEmpty(settings.ScannerDeviceId),
                ScannerName: settings.ScannerDeviceName));
        });

        api.MapGet("/scanners", async (CancellationToken cancellationToken, string? driver = null, bool refresh = false) =>
        {
            var settings = settingsStore.Current;
            var scanners = await scannerService.ListScannersAsync(
                driver ?? settings.ScannerDriver, refresh, cancellationToken);
            return Results.Ok(scanners.Select(s => new ScannerDto(s.Id, s.Name, s.Driver)));
        });

        api.MapPost("/scans", (StartScanRequest? body) =>
        {
            body ??= new StartScanRequest();
            var settings = settingsStore.Current;

            if (!ScanFormats.TryNormalize(body.Format ?? settingsStore.Current.Defaults.Format, out var format))
            {
                return Error(StatusCodes.Status400BadRequest, "invalidFormat",
                    "format deve essere \"pdf\" oppure \"jpg\".");
            }

            var jpegQuality = body.JpegQuality ?? settings.Defaults.JpegQuality;
            if (!ScanFormats.IsValidJpegQuality(jpegQuality))
            {
                return Error(StatusCodes.Status400BadRequest, "invalidJpegQuality",
                    "jpegQuality deve essere compreso tra 1 e 100.");
            }

            var deviceId = body.ScannerId ?? settings.ScannerDeviceId;
            if (string.IsNullOrEmpty(deviceId))
            {
                return Error(StatusCodes.Status422UnprocessableEntity, ScanErrorCodes.NoScannerConfigured,
                    "Nessuno scanner configurato. Apri le impostazioni di ScanBridge e seleziona uno scanner.");
            }

            // A device id passed by the caller may not match the configured device; only
            // reuse the configured name/driver when the id is the configured one.
            var usingConfiguredDevice = body.ScannerId is null || body.ScannerId == settings.ScannerDeviceId;
            var defaults = settings.Defaults;
            var paperSource = body.Duplex == true ? "duplex" : body.PaperSource ?? defaults.PaperSource;
            var request = new ScanRequest(
                DeviceId: deviceId,
                DeviceName: usingConfiguredDevice ? settings.ScannerDeviceName : null,
                Driver: (usingConfiguredDevice ? settings.ScannerDriver : body.Driver) ?? body.Driver ?? "wia",
                PaperSource: paperSource,
                Dpi: body.Dpi ?? defaults.Dpi,
                ColorMode: body.ColorMode ?? defaults.ColorMode,
                PageSize: body.PageSize ?? defaults.PageSize,
                ExcludeBlankPages: body.ExcludeBlankPages ?? defaults.ExcludeBlankPages,
                AutoDeskew: body.AutoDeskew ?? defaults.AutoDeskew,
                Format: format,
                JpegQuality: jpegQuality);

            var job = jobManager.TryStart(request, out var errorCode);
            if (job is null)
            {
                return Error(StatusCodes.Status409Conflict, errorCode ?? ScanErrorCodes.ScannerBusy,
                    "Una scansione è già in corso.");
            }

            return Results.Accepted($"/api/v1/scans/{job.Id}", new StartScanResponse(job.Id));
        });

        api.MapGet("/scans/{jobId}", (string jobId) =>
        {
            var job = jobManager.Get(jobId);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, "notFound", "Scansione sconosciuta.");
            }

            return Results.Ok(ToResponse(job));
        });

        api.MapGet("/scans/{jobId}/document", (string jobId, bool deleteAfterDownload = false) =>
        {
            var job = jobManager.Get(jobId);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, "notFound", "Scansione sconosciuta.");
            }

            if (job.Status != ScanJobStatus.Completed)
            {
                if (job.IsTerminal)
                {
                    return Error(StatusCodes.Status410Gone, job.ErrorCode ?? ScanErrorCodes.ScanFailed,
                        job.ErrorMessage ?? "La scansione non è stata completata.");
                }

                return Error(StatusCodes.Status409Conflict, "notReady", "La scansione non è ancora terminata.");
            }

            var content = job.ContentBytes!;
            if (deleteAfterDownload)
            {
                // The response keeps its own reference to the bytes; this frees the job right away.
                jobManager.CancelOrDiscard(jobId);
            }

            return Results.File(content, contentType: job.ContentType,
                fileDownloadName: ScanFormats.FileNameForContentType(job.ContentType));
        });

        api.MapDelete("/scans/{jobId}", (string jobId) =>
        {
            jobManager.CancelOrDiscard(jobId);
            return Results.NoContent();
        });
    }

    private static ScanJobResponse ToResponse(ScanJob job) => new(
        JobId: job.Id,
        Status: job.Status switch
        {
            ScanJobStatus.Pending => "pending",
            ScanJobStatus.Scanning => "scanning",
            ScanJobStatus.Processing => "processing",
            ScanJobStatus.Completed => "completed",
            ScanJobStatus.Failed => "failed",
            ScanJobStatus.Canceled => "canceled",
            _ => "unknown",
        },
        PagesScanned: job.PagesScanned,
        Error: job.ErrorCode is null ? null : new ApiErrorBody(job.ErrorCode, job.ErrorMessage ?? string.Empty));

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ApiErrorResponse(new ApiErrorBody(code, message)), statusCode: statusCode);
}
