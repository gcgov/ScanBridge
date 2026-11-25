using System.Globalization;

namespace ScanBridge.Localization;

/// <summary>
/// A user-visible message kept as a resource key plus its arguments, not as finished text.
/// The scan job, the tray and the HTTP API each render it in their own language: the
/// tray in the language of the desktop, the API in the language the caller asked for.
/// </summary>
public sealed class LocalizedMessage
{
    private readonly object?[] _args;

    /// <param name="key">A member name of <see cref="Strings"/>, passed with <c>nameof</c>.</param>
    /// <param name="args">Values for the placeholders of the resource text.</param>
    public LocalizedMessage(string key, params object?[] args)
    {
        Key = key;
        _args = args;
    }

    public string Key { get; }

    public IReadOnlyList<object?> Args => _args;

    /// <summary>The message in <paramref name="culture"/>, or in English if there is no translation.</summary>
    public string ToString(CultureInfo culture) => Strings.Format(Key, culture, _args);

    /// <summary>The message in English, the text that goes to the log file.</summary>
    public string ToLogString() => ToString(CultureInfo.InvariantCulture);

    /// <summary>The message in the current UI culture of the calling thread.</summary>
    public override string ToString() => Strings.Format(Key, culture: null, _args);
}
