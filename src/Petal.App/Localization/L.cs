using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Petal.App.Localization;

public enum UiLanguage
{
    /// <summary>Follow Windows' display language.</summary>
    System,
    English,
    Arabic,
}

/// <summary>
/// Petal's interface language. Strings are written in English in the code and looked up in the
/// Arabic table at display time; anything without a translation stays English. Looking up a
/// string that is already translated returns it unchanged, so translating twice is harmless.
/// </summary>
public static class L
{
    private static string _code = Resolve(UiLanguage.System);

    public static event Action? Changed;

    /// <summary>"en" or "ar".</summary>
    public static string Code => _code;

    public static bool IsArabic => _code == "ar";

    public static FlowDirection Direction => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static void Configure(UiLanguage preference)
    {
        var code = Resolve(preference);
        if (code == _code)
            return;
        _code = code;
        Changed?.Invoke();
    }

    public static void Initialize(UiLanguage preference) => _code = Resolve(preference);

    /// <summary>
    /// Gives a window or popup root the interface direction, and keeps digits Western
    /// (0–9) everywhere so numbers look the same next to Arabic and Latin text.
    /// </summary>
    public static void Apply(FrameworkElement root, bool mirror = true)
    {
        if (mirror)
            root.FlowDirection = Direction;
        NumberSubstitution.SetCultureSource(root, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(root, CultureInfo.GetCultureInfo("en-US"));
        NumberSubstitution.SetSubstitution(root, NumberSubstitutionMethod.European);
    }

    private static string Resolve(UiLanguage preference) => preference switch
    {
        UiLanguage.Arabic => "ar",
        UiLanguage.English => "en",
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ar" || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en",
    };

    /// <summary>Translates an English interface string.</summary>
    public static string T(string english)
    {
        if (!IsArabic || string.IsNullOrEmpty(english))
            return english;
        if (Arabic.Table.TryGetValue(english, out var arabic))
            return First(arabic);
        // Labels padded with spaces for spacing next to an icon keep their padding.
        var trimmed = english.Trim();
        if (trimmed.Length != english.Length && Arabic.Table.TryGetValue(trimmed, out arabic))
            return english.Replace(trimmed, First(arabic));
        return english;
    }

    /// <summary>
    /// Translates a word whose meaning depends on where it appears, e.g. T("Light", "photo") for
    /// the Edit Photo section, which Arabic words differently from the "Light" theme. The table
    /// key is "photo:Light"; without one, the plain translation is used.
    /// </summary>
    public static string T(string english, string context) =>
        IsArabic && Arabic.Table.TryGetValue(context + ":" + english, out var arabic) ? arabic : T(english);

    /// <summary>The Arabic table, for the tests that check it is complete and well formed.</summary>
    public static IReadOnlyDictionary<string, string> ArabicTable => Arabic.Table;

    /// <summary>True when the string has an Arabic translation (used by the completeness test).</summary>
    public static bool HasTranslation(string english) => Arabic.Table.ContainsKey(english.Trim());

    /// <summary>Translates a format string, then fills it in: F("Convert to {0}", "PNG").</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>
    /// Counted phrase with the right grammar: English one/many; Arabic uses its own forms for
    /// 1, 2, 3–10 and 11+ ("ملف واحد", "ملفان", "3 ملفات", "11 ملفًا"), written in the table as
    /// "one|two|few|many" with {0} for the number.
    /// </summary>
    public static string P(int count, string englishOne, string englishMany)
    {
        if (!IsArabic)
            return string.Format(CultureInfo.CurrentCulture, count == 1 ? englishOne : englishMany, count);
        if (!Arabic.Table.TryGetValue(englishMany, out var forms))
            return string.Format(CultureInfo.CurrentCulture, count == 1 ? englishOne : englishMany, count);
        var parts = forms.Split('|');
        if (parts.Length < 4)
            return string.Format(CultureInfo.CurrentCulture, parts[0], count);
        int mod = count % 100;
        // Arabic: 1 and 2 have their own words, 3–10 take the plural, 11–99 the singular
        // accusative, and 0 / 100 / 101… the singular genitive (fifth form, when given).
        string form = count == 1 ? parts[0]
            : count == 2 ? parts[1]
            : mod is >= 3 and <= 10 ? parts[2]
            : mod is >= 11 and <= 99 ? parts[3]
            : parts.Length > 4 ? parts[4] : parts[3];
        return string.Format(CultureInfo.CurrentCulture, form, count);
    }

    private static string First(string value)
    {
        // Plural entries looked up with T (not P) use their "many" form.
        int bar = value.IndexOf('|');
        return bar < 0 ? value : value.Split('|')[^1];
    }
}
