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
        "Petal", "DropWheel", "Petal input hooks", "Preferred DropEffect", "do:draw.timer", "do:screen.circle", "Shell.Application", "System.Photo.Orientation", "PART_Track", "PART_ContentHost",
        "Segoe UI Variable Display, Segoe UI", "Segoe UI Variable Text, Segoe UI", "Cascadia Mono, Consolas",
        "Shift", "Ctrl", "Alt", "Win", "⇧ Shift", "Shift ⇧", "Windows ⊞", "English",
        "FFmpeg", "Microsoft Office", "4K (2160p)", "Full HD (1080p)", "HD (720p)", "SD (480p)",
        "ID3v2_3", "ID3v2_4", "ID3v1", "Vorbis",
        // Key names as printed on keycaps, and Windows class names.
        "Enter", "Backspace", "Progman", "SHELLDLL_DefView", "Petal shortcut",
        // Font names, a template part, a unit and a file-name suffix (file names stay in English).
        "Segoe UI", "Segoe Script", "Segoe Print", "Ink Free", "Cell", "pt", "PNG · SVG", "Unlocked",
        // Date and time patterns, and a sample installer name in the design snapshots.
        "HH:mm", "yyyy-MM-dd", "ddd d MMM", "ddd d MMM yyyy", "d MMM, HH:mm", "DropWheel-Setup-1.1.0.exe", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH.mm.ss",
        // A font, a Recycle Bin record pattern, a MIME type, a thread name and a voice maker's prefix.
        "Consolas", "$I*", "image/jpeg", "Petal search index", "Petal warm-up", "Microsoft",
        // The culture the wheel measures its text in, as WPF text blocks do by default.
        "en-US",
        // The development switch that runs a wheel choice on a folder ("folder:OrganizeFolder").
        "folder:",
        // Keycap names, cultures and date patterns for the statistics, and the line a PowerShell petal starts with.
        "Escape", "ar-EG", "en-GB", "d MMM", "d MMM yyyy", "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
        // Thread and message-window names.
        "Petal app list", "DropWheel keep awake", "Petal wheel shortcuts",
        // Apps the ready-made wheels are for, by their own names.
        "VS Code", "Word", "Excel", "PowerPoint", "Teams", "Zoom", "Photoshop", "Slack", "Discord", "Bluetooth",
        // Sample requests and search results in the design snapshots, already in both languages.
        // Photography abbreviations and ExifTool tag names, which read the same in Arabic.
        "f/", "ISO", "mm", "FNumber", "Software",
        "make them 1080 px WEBP and zip them", "صغّرها إلى 1080 بكسل وحوّلها إلى WEBP ثم ضعها في ملف مضغوط", "…our trip to the «beach» in July…",
        // The rename pattern a recipe step starts with, and singular forms chosen inside L.P (looked up through their plural).
        "{name} {n}", "{0} file has no password and is skipped.", "{0} file already has a password and is skipped.",
        // A sample script, and a name the statistics window shows in the language Petal speaks.
        "$text = $env:PETAL_INPUT.ToUpper()\nSet-Clipboard $text\nWrite-Output $text", "Convert to WEBP",
        // Message-window and thread names, clipboard formats and Windows class names the screen tools look for, and a code label.
        // Sample texts, sums and notes in the design snapshots of 1.6 (the Arabic ones are written beside them).
        "Hi team, the report is late again because the numbers came in late, sorry, we will send it tomorrow morning hopefully.",
        "The meeting moves to Thursday at ten.", "Call Sara before five", "Send the updated slides", "Address: 14 Harbour Road, Bristol",
        "Thanks for your time, I'll send the file today.", "12% of 340", "5 km in mi",
        // Apps the gallery has wheels for, by their own names; their program names; keycaps; a wheel code's mark; words added to searches.
        "Figma", "Illustrator", "Premiere Pro", "Blender", "Notion", "Outlook", "adobe premiere pro", "Space", "Tab", "PETAL-WHEEL:", "snippet", "wheel", "workspace",
        // Made-up windows, snippets, PCs, notes and captions in the design snapshots of 1.7 (the Arabic ones are written beside them).
        "Spotify", "WINWORD", "Code", "petal-site — Visual Studio Code", "Project board - Google Chrome", "Landing page – Figma",
        ";addr", ";thx", "14 Harbour Road, Bristol BS1 5TT", "Thanks so much, {cursor}. Best wishes.", "Office PC", "Sara's laptop", "Study",
        "[00:00] Morning all, let's start with the launch date.\n[00:06] The translation isn't finished yet.", "We'll move the launch to next Thursday.", "And we'll tell the team",
        "Petal clipboard", "Petal screen recording", "Clipboard Viewer Ignore", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "WPF / C#",
        // 1.8: thread names, keycaps, UI Automation control kinds the step words are chosen by, a date pattern, a format name,
        // the support report (kept in English for whoever reads it), polite words a spoken command drops, and a font sample's letters.
        "Petal camera recording", "Petal steps", "Esc", "Insert", "Home", "Page Up", "Page Down", "ControlType.",
        "menu item", "tab item", "check box", "radio button", "Hyperlink", "Edit", "combo box", "list item", "tree item", "data item",
        "d MMMM", "Markdown", "ERROR", "[ok]", "[note]", "(left out)", "(unreadable)", "Remote", "Destinations",
        "please", "petal", "Aa Bb Cc", "1.4 GB", "212 GB", "C:", "Notepad", "yyyyMMdd-HHmmss",
        // Languages named to the AI model in English, in the instruction it reads.
        "Turkish", "Persian", "Urdu", "Indonesian", "French", "Spanish", "0123456789 !?&@ ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        // 1.8.4: a progress card's sample line in the design snapshots.
        "lesson.mp4 · 37%",
        // 1.10: the sample page Circle Anything is drawn over in the design snapshots, its made-up explanation, and what its checks write when nothing was read.
        "A quiet lake town for the weekend", "Two hours from the city, the old harbour has cafés, boat trips and a market", "every Saturday morning. The trail around the lake is easy and shaded, and", "the views at sunset are worth the walk.", "Rooms from $1,299 a week · the hotel is 3.5 km from the station", "Live Q&A on Nov 5, 2026 at 3:00 PM PST · call +1 415 555 0134", "Theme colour: #7C5CFC", "Scan for the app", "The harbour at dusk", "A short travel note: a small lake town two hours from the city, with an old harbour, cafés, boat trips and a Saturday market. The walk round the lake is easy and shady, best at sunset.", "(not read)", "(nothing)",
        // 1.11: the made-up moments of an interview Make Shorts is drawn with in the design snapshots, and the sample title over its preview.
        "Can you take me to space? I want to float around and do flips.", "The truth is actually pretty amazing. When we get into space, we all feel something.",
        "We go around the Earth every 90 minutes. I've seen it all.", "My advice to anyone who dreams of flying: start today.",
        "The first time I saw the Earth through the window, I couldn't speak.", "The best part",
        // 1.12: the made-up lines of an interview Dub is drawn with in the design snapshots (the Arabic ones are written beside them).
        "I'm a very lucky person.", "I'm grateful every day, and I want more people to come up here to space.", "We need some good wings up here.",
        "I would love to go. Can you take me to space?", "I'd look down and say: I'm over Europe, and in twenty seconds I'll be on the West Coast.",
        // 1.15: the studio's thread, FFmpeg's filters, the file name's ending, and the made-up notes app its design snapshots show (a screen as it was recorded).
        "Petal studio pointer", ",highpass=f=80,afftdn=nr=12:nf=-40:tn=1", "loudnorm=I=-16:TP=-1.5:LRA=11", "alimiter=limit=0.95", "[out]", ",atempo=2", "Cinematic",
        "Ctrl + S", "Notes — Quarterly plan", "Inbox", "Quarterly plan", "Ideas", "Meeting notes", "Archive",
        "Launch the new reading lamp in March, with the shops and the website ready the same week.", "Photos for the website", "Price list for the shops",
        "Two short videos for social media", "Answers for the support team", "Budget", "The marketing budget is 250,000 euros, half of it for online adverts.",
        "Next review: Thursday at 10:00.",
        // 1.14: the made-up questions and answers Ask a File is drawn with in the design snapshots (the Arabic ones are written beside them).
        "Is elementary education compulsory?", "Yes. Elementary education is compulsory, and it must be free, at least in the elementary and fundamental stages.",
        "Everyone has the right to education. Education shall be free, at least in the elementary and fundamental stages. Elementary education shall be compulsory.",
        "When was this declaration adopted?", "It was adopted by the United Nations General Assembly in Paris on 10 December 1948.",
        "The United Nations General Assembly adopted the Universal Declaration of Human Rights in Paris on 10 December 1948.",
        "On 10 December 1948 the General Assembly adopted this declaration.", "How long is the warranty?",
        // 1.13: the made-up words of an interview Edit by Text is drawn with in the design snapshots (the Arabic ones are written beside them).
        "So, um, I think the the best part of living up here is the view. | Every ninety minutes we see a sunrise. Uh, it never gets old. ~ My advice to anyone who dreams of flying: start today, and and don't wait. | The truth is actually pretty amazing. When we get into space, we all feel something strange, something hard to put into words.",
    };

    private static readonly Regex Literal = MakeLiteral();
    private static readonly Regex Translated = MakeTranslated();
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
                if (line.StartsWith("//") || line.StartsWith("using "))
                    continue;
                // A line the scan otherwise skips (resources, paths, logging) still has its L.T and L.F texts checked.
                bool skipped = SkipLine.IsMatch(line);
                foreach (Match m in skipped ? Translated.Matches(line) : Literal.Matches(line))
                {
                    if (m.Groups["prefix"].Value.Contains('$'))
                        continue;
                    var text = UnicodeEscape().Replace(m.Groups["body"].Value, e => ((char)Convert.ToInt32(e.Groups[1].Value, 16)).ToString())
                        .Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
                    bool looks = skipped || m.Groups["call"].Success ? text.Any(char.IsAsciiLetter) : LooksLikeInterfaceText(text);
                    if (!looks || singulars.Contains(text) || NotTranslated.Contains(text.Trim()))
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
        if (s.StartsWith("Petal.") || s.StartsWith("http") || s.StartsWith('.') || s.StartsWith('-') || s.StartsWith('/') || s.StartsWith('#') || (s.StartsWith('{') && !s.Contains(' ')) || s.StartsWith("pack:"))
            return false;
        // A path on its own is not text; a sentence that names a folder such as Downloads\Petal is.
        if (s.Contains('\\') && (!s.Contains(' ') || s.StartsWith('%') || Regex.IsMatch(s, @"^[A-Za-z]:\\") || Regex.IsMatch(s, @"\.[A-Za-z0-9]{2,4}$")) || s.Contains("*.") || s.Contains("%1"))
            return false;
        // Batch commands, and text already written in Arabic.
        if (s.StartsWith('@') || s.Any(c => c is >= (char)0x0600 and <= (char)0x06FF))
            return false;
        return !Regex.IsMatch(s, @"^[a-z][A-Za-z0-9]*$")              // icon names, ids
            && !Regex.IsMatch(s, @"^[A-Z][a-z]+[A-Z][A-Za-z0-9]*$")    // PascalCase ids
            && !Regex.IsMatch(s, @"^[\w\- ]+\.[A-Za-z0-9]{2,4}$")      // file names
            && !Regex.IsMatch(s, @"^[0#.,:%PN \d{}\-+;]+$")            // number formats
            && !Regex.IsMatch(s, @"^[A-Z0-9_]{1,6}$")                  // format codes such as PNG
            && !Regex.IsMatch(s, @"^[a-z0-9_\-]+(:[a-z0-9_\-=]+)?$")   // filter names
            && !Regex.IsMatch(s, @"^[0-9A-F]{8}-[0-9A-F]{4}-")         // COM ids
            && !Regex.IsMatch(s, @"^M[\s\d]")                          // path data
            && !Regex.IsMatch(s, @"^(?:(?:Ctrl|Alt|Shift|Win)\+)+\S+(?: (?:(?:Ctrl|Alt|Shift|Win)\+)*\S+)*$") // key chords
            && !Regex.IsMatch(s, @"^[A-Z0-9]+(?:_[A-Z0-9]+)+$")        // environment variables
            && !Regex.IsMatch(s, @"^[a-z]+\.[a-z]+$")                      // action ids such as system.darkmode
            && !Regex.IsMatch(s, @"^[a-z]+:(?:[A-Z][A-Za-z]*)?$")      // key prefixes such as tool: and files:Tools
            && !Regex.IsMatch(s, @"^(?:ms-[a-z\-]+|shell):");                // Windows addresses
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Petal's sources were not found above the test folder.");
    }

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})")]
    private static partial Regex UnicodeEscape();

    [GeneratedRegex(@"(?<call>L\.[TF]\()?(?<prefix>\$@|@\$|\$|@)?""(?<body>(?:[^""\\\n]|\\.)*)""")]
    private static partial Regex MakeLiteral();

    /// <summary>Texts handed straight to L.T or L.F.</summary>
    [GeneratedRegex(@"L\.[TF]\((?<prefix>)""(?<body>(?:[^""\\\n]|\\.)*)""")]
    private static partial Regex MakeTranslated();

    [GeneratedRegex(@"\bLog\.(Info|Error|Warn|Debug)|\[DllImport|\[LibraryImport|ArgumentList|Arguments\s*=|Registry|SetValue|GetValue|nameof\(|FindResource|SetResourceReference|Path\.Combine|EntryPoint|""-[a-z]|const string \w*(Key|Id|Name)\b|\bKey\.[A-Z]|InvokeMember\(|GetTypeFromProgID")]
    private static partial Regex MakeSkipLine();

    [GeneratedRegex(@"L\.P\([^,]+,\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex MakePluralOne();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex MakePlaceholder();
}
