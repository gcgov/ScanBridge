using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ScanBridge.Localization;
using ScanBridge.Scanning;
using ScanBridge.Settings;

namespace ScanBridge.Api;

/// <summary>
/// Maps the localhost HTTP surface. All responses are camelCase JSON except the
/// finished document, which is served as application/pdf or image/jpeg. Error
/// <c>message</c> texts are in the language of the request's Accept-Language header
/// (English when ScanBridge has no translation for it); error <c>code</c> values never change.
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

            // No format means PDF, whatever the settings say: a page written before JPG
            // support always expects a PDF back.
            if (!ScanFormats.TryNormalize(body.Format, out var format))
            {
                return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidFormat, Strings.ApiInvalidFormat);
            }

            // Reject a bad value from the caller, but never fail a scan over a bad value
            // in a hand-edited settings file.
            int jpegQuality;
            if (body.JpegQuality is { } requestedQuality)
            {
                if (!ScanFormats.IsValidJpegQuality(requestedQuality))
                {
                    return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidJpegQuality, Strings.ApiInvalidJpegQuality);
                }

                jpegQuality = requestedQuality;
            }
            else
            {
                jpegQuality = ScanFormats.IsValidJpegQuality(settings.Defaults.JpegQuality)
                    ? settings.Defaults.JpegQuality
                    : ScanFormats.DefaultJpegQuality;
            }

            var deviceId = body.ScannerId ?? settings.ScannerDeviceId;
            if (string.IsNullOrEmpty(deviceId))
            {
                return Error(StatusCodes.Status422UnprocessableEntity, ScanErrorCodes.NoScannerConfigured,
                    Strings.ScanNoScannerConfigured);
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
                    Strings.ScanScannerBusy);
            }

            return Results.Accepted($"/api/v1/scans/{job.Id}", new StartScanResponse(job.Id));
        });

        api.MapGet("/scans/{jobId}", (string jobId) =>
        {
            var job = jobManager.Get(jobId);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, Strings.ApiScanNotFound);
            }

            return Results.Ok(ToResponse(job));
        });

        api.MapGet("/scans/{jobId}/document", (string jobId, int page = 1, bool deleteAfterDownload = false) =>
        {
            var job = jobManager.Get(jobId);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, Strings.ApiScanNotFound);
            }

            if (job.Status != ScanJobStatus.Completed)
            {
                if (job.IsTerminal)
                {
                    return Error(StatusCodes.Status410Gone, job.ErrorCode ?? ScanErrorCodes.ScanFailed,
                        job.ErrorMessage?.ToString() ?? Strings.ApiScanDidNotComplete);
                }

                return Error(StatusCodes.Status409Conflict, ApiErrorCodes.NotReady, Strings.ApiScanNotFinished);
            }

            // A PDF holds every page in its single file. A JPG scan has one file per page.
            var files = job.Files!;
            if (page < 1 || page > files.Count)
            {
                return Error(StatusCodes.Status404NotFound, ApiErrorCodes.PageNotFound,
                    Strings.ApiPageNotFound(page, files.Count));
            }

            var content = files[page - 1];
            if (deleteAfterDownload)
            {
                // The response keeps its own reference to the bytes; this frees the job right away.
                jobManager.CancelOrDiscard(jobId);
            }

            return Results.File(content, contentType: job.ContentType,
                fileDownloadName: ScanFormats.FileNameFor(job.Format, page));
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
        Format: job.Format,
        FileCount: job.Files?.Count ?? 0,
        Error: job.ErrorCode is null ? null : new ApiErrorBody(job.ErrorCode, job.ErrorMessage?.ToString() ?? string.Empty));

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ApiErrorResponse(new ApiErrorBody(code, message)), statusCode: statusCode);
}
