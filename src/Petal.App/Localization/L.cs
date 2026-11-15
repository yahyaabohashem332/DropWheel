using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Petal.App.Localization;

/// <summary>The interface languages. New ones are added at the end: settings store them by number.</summary>
public enum UiLanguage
{
    /// <summary>Follow Windows' display language.</summary>
    System,
    English,
    Arabic,
    Turkish,
    Persian,
    Urdu,
    Indonesian,
    French,
    Spanish,
}

/// <summary>
/// Petal's interface language. Strings are written in English in the code and looked up in the
/// language's table at display time; anything without a translation stays English. Looking up a
/// string that is already translated returns it unchanged, so translating twice is harmless. Arabic's
/// table is in the code (<see cref="Arabic"/>); the others are JSON files built into Petal, read when
/// chosen.
/// </summary>
public static class L
{
    /// <summary>Every language Petal speaks: its code, its own name, and whether it is written right to left.</summary>
    public static readonly IReadOnlyList<(UiLanguage Language, string Code, string Name, bool RightToLeft)> Languages =
    [
        (UiLanguage.English, "en", "English", false),
        (UiLanguage.Arabic, "ar", "العربية", true),
        (UiLanguage.Turkish, "tr", "Türkçe", false),
        (UiLanguage.Persian, "fa", "فارسی", true),
        (UiLanguage.Urdu, "ur", "اردو", true),
        (UiLanguage.Indonesian, "id", "Bahasa Indonesia", false),
        (UiLanguage.French, "fr", "Français", false),
        (UiLanguage.Spanish, "es", "Español", false),
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Loaded = new(StringComparer.Ordinal);
    // The language and its table, swapped together so a count made during a switch never mixes the two.
    private sealed record State(string Code, IReadOnlyDictionary<string, string>? Table);
    private static volatile State _state = new("en", null);

    private static string _code => _state.Code;

    static L() => Use(Resolve(UiLanguage.System));

    public static event Action? Changed;

    /// <summary>"en", "ar", "tr", "fa", "ur", "id", "fr" or "es".</summary>
    public static string Code => _code;

    /// <summary>The language's name in English ("Persian"), for what the AI model is told.</summary>
    public static string EnglishName => _code switch
    {
        "ar" => "Arabic",
        "tr" => "Turkish",
        "fa" => "Persian",
        "ur" => "Urdu",
        "id" => "Indonesian",
        "fr" => "French",
        "es" => "Spanish",
        _ => "English",
    };

    /// <summary>Arabic itself: for what Petal writes in Arabic (names of files, translations), not for layout.</summary>
    public static bool IsArabic => _code == "ar";

    /// <summary>Arabic, Persian and Urdu: written right to left, in the Arabic script.</summary>
    public static bool IsRightToLeft => _code is "ar" or "fa" or "ur";

    public static FlowDirection Direction => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>The culture dates and month names are written in.</summary>
    public static CultureInfo Culture => _code switch
    {
        "en" => CultureInfo.GetCultureInfo("en-GB"),
        "ar" => CultureInfo.GetCultureInfo("ar-EG"),
        "fa" => CultureInfo.GetCultureInfo("fa-IR"),
        "ur" => CultureInfo.GetCultureInfo("ur-PK"),
        _ => CultureInfo.GetCultureInfo(_code),
    };

    public static void Configure(UiLanguage preference)
    {
        var code = Resolve(preference);
        if (code == _code)
            return;
        Use(code);
        Changed?.Invoke();
    }

    public static void Initialize(UiLanguage preference) => Use(Resolve(preference));

    /// <summary>The language for a code ("tr"), or English for one Petal doesn't speak.</summary>
    public static UiLanguage FromCode(string code) =>
        Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Language is var found && found != UiLanguage.System ? found : UiLanguage.English;

    private static void Use(string code)
    {
        _state = new State(code, Table(code));
    }

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

    private static string Resolve(UiLanguage preference)
    {
        if (preference != UiLanguage.System)
            return Languages.FirstOrDefault(l => l.Language == preference).Code ?? "en";
        // Windows' display language when Petal speaks it, English otherwise.
        foreach (var culture in new[] { CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture })
        {
            var two = culture.TwoLetterISOLanguageName;
            if (Languages.Any(l => l.Code == two))
                return two;
        }
        return "en";
    }

    /// <summary>A language's table: Arabic's from the code, the others from their JSON file; English has none.</summary>
    internal static IReadOnlyDictionary<string, string>? Table(string code)
    {
        if (code == "en")
            return null;
        if (code == "ar")
            return Arabic.Table;
        lock (Loaded)
        {
            if (Loaded.TryGetValue(code, out var table))
                return table;
            table = Load(code);
            Loaded[code] = table;
            return table;
        }
    }

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Petal.App.Localization.Languages.{code}.json");
            if (stream is null)
                return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) is { } table
                ? new Dictionary<string, string>(table, StringComparer.Ordinal)
                : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Info($"The {code} translation couldn't be read: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Translates an English interface string.</summary>
    public static string T(string english)
    {
        if (_state.Table is not { } table || string.IsNullOrEmpty(english))
            return english;
        if (table.TryGetValue(english, out var translated))
            return Last(translated);
        // Labels padded with spaces for spacing next to an icon keep their padding.
        var trimmed = english.Trim();
        if (trimmed.Length != english.Length && table.TryGetValue(trimmed, out translated))
            return english.Replace(trimmed, Last(translated));
        return english;
    }

    /// <summary>
    /// Translates a word whose meaning depends on where it appears, e.g. T("Light", "photo") for
    /// the Edit Photo section, which Arabic words differently from the "Light" theme. The table
    /// key is "photo:Light"; without one, the plain translation is used.
    /// </summary>
    public static string T(string english, string context) =>
        _state.Table is { } table && table.TryGetValue(context + ":" + english, out var translated) ? translated : T(english);

    /// <summary>
    /// An English interface string in one of Petal's languages chosen for what is written, not the interface's: for
    /// a file DropWheel writes for others to read in their language (a study file).
    /// </summary>
    public static string In(string code, string english)
    {
        if (string.IsNullOrEmpty(english) || Table(code) is not { } table)
            return english;
        return table.TryGetValue(english, out var translated) ? Last(translated) : english;
    }

    /// <summary>A counted phrase (see <see cref="P(int, string, string)"/>) in one of Petal's languages chosen for what is written.</summary>
    public static string P(int count, string englishOne, string englishMany, string code)
    {
        if (Table(code) is not { } table || !table.TryGetValue(englishMany, out var forms))
            return string.Format(CultureInfo.InvariantCulture, count == 1 ? englishOne : englishMany, count);
        var parts = forms.Split('|');
        return string.Format(CultureInfo.InvariantCulture, code == "ar" ? ArabicForm(parts, count) : OtherForm(parts, count, code), count);
    }

    /// <summary>The Arabic table, for the tests that check it is complete and well formed.</summary>
    public static IReadOnlyDictionary<string, string> ArabicTable => Arabic.Table;

    /// <summary>True when the string has an Arabic translation (used by the completeness test).</summary>
    public static bool HasTranslation(string english) => Arabic.Table.ContainsKey(english.Trim());

    /// <summary>Translates a format string, then fills it in: F("Convert to {0}", "PNG").</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>
    /// Counted phrase with the right grammar. English: one/many. Arabic has its own forms for 1, 2, 3–10
    /// and 11+ ("ملف واحد", "ملفان", "3 ملفات", "11 ملفًا"), written in its table as "one|two|few|many|hundreds".
    /// The other languages write "one|other", or a single form when, like Turkish, they use one after every number.
    /// </summary>
    public static string P(int count, string englishOne, string englishMany)
    {
        var state = _state;
        if (state.Table is not { } table || !table.TryGetValue(englishMany, out var forms))
            return string.Format(CultureInfo.CurrentCulture, count == 1 ? englishOne : englishMany, count);
        var parts = forms.Split('|');
        return string.Format(CultureInfo.CurrentCulture, state.Code == "ar" ? ArabicForm(parts, count) : OtherForm(parts, count, state.Code), count);
    }

    private static string ArabicForm(string[] parts, int count)
    {
        if (parts.Length < 4)
            return parts[0];
        int mod = count % 100;
        // Arabic: 1 and 2 have their own words, 3–10 take the plural, 11–99 the singular
        // accusative, and 0 / 100 / 101… the singular genitive (fifth form, when given).
        return count == 1 ? parts[0]
            : count == 2 ? parts[1]
            : mod is >= 3 and <= 10 ? parts[2]
            : mod is >= 11 and <= 99 ? parts[3]
            : parts.Length > 4 ? parts[4] : parts[3];
    }

    private static string OtherForm(string[] parts, int count, string code)
    {
        if (parts.Length < 2)
            return parts[0];
        // French counts 0 and 1 as one; the rest only 1.
        bool one = code == "fr" ? count is 0 or 1 : count == 1;
        return one ? parts[0] : parts[^1];
    }

    private static string Last(string value)
    {
        // Counted entries looked up with T (not P) use their last, "many" form.
        int bar = value.IndexOf('|');
        return bar < 0 ? value : value.Split('|')[^1];
    }
}
