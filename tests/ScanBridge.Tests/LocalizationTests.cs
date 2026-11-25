using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ScanBridge.Localization;
using Xunit;

namespace ScanBridge.Tests;

/// <summary>
/// Keeps <see cref="Strings"/>, Strings.resx and every translation in step, so a new text
/// cannot ship without its English resource or with a translation that breaks its placeholders.
/// </summary>
public partial class LocalizationTests
{
    private static readonly string[] HelperMembers = [nameof(Strings.Get), nameof(Strings.Format), nameof(Strings.ResourceManager)];

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var language in UiLanguage.Supported.Where(l => l.Name != UiLanguage.English))
        {
            data.Add(language.Name);
        }

        return data;
    }

    private static Dictionary<string, string> ReadResources(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static Dictionary<string, string> Neutral() => ReadResources(CultureInfo.InvariantCulture);

    private static IEnumerable<MemberInfo> StringMembers() =>
        typeof(Strings)
            .GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m is PropertyInfo or MethodInfo { IsSpecialName: false })
            .Where(m => !HelperMembers.Contains(m.Name));

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    [Fact]
    public void EveryStringsMemberHasAnEnglishResource()
    {
        var neutral = Neutral();

        var missing = StringMembers().Select(m => m.Name).Where(name => !neutral.ContainsKey(name)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryEnglishResourceHasAStringsMember()
    {
        var members = StringMembers().Select(m => m.Name).ToHashSet();

        var unused = Neutral().Keys.Where(key => !members.Contains(key)).ToList();

        Assert.Empty(unused);
    }

    [Fact]
    public void PlaceholderCountMatchesTheMethodParameters()
    {
        var neutral = Neutral();
        foreach (var member in StringMembers())
        {
            var expected = member is MethodInfo method ? method.GetParameters().Length : 0;
            Assert.True(Placeholders(neutral[member.Name]).Count() == expected,
                $"{member.Name}: the English text has a different number of placeholders than the member has parameters");
        }
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationHasEveryKeyAndNoOthers(string language)
    {
        var neutral = Neutral();
        var translated = ReadResources(CultureInfo.GetCultureInfo(language));

        Assert.Empty(neutral.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(neutral.Keys));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationKeepsThePlaceholders(string language)
    {
        var neutral = Neutral();
        var translated = ReadResources(CultureInfo.GetCultureInfo(language));

        foreach (var (key, text) in translated)
        {
            Assert.True(Placeholders(neutral[key]).SequenceEqual(Placeholders(text)),
                $"{language}/{key}: placeholders differ from the English text");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("it")]
    [InlineData("de")]
    public void EveryMemberRendersInEveryCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var member in StringMembers())
        {
            var args = member is MethodInfo method
                ? method.GetParameters().Select(p => p.ParameterType == typeof(int) ? (object)7 : "x").ToArray()
                : [];

            var text = Strings.Format(member.Name, culture, args);

            Assert.False(string.IsNullOrWhiteSpace(text), member.Name);
        }
    }

    [Fact]
    public void MissingTranslationFallsBackToEnglish()
    {
        var english = Strings.Get(nameof(Strings.ScanCanceled), CultureInfo.InvariantCulture);

        Assert.Equal(english, Strings.Get(nameof(Strings.ScanCanceled), CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void LocalizedMessageRendersInTheAskedCulture()
    {
        var message = new LocalizedMessage(nameof(Strings.ScanFailed), "jam");

        Assert.Equal("The scan failed: jam", message.ToLogString());
        Assert.Equal("La scansione non è riuscita: jam", message.ToString(CultureInfo.GetCultureInfo("it-IT")));
    }

    [Theory]
    [InlineData("auto", "it-IT", "it-IT")]
    [InlineData("auto", "it-CH", "it-CH")]
    [InlineData("auto", "en-US", "en-US")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData(null, "it-IT", "it-IT")]
    [InlineData("", "fr-FR", "en")]
    [InlineData("en", "it-IT", "en")]
    [InlineData("it", "en-US", "it")]
    [InlineData("IT", "en-US", "it")]
    [InlineData("xx", "it-IT", "it-IT")]
    [InlineData("xx", "de-DE", "en")]
    public void LanguageSettingResolvesToACulture(string? setting, string windows, string expected)
    {
        var culture = UiLanguage.Resolve(setting, CultureInfo.GetCultureInfo(windows));

        Assert.Equal(expected, culture.Name);
    }
}
