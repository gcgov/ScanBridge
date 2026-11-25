using ScanBridge.Localization;

namespace ScanBridge.Scanning;

public enum ScanJobStatus
{
    Pending,
    Scanning,
    Processing,
    Completed,
    Failed,
    Canceled,
}

/// <summary>Machine-readable error codes surfaced to API clients.</summary>
public static class ScanErrorCodes
{
    public const string ScannerBusy = "scannerBusy";
    public const string NoScannerConfigured = "noScannerConfigured";
    public const string DeviceOffline = "deviceOffline";
    public const string NoPages = "noPages";
    public const string Canceled = "canceled";
    public const string ScanFailed = "scanFailed";
}

/// <summary>Thrown by the scan pipeline with a machine-readable code from <see cref="ScanErrorCodes"/>.</summary>
public sealed class ScanBridgeException : Exception
{
    public ScanBridgeException(string code, LocalizedMessage text, Exception? inner = null)
        : base(text.ToLogString(), inner)
    {
        Code = code;
        Text = text;
    }

    public string Code { get; }

    /// <summary>The message for people, rendered in each reader's language. <see cref="Exception.Message"/> is the English text.</summary>
    public LocalizedMessage Text { get; }
}

/// <summary>
/// Resolved parameters for one scan: request overrides already merged over configured defaults.
/// All values are plain strings/primitives so the job layer stays free of NAPS2 types.
/// </summary>
public sealed record ScanRequest(
    string? DeviceId,
    string? DeviceName,
    string Driver,
    string PaperSource,
    int Dpi,
    string ColorMode,
    string PageSize,
    bool ExcludeBlankPages,
    bool AutoDeskew,
    string Format = ScanFormats.Pdf,
    int JpegQuality = ScanFormats.DefaultJpegQuality);

/// <summary>
/// What a finished scan produced: one PDF that holds every page, or one JPEG per page.
/// </summary>
public sealed record ScanOutput(string Format, IReadOnlyList<byte[]> Files)
{
    public static ScanOutput Pdf(byte[] pdf) => new(ScanFormats.Pdf, [pdf]);
}

/// <summary>Output formats a scan can be delivered in.</summary>
public static class ScanFormats
{
    public const string Pdf = "pdf";
    public const string Jpg = "jpg";
    public const int DefaultJpegQuality = 85;

    public static bool IsValidJpegQuality(int quality) => quality is >= 1 and <= 100;

    /// <summary>
    /// Canonical format for a request value: "pdf" or "jpg" ("jpeg" is accepted as "jpg"),
    /// any case. A missing value is "pdf", so callers written before JPG support get the same result.
    /// </summary>
    public static bool TryNormalize(string? value, out string format)
    {
        format = (value ?? Pdf).Trim().ToLowerInvariant() switch
        {
            "jpeg" => Jpg,
            var other => other,
        };
        return format is Pdf or Jpg;
    }

    public static string ContentTypeFor(string format) =>
        format == Jpg ? "image/jpeg" : "application/pdf";

    /// <summary>Download file name: "scan.pdf", or "scan-2.jpg" for the second JPEG page.</summary>
    public static string FileNameFor(string format, int page) =>
        format == Jpg ? (page == 1 ? "scan.jpg" : $"scan-{page}.jpg") : "scan.pdf";
}

public sealed class ScanJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ScanJobStatus Status { get; internal set; } = ScanJobStatus.Pending;

    public int PagesScanned { get; internal set; }

    public string? ErrorCode { get; internal set; }

    /// <summary>Why the job failed or was canceled, rendered in each reader's language.</summary>
    public LocalizedMessage? ErrorMessage { get; internal set; }

    /// <summary>"pdf" or "jpg", as requested.</summary>
    public string Format { get; internal set; } = ScanFormats.Pdf;

    /// <summary>
    /// The finished result: a single PDF with every page, or one JPEG per page. Null until
    /// the job completes.
    /// </summary>
    public IReadOnlyList<byte[]>? Files { get; internal set; }

    public string ContentType => ScanFormats.ContentTypeFor(Format);

    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedUtc { get; internal set; }

    internal CancellationTokenSource Cts { get; } = new();

    public bool IsTerminal => Status is ScanJobStatus.Completed or ScanJobStatus.Failed or ScanJobStatus.Canceled;
}
