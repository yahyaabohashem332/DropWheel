using System.Text.RegularExpressions;
using System.Windows;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.Core.Actions;
using Petal.Core.Tools;

namespace Petal.Tests;

/// <summary>
/// Petal's other languages: every string Arabic has, each with its placeholders and counted forms, every
/// wheel label fitting its petal, and the interface turning to the right direction and grammar.
/// </summary>
[Collection("Language")]
public sealed partial class LanguageTests
{
    public static TheoryData<string> Codes() => [.. L.Languages.Select(l => l.Code).Where(c => c is not ("en" or "ar"))];

    private static IReadOnlyDictionary<string, string> TableOf(string code) =>
        L.Table(code) ?? throw new InvalidOperationException($"No table for {code}");

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_string_has_a_translation(string code)
    {
        var table = TableOf(code);
        var missing = L.ArabicTable.Keys.Where(k => !table.TryGetValue(k, out var t) || string.IsNullOrWhiteSpace(t)).ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} missing, such as: " + string.Join(" | ", missing.Take(15)));
        var extra = table.Keys.Where(k => !L.ArabicTable.ContainsKey(k)).ToList();
        Assert.True(extra.Count == 0, $"{code}: keys the interface doesn't have: " + string.Join(" | ", extra.Take(15)));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Translations_keep_their_placeholders_and_counted_forms(string code)
    {
        var wrong = new List<string>();
        foreach (var (english, translated) in TableOf(code))
        {
            var forms = translated.Split('|');
            bool counted = L.ArabicTable.TryGetValue(english, out var arabic) && arabic.Contains('|');
            if (!counted && forms.Length > 1)
                wrong.Add($"{english} → {translated} (only counted phrases have forms)");
            if (forms.Length > 2)
                wrong.Add($"{english} → {translated} (one or two forms)");
            foreach (var form in forms)
            {
                var want = Placeholder().Matches(english).Select(m => m.Value).Order();
                var have = Placeholder().Matches(form).Select(m => m.Value).Order();
                if (!want.SequenceEqual(have))
                    wrong.Add($"{english} → {form}");
            }
        }
        Assert.True(wrong.Count == 0, $"{code}:\n" + string.Join("\n", wrong.Take(30)));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Tool_and_action_names_read_in_the_language(string code)
    {
        var table = TableOf(code);
        var names = ToolRegistry.All.SelectMany(t => new[] { t.Name, t.WheelTitle })
            .Concat(FileActionRegistry.All.SelectMany(a => new[] { a.Name, a.WheelTitle, a.Description }))
            .Distinct().Where(n => !table.ContainsKey(n)).ToList();
        Assert.Empty(names);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_wheel_label_fits_its_petal(string code)
    {
        bool rtl = L.Languages.First(l => l.Code == code).RightToLeft;
        var table = TableOf(code);
        var font = new InkFont(UiFonts.Display, FontWeights.SemiBold, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, 1);
        var titles = ToolRegistry.All.Select(t => t.WheelTitle)
            .Concat(FileActionRegistry.All.Select(a => a.WheelTitle))
            .Concat(Petal.App.Wheels.WheelActions.All.Select(a => a.Label))
            .Concat(["PAUSE", "MORE"])
            .Concat(ReadyMadeLabels())
            .Distinct()
            .Select(t => table.TryGetValue(t, out var x) ? x.Split('|')[^1] : t)
            .ToList();
        var outline = Icons.InkGeometry("share", 16);
        var icon = new IconInk(outline.Bounds, InkShape.Strips(outline, 3));
        foreach (int count in new[] { 8, 12 })
        {
            // As the wheel sets them for the language's script.
            double size = rtl ? (count > 8 ? 10 : 10.5) : count > 8 ? 8 : 8.5;
            double step = rtl ? 13 : 9.5;
            var over = new List<string>();
            foreach (var title in titles)
            {
                for (int i = 0; i < count; i++)
                {
                    var label = PetalLayout.Fit(title, font, size, size - 1.5, step, icon, 16, i, count);
                    if (label.Clearance < -0.5)
                        over.Add($"{title} at {i} of {count}: {label.Clearance:0.0}");
                }
            }
            var titlesOver = over.Select(o => o[..o.IndexOf(" at ", StringComparison.Ordinal)]).Distinct().ToList();
            Assert.True(over.Count == 0, $"{code}: too long for a petal at {count}: " + string.Join("; ", titlesOver));
        }
    }

    /// <summary>The labels of the ready-made wheels and of the gallery's.</summary>
    private static IEnumerable<string> ReadyMadeLabels()
    {
        var settings = new Petal.App.Settings.AppSettings();
        Petal.App.Wheels.WheelTemplates.Upgrade(settings);
        var wheels = settings.Wheels.Concat(Petal.App.Wheels.WheelGallery.All.Select(g => g.Make()));
        return Flatten(wheels.SelectMany(w => w.Items)).Select(e => e.Label).OfType<string>();

        static IEnumerable<Petal.App.Wheels.WheelEntry> Flatten(IEnumerable<Petal.App.Wheels.WheelEntry> entries) =>
            entries.SelectMany(e => e.Children is { } children ? Flatten(children).Prepend(e) : [e]);
    }

    [Fact]
    public void Every_ready_made_wheel_label_is_translated()
    {
        var labels = ReadyMadeLabels().Where(l => l.Any(char.IsLetter) && l.ToUpperInvariant() == l).Distinct().ToList();
        foreach (var (_, code, _, _) in L.Languages.Where(l => l.Code != "en"))
        {
            var missing = labels.Where(l => !TableOf(code).ContainsKey(l) && l is not ("VS CODE" or "QR")).ToList();
            Assert.True(missing.Count == 0, $"{code}: " + string.Join(", ", missing));
        }
    }

    [Theory]
    [InlineData("tr", 3, "3")]
    [InlineData("fr", 0, "0")]
    [InlineData("es", 1, "1")]
    public void Counted_phrases_take_the_languages_grammar(string code, int count, string number)
    {
        L.Configure(L.FromCode(code));
        try
        {
            var text = L.P(count, "{0} file", "{0} files");
            Assert.Contains(number, text);
            Assert.DoesNotContain("file", text);
            Assert.Equal(L.FromCode(code) is UiLanguage.Persian or UiLanguage.Urdu or UiLanguage.Arabic, L.IsRightToLeft);
        }
        finally
        {
            L.Configure(UiLanguage.English);
        }
    }

    [Fact]
    public void Persian_and_Urdu_turn_the_interface_right_to_left()
    {
        foreach (var language in new[] { UiLanguage.Persian, UiLanguage.Urdu, UiLanguage.Arabic })
        {
            L.Configure(language);
            Assert.Equal(FlowDirection.RightToLeft, L.Direction);
        }
        L.Configure(UiLanguage.French);
        Assert.Equal(FlowDirection.LeftToRight, L.Direction);
        Assert.False(L.IsArabic);
        L.Configure(UiLanguage.English);
    }

    [GeneratedRegex(@"\{[a-z0-9]+\}")]
    private static partial Regex Placeholder();
}
