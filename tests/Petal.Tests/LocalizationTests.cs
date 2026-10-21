using System.Text.RegularExpressions;
using System.Windows;
using Petal.App.Localization;
using Petal.App.Util;
using Petal.Core.Actions;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.Tests;

/// <summary>The interface language is global, so these tests never run alongside others.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public class LanguageCollection;

[Collection("Language")]
public partial class LocalizationTests
{
    // Strings in the app's sources that are not interface text, or read the same in Arabic.
    private static readonly HashSet<string> NotTranslated = new(StringComparer.Ordinal)
    {
        "Petal", "Petal input hooks", "Preferred DropEffect", "Shell.Application", "System.Photo.Orientation", "PART_Track",
        "Segoe UI Variable Display, Segoe UI", "Segoe UI Variable Text, Segoe UI", "Cascadia Mono, Consolas",
        "Shift", "Ctrl", "Alt", "Win", "⇧ Shift", "Shift ⇧", "Windows ⊞", "English",
        "FFmpeg", "Microsoft Office", "4K (2160p)", "Full HD (1080p)", "HD (720p)", "SD (480p)",
        "ID3v2_3", "ID3v2_4", "ID3v1", "Vorbis",
        // Key names as printed on keycaps, and Windows class names.
        "Enter", "Backspace", "Progman", "SHELLDLL_DefView", "Petal shortcut",
        // Font names, a template part, a unit and a file-name suffix (file names stay in English).
        "Segoe UI", "Segoe Script", "Segoe Print", "Ink Free", "Cell", "pt", "PNG · SVG", "Unlocked",
        // Date and time patterns, and a sample installer name in the design snapshots.
        "HH:mm", "yyyy-MM-dd", "Petal-Setup-1.1.0.exe", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH.mm.ss",
        // A font, a Recycle Bin record pattern, a MIME type, a thread name and a voice maker's prefix.
        "Consolas", "$I*", "image/jpeg", "Petal search index", "Microsoft",
        // The culture the wheel measures its text in, as WPF text blocks do by default.
        "en-US",
        // Sample requests and search results in the design snapshots, already in both languages.
        // Photography abbreviations and ExifTool tag names, which read the same in Arabic.
        "f/", "ISO", "mm", "FNumber", "Software",
        "make them 1080 px WEBP and zip them", "صغّرها إلى 1080 بكسل وحوّلها إلى WEBP ثم ضعها في ملف مضغوط", "…our trip to the «beach» in July…",
    };

    private static readonly Regex Literal = MakeLiteral();
    private static readonly Regex SkipLine = MakeSkipLine();
    private static readonly Regex PluralOne = MakePluralOne();
    private static readonly Regex Placeholder = MakePlaceholder();

