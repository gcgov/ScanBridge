using System.Globalization;

namespace ScanBridge.Localization;

/// <summary>A language ScanBridge has a translation for.</summary>
/// <param name="Name">Culture name, as in <c>Strings.{Name}.resx</c>.</param>
/// <param name="NativeName">The name of the language in that language, for the language picker.</param>
public sealed record SupportedLanguage(string Name, string NativeName);

/// <summary>
/// Chooses the language of the desktop UI. English is the neutral language: it is the
/// fallback for a missing translation and for a Windows language with no translation.
/// </summary>
/// <remarks>
/// To add a language, add <c>Localization/Strings.{name}.resx</c>, add the language to
/// <see cref="Supported"/>, add it to <c>SatelliteResourceLanguages</c> in the project
/// file, and add it to the <c>[Languages]</c> section of the installer script.
/// </remarks>
public static class UiLanguage
{
    /// <summary>Settings value that means "use the Windows display language".</summary>
    public const string Automatic = "auto";

    /// <summary>The neutral language, built into the main assembly.</summary>
    public const string English = "en";

    /// <summary>Every language with a translation. English comes first.</summary>
    public static IReadOnlyList<SupportedLanguage> Supported { get; } =
    [
        new(English, "English"),
        new("it", "Italiano"),
    ];

    /// <summary>The Windows display language, captured before ScanBridge changes the UI culture.</summary>
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The culture for a language setting. <see cref="Automatic"/>, an empty value or an
    /// unknown value follows <paramref name="system"/> when ScanBridge has its language,
    /// and English when it does not.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo system)
    {
        if (!string.IsNullOrWhiteSpace(setting) && FindSupported(setting) is { } chosen)
        {
            return CultureInfo.GetCultureInfo(chosen.Name);
        }

        // Keep the full Windows culture (for example it-CH) when its language is supported.
        return FindSupported(system.Name) is not null ? system : CultureInfo.GetCultureInfo(English);
    }

    /// <summary>
    /// Sets the process-wide default UI culture from a language setting. Every thread
    /// without a culture of its own, the UI thread included, follows it at once. An HTTP
    /// request sets its own culture from Accept-Language and is not affected. Formats for
    /// dates and numbers keep following the Windows regional settings.
    /// </summary>
    /// <remarks>
    /// Only the default is set, not <see cref="CultureInfo.CurrentUICulture"/>: a value set
    /// on a thread is undone when the execution context that set it ends, so a language
    /// change saved from an async handler would not last.
    /// </remarks>
    public static CultureInfo Apply(string? setting)
    {
        var culture = Resolve(setting, SystemUiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return culture;
    }

    /// <summary>The supported language for a culture name such as "it", "it-IT" or "IT".</summary>
    public static SupportedLanguage? FindSupported(string cultureName)
    {
        var language = cultureName.Split('-', 2)[0];
        return Supported.FirstOrDefault(l => string.Equals(l.Name, language, StringComparison.OrdinalIgnoreCase));
    }
}
