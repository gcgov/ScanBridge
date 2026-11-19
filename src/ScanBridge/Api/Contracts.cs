namespace ScanBridge.Api;

public sealed record StatusResponse(
    string App,
    string Version,
    int ApiVersion,
    bool ScannerConfigured,
    string? ScannerName);

public sealed record ScannerDto(string Id, string Name, string Driver);

/// <summary>
/// Body of POST /api/v1/scans. Every field is optional; anything omitted falls back
/// to the defaults configured in the ScanBridge settings window.
/// </summary>
public sealed class StartScanRequest
{
    public string? ScannerId { get; set; }

    /// <summary>"wia", "twain" or "escl". Only used together with <see cref="ScannerId"/>.</summary>
    public string? Driver { get; set; }

    /// <summary>"flatbed", "feeder" or "duplex".</summary>
    public string? PaperSource { get; set; }

    /// <summary>Shorthand: true forces the paper source to "duplex".</summary>
    public bool? Duplex { get; set; }

    public int? Dpi { get; set; }

    /// <summary>"color", "grayscale" or "blackAndWhite".</summary>
    public string? ColorMode { get; set; }

    /// <summary>"letter", "legal" or "a4".</summary>
    public string? PageSize { get; set; }

    public bool? ExcludeBlankPages { get; set; }

    public bool? AutoDeskew { get; set; }

    /// <summary>"pdf" (default) or "jpg". With "jpg" only the first scanned image is returned.</summary>
    public string? Format { get; set; }

    /// <summary>JPEG quality 1-100. Only used with "jpg"; omitted falls back to the settings window.</summary>
    public int? JpegQuality { get; set; }
}

public sealed record StartScanResponse(string JobId);

public sealed record ApiErrorBody(string Code, string Message);

public sealed record ApiErrorResponse(ApiErrorBody Error);

public sealed record ScanJobResponse(
    string JobId,
    string Status,
    int PagesScanned,
    ApiErrorBody? Error);