    [Fact]
    public void Every_interface_string_in_the_app_has_an_Arabic_translation()
    {
        var missing = new List<string>();
        int checkedStrings = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Petal.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}"))
                continue;
            var source = File.ReadAllText(file);
            // English singular forms of counted phrases are looked up through their plural.
            var singulars = PluralOne.Matches(source).Select(m => m.Groups[1].Value).ToHashSet();
            var lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//") || line.StartsWith("using ") || SkipLine.IsMatch(line))
                    continue;
                foreach (Match m in Literal.Matches(line))
                {
                    if (m.Groups["prefix"].Value.Contains('$'))
                        continue;
                    var text = m.Groups["body"].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
                    if (!LooksLikeInterfaceText(text) || singulars.Contains(text) || NotTranslated.Contains(text.Trim()))
                        continue;
                    checkedStrings++;
                    if (!L.HasTranslation(text))
                        missing.Add($"{Path.GetFileName(file)}:{i + 1}  \"{text}\"");
                }
            }
        }
        Assert.True(missing.Count == 0, "No Arabic for:\n" + string.Join("\n", missing));
        // Guards against a scan that silently finds nothing.
        Assert.True(checkedStrings > 300, $"Only {checkedStrings} strings were checked.");
    }

    [Fact]
    public void Tool_action_and_photo_slider_names_have_Arabic_translations()
    {
        var names = ToolRegistry.All.SelectMany(t => new[] { t.Name, t.WheelTitle })
            .Concat(FileActionRegistry.All.SelectMany(a => new[] { a.Name, a.WheelTitle, a.Description }))
            .Concat(PhotoAdjustments.Sliders.Select(s => s.Label))
            .Concat(PhotoAdjustments.Sliders.Select(s => s.Section).Where(s => s != "Light"));
        var missing = names.Distinct().Where(n => !L.HasTranslation(n)).ToList();
        Assert.Empty(missing);
        Assert.True(L.ArabicTable.ContainsKey("photo:Light"));
    }

    [Fact]
    public void Arabic_entries_keep_the_placeholders_of_their_English_key()
    {
        var wrong = new List<string>();
        foreach (var (english, arabic) in L.ArabicTable)
        {
            var expected = Placeholders(english);
            var forms = arabic.Split('|');
            if (forms.Length == 1)
            {
                if (!expected.SetEquals(Placeholders(arabic)))
                    wrong.Add(english);
            }
            else if (forms.Length != 5 || forms.Any(f => !Placeholders(f).IsSubsetOf(expected)) || !forms[2..].All(f => Placeholders(f).SetEquals(expected)))
            {
                // Five forms; "one" and "two" may spell the number out, the others show it.
                wrong.Add(english);
            }
        }
        Assert.True(wrong.Count == 0, "Placeholders differ for:\n" + string.Join("\n", wrong));
    }

    [Theory]
    [InlineData(1, "ملف واحد")]
    [InlineData(2, "ملفان")]
    [InlineData(3, "3 ملفات")]
    [InlineData(10, "10 ملفات")]
    [InlineData(11, "11 ملفًا")]
    [InlineData(99, "99 ملفًا")]
    [InlineData(100, "100 ملف")]
    [InlineData(103, "103 ملفات")]
    [InlineData(0, "0 ملف")]
    public void Arabic_counts_use_the_right_plural_form(int count, string expected) =>
        InArabic(() => Assert.Equal(expected, L.P(count, "{0} file", "{0} files")));

    [Fact]
    public void English_counts_use_one_and_many()
    {
        L.Configure(UiLanguage.English);
        Assert.Equal("1 file", L.P(1, "{0} file", "{0} files"));
        Assert.Equal("2 files", L.P(2, "{0} file", "{0} files"));
        Assert.Equal("Convert to PNG", L.F("Convert to {0}", "PNG"));
    }

    [Fact]
    public void Arabic_switches_direction_and_translates_on_the_fly()
    {
        InArabic(() =>
        {
            Assert.True(L.IsArabic);
            Assert.Equal(FlowDirection.RightToLeft, L.Direction);
            Assert.Equal("تحويل إلى PNG", L.F("Convert to {0}", "PNG"));
            // Padding around a label survives, untranslated text passes through, and translating twice is harmless.
            Assert.Equal("  تشغيل الإخراج", L.T("  Play Output"));
            Assert.Equal("weekend.mov", L.T("weekend.mov"));
            Assert.Equal(L.T("Settings"), L.T(L.T("Settings")));
            Assert.Equal("الإضاءة", L.T("Light", "photo"));
            Assert.Equal("فاتح", L.T("Light"));
            Assert.Equal("1.6 م.ب", Sizes.Format(1_600_000).Replace(',', '.'));
        });
        Assert.Equal(FlowDirection.LeftToRight, L.Direction);
    }

    [Fact]
    public void Language_change_is_announced_once()
    {
        L.Configure(UiLanguage.English);
        int raised = 0;
        void Count() => raised++;
        L.Changed += Count;
        try
        {
            L.Configure(UiLanguage.Arabic);
            L.Configure(UiLanguage.Arabic);
            L.Configure(UiLanguage.English);
        }
        finally
        {
            L.Changed -= Count;
            L.Configure(UiLanguage.English);
        }
        Assert.Equal(2, raised);
    }

    private static void InArabic(Action test)
    {
        L.Configure(UiLanguage.Arabic);
        try { test(); }
        finally { L.Configure(UiLanguage.English); }
    }

    private static HashSet<string> Placeholders(string s) => Placeholder.Matches(s).Select(m => m.Value).ToHashSet();

    /// <summary>The same rules the translation table was built from: words, not ids, icons, paths or formats.</summary>
    private static bool LooksLikeInterfaceText(string s)
    {
        if (!s.Any(char.IsAsciiLetter))
            return false;
        if (s.StartsWith("Petal.") || s.StartsWith("http") || s.StartsWith('.') || s.StartsWith('-') || s.StartsWith('/') || s.StartsWith('#') || s.StartsWith('{') || s.StartsWith("pack:"))
            return false;
        if (s.Contains('\\') || s.Contains("*.") || s.Contains("%1"))
            return false;
        return !Regex.IsMatch(s, @"^[a-z][A-Za-z0-9]*$")              // icon names, ids
            && !Regex.IsMatch(s, @"^[A-Z][a-z]+[A-Z][A-Za-z0-9]*$")    // PascalCase ids
            && !Regex.IsMatch(s, @"^[\w\- ]+\.[A-Za-z0-9]{2,4}$")      // file names
            && !Regex.IsMatch(s, @"^[0#.,:%PN \d{}\-+;]+$")            // number formats
            && !Regex.IsMatch(s, @"^[A-Z0-9_]{1,6}$")                  // format codes such as PNG
            && !Regex.IsMatch(s, @"^[a-z0-9_\-]+(:[a-z0-9_\-=]+)?$")   // filter names
            && !Regex.IsMatch(s, @"^[0-9A-F]{8}-[0-9A-F]{4}-")         // COM ids
            && !Regex.IsMatch(s, @"^M[\s\d]");                         // path data
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Petal's sources were not found above the test folder.");
    }

    [GeneratedRegex(@"(?<prefix>\$@|@\$|\$|@)?""(?<body>(?:[^""\\\n]|\\.)*)""")]
    private static partial Regex MakeLiteral();

    [GeneratedRegex(@"\bLog\.(Info|Error|Warn|Debug)|\[DllImport|\[LibraryImport|ArgumentList|Arguments\s*=|Registry|SetValue|GetValue|nameof\(|FindResource|SetResourceReference|Path\.Combine|EntryPoint|""-[a-z]|const string \w*(Key|Id|Name)\b")]
    private static partial Regex MakeSkipLine();

    [GeneratedRegex(@"L\.P\([^,]+,\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex MakePluralOne();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex MakePlaceholder();
}
