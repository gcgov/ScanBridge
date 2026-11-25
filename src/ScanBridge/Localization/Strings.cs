using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace ScanBridge.Localization;

/// <summary>
/// Every user-visible text of ScanBridge. Each member name is its key in
/// <c>Strings.resx</c> (English, the neutral language) and in each translation,
/// <c>Strings.{culture}.resx</c>. A lookup uses the UI culture of the calling thread and
/// falls back to English when a translation is missing.
/// </summary>
/// <remarks>
/// Log messages and API error codes are not here: they stay in English so that support
/// staff and client code can rely on them whatever the user's language.
/// </remarks>
public static class Strings
{
    public static ResourceManager ResourceManager { get; } =
        new("ScanBridge.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The text for <paramref name="key"/>, or the key itself if no resource has it.</summary>
    public static string Get(string key, CultureInfo? culture = null) =>
        ResourceManager.GetString(key, culture ?? CultureInfo.CurrentUICulture) ?? key;

    /// <summary>
    /// The text for <paramref name="key"/> with its placeholders filled in. A null
    /// <paramref name="culture"/> uses the UI culture for the text and the current culture
    /// for numbers in the arguments.
    /// </summary>
    public static string Format(string key, CultureInfo? culture, params object?[] args) =>
        string.Format(culture ?? CultureInfo.CurrentCulture, Get(key, culture), args);

    private static string S([CallerMemberName] string key = "") => Get(key);

    private static string F(object?[] args, [CallerMemberName] string key = "") => Format(key, null, args);

    // ---- Shared ----
    public static string NotListening => S();
    public static string OpenLogFolder => S();

    // ---- Tray ----
    public static string TrayMenuSettings => S();
    public static string TrayMenuTestScan => S();
    public static string TrayMenuExit => S();
    public static string TrayStatusServerError => S();
    public static string TrayStatusScanning => S();
    public static string TrayStatusReady(int port) => F([port]);
    public static string TrayStatusLine(string version, string status) => F([version, status]);
    public static string TrayNoScannerConfigured => S();
    public static string TrayTestScanStarted => S();
    public static string TrayTestScanFailed(string detail) => F([detail]);
    public static string TrayScanCompleted => S();
    public static string TrayScanFailedTitle => S();

    // ---- Settings window ----
    public static string SettingsTitle(string version) => F([version]);
    public static string SettingsScannerGroup => S();
    public static string SettingsDriverLabel => S();
    public static string SettingsScannerLabel => S();
    public static string SettingsRefresh => S();
    public static string SettingsLookingForScanners => S();
    public static string SettingsNoScannersFound => S();
    public static string SettingsScannersFound(int count) => F([count]);
    public static string SettingsScannerListFailed(string detail) => F([detail]);
    public static string SettingsDefaultsGroup => S();
    public static string SettingsSourceLabel => S();
    public static string SettingsResolutionLabel => S();
    public static string SettingsColorLabel => S();
    public static string SettingsPageSizeLabel => S();
    public static string SourceFlatbed => S();
    public static string SourceFeeder => S();
    public static string SourceDuplex => S();
    public static string ColorColor => S();
    public static string ColorGrayscale => S();
    public static string ColorBlackAndWhite => S();
    public static string SettingsRemoveBlankPages => S();
    public static string SettingsDeskew => S();
    public static string SettingsJpegQualityLabel => S();
    public static string SettingsJpegQualityHelp => S();
    public static string SettingsServerGroup => S();
    public static string SettingsPortLabel => S();
    public static string SettingsListening(int port) => F([port]);
    public static string SettingsOriginsLabel => S();
    public static string SettingsAddOrigin => S();
    public static string SettingsRemoveOrigin => S();
    public static string SettingsInvalidOriginTitle => S();
    public static string SettingsInvalidOriginMessage => S();
    public static string SettingsRunAtLogin => S();
    public static string SettingsLanguageLabel => S();
    public static string SettingsLanguageAutomatic => S();
    public static string SettingsCopyDiagnostics => S();
    public static string SettingsDiagnosticsCopied => S();
    public static string SettingsClipboardUnavailable => S();
    public static string SettingsSave => S();
    public static string SettingsCancel => S();

    // ---- Local server and API errors ----
    public static string ServerPortInUse(int port) => F([port]);
    public static string ServerStartFailed(string detail) => F([detail]);
    public static string ApiInvalidHost => S();
    public static string ApiOriginNotAllowed(string origin) => F([origin]);
    public static string ApiInvalidFormat => S();
    public static string ApiInvalidJpegQuality => S();
    public static string ApiScanNotFound => S();
    public static string ApiScanNotFinished => S();
    public static string ApiScanDidNotComplete => S();
    public static string ApiPageNotFound(int page, int pageCount) => F([page, pageCount]);

    // ---- Scan errors (also used as LocalizedMessage keys) ----
    public static string ScanNoScannerConfigured => S();
    public static string ScanScannerBusy => S();
    public static string ScanDeviceOffline(string detail) => F([detail]);
    public static string ScanNoPages => S();
    public static string ScanCanceled => S();
    public static string ScanFailed(string detail) => F([detail]);
}
