using ScanBridge.Scanning;

namespace ScanBridge.Settings;

/// <summary>
/// User-editable application settings, persisted as JSON in %AppData%\ScanBridge\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultPort = 7226;

    /// <summary>TCP port the local HTTP listener binds on 127.0.0.1.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Exact web origins (scheme://host[:port]) allowed to call the API via CORS.
    /// Empty means no browser origin is allowed (same-machine tools still work).
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>Scan driver: "wia", "twain" or "escl". Null falls back to WIA.</summary>
    public string? ScannerDriver { get; set; }

    /// <summary>Device id of the selected scanner, as reported by the driver.</summary>
    public string? ScannerDeviceId { get; set; }

    /// <summary>Display name of the selected scanner (shown in status responses and the UI).</summary>
    public string? ScannerDeviceName { get; set; }

    public ScanDefaults Defaults { get; set; } = new();

    public bool RunAtLogin { get; set; } = true;

    public AppSettings Clone() => new()
    {
        Port = Port,
        AllowedOrigins = new List<string>(AllowedOrigins),
        ScannerDriver = ScannerDriver,
        ScannerDeviceId = ScannerDeviceId,
        ScannerDeviceName = ScannerDeviceName,
        Defaults = Defaults.Clone(),
        RunAtLogin = RunAtLogin,
    };

    /// <summary>
    /// Validates and canonicalizes a web origin: http/https, no path/query/fragment,
    /// no credentials; lowercased scheme + host; default ports dropped.
    /// </summary>
    public static bool TryNormalizeOrigin(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        input = input.Trim().TrimEnd('/');
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // GetLeftPart(Authority) lowercases scheme/host and omits default ports.
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

public sealed class ScanDefaults
{
    /// <summary>"flatbed", "feeder" or "duplex".</summary>
    public string PaperSource { get; set; } = "feeder";

    public int Dpi { get; set; } = 300;

    /// <summary>"color", "grayscale" or "blackAndWhite".</summary>
    public string ColorMode { get; set; } = "color";

    /// <summary>"letter", "legal" or "a4".</summary>
    public string PageSize { get; set; } = "a4";

    /// <summary>Drop pages the scanner returns that are (nearly) blank — useful for duplex.</summary>
    public bool ExcludeBlankPages { get; set; }

    public bool AutoDeskew { get; set; }

    /// <summary>
    /// Output format used when a request does not specify one: "pdf" or "jpg"
    /// (jpg returns only the first scanned image).
    /// </summary>
    public string Format { get; set; } = "pdf";

    /// <summary>JPEG quality, 1-100, used when the output format is jpg.</summary>
    public int JpegQuality { get; set; } = ScanFormats.DefaultJpegQuality;

    public ScanDefaults Clone() => (ScanDefaults)MemberwiseClone();
}
