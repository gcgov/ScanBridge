using System.Globalization;
using ScanBridge.Localization;
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

    /// <summary>
    /// Language of the tray and the settings window: "auto" (follow the Windows display
    /// language) or a culture name from <see cref="UiLanguage.Supported"/>, such as "it".
    /// </summary>
    public string Language { get; set; } = UiLanguage.Automatic;

    public AppSettings Clone() => new()
    {
        Port = Port,
        AllowedOrigins = new List<string>(AllowedOrigins),
        ScannerDriver = ScannerDriver,
        ScannerDeviceId = ScannerDeviceId,
        ScannerDeviceName = ScannerDeviceName,
        Defaults = Defaults.Clone(),
        RunAtLogin = RunAtLogin,
        Language = Language,
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

    /// <summary>
    /// "letter", "legal" or "a4". A new install picks the paper size of the Windows region;
    /// a saved settings file keeps its value.
    /// </summary>
    public string PageSize { get; set; } = DefaultPageSizeFor(RegionInfo.CurrentRegion);

    /// <summary>Drop pages the scanner returns that are (nearly) blank — useful for duplex.</summary>
    public bool ExcludeBlankPages { get; set; }

    public bool AutoDeskew { get; set; }

    /// <summary>
    /// JPEG quality, 1-100, for a request that asks for "jpg" without a "jpegQuality".
    /// There is deliberately no default output format: a request without "format" always
    /// gets a PDF, so a page written for the PDF-only API never receives a JPEG.
    /// </summary>
    public int JpegQuality { get; set; } = ScanFormats.DefaultJpegQuality;

    public ScanDefaults Clone() => (ScanDefaults)MemberwiseClone();

    /// <summary>Regions where Letter, not A4, is the usual paper size.</summary>
    private static readonly HashSet<string> LetterRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "CA", "MX", "PR", "PH", "CL", "CO", "VE", "CR", "GT", "PA", "DO", "SV", "NI", "HN",
    };

    /// <summary>"letter" for North America and the other Letter regions, "a4" everywhere else.</summary>
    public static string DefaultPageSizeFor(RegionInfo region) =>
        LetterRegions.Contains(region.TwoLetterISORegionName) ? "letter" : "a4";
}
