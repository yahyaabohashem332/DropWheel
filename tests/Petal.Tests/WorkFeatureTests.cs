using Petal.App.Settings;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Text;
using Petal.Engines.Imaging;
using Petal.Engines.Media;
using Petal.Recipes;

namespace Petal.Tests;

/// <summary>
/// The parts of 1.7 that need no screen, sound or model: the palette's matching, snippets, private details
/// in text, bursts of similar shots, names from what a file holds, meeting notes, and wheels shared as text.
/// </summary>
public sealed class WorkFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-work-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_palette_ranks_the_whole_name_then_its_start_then_words_then_letters()
    {
        int exact = Fuzzy.Score("record the screen", "Record the Screen");
        int start = Fuzzy.Score("record", "Record the Screen");
        int words = Fuzzy.Score("rec scr", "Record the Screen");
        int inside = Fuzzy.Score("screen", "Record the Screen");
        int initials = Fuzzy.Score("rts", "Record the Screen");
        int letters = Fuzzy.Score("rcdscn", "Record the Screen");
        Assert.Equal(1000, exact);
        Assert.True(exact > start && start > words && words > inside && inside > initials && initials > letters && letters > 0,
            $"{exact} {start} {words} {inside} {initials} {letters}");
        Assert.Equal(0, Fuzzy.Score("xyz", "Record the Screen"));
        Assert.Equal(0, Fuzzy.Score("", "Record"));
        // Two letters out of order are no match: too many names would have them.
        Assert.Equal(0, Fuzzy.Score("rd", "Desk Rider"));
    }

    [Fact]
    public void Arabic_matches_with_or_without_hamza_taa_marbuta_and_diacritics()
    {
        Assert.Equal("احمد", Fuzzy.Fold("أَحْمَد"));
        Assert.Equal("مدرسه", Fuzzy.Fold("مدرسة"));
        Assert.Equal("مستشفي", Fuzzy.Fold("مستشفى"));
        Assert.Equal("ab c", Fuzzy.Fold("  Áb_-C  "));
        Assert.Equal(1000, Fuzzy.Score("اسال بتل", "إسأل بتل"));
        Assert.True(Fuzzy.Best("تسجيل", "Record the Screen", null, "تسجيل الشاشة") >= 850);
    }

    [Fact]
    public void A_snippet_expands_only_as_a_word_of_its_own_and_the_longest_wins()
    {
        var addr = new Snippet { Abbreviation = ";addr", Text = "12 Palm Street" };
        var addrWork = new Snippet { Abbreviation = ";addrw", Text = "Office 4" };
        var tiny = new Snippet { Abbreviation = ";", Text = "never" };
        List<Snippet> all = [addr, addrWork, tiny];
        var match = Snippets.Match("send it to ;addr", all);
        Assert.NotNull(match);
        Assert.Same(addr, match.Snippet);
        Assert.Equal(5, match.Typed);
        Assert.Same(addrWork, Snippets.Match(";addrw", all)!.Snippet);
        Assert.Null(Snippets.Match("x;addr", all));
        Assert.Null(Snippets.Match(";add", all));
        Assert.Null(Snippets.Match("hello;", all));
    }

    [Fact]
    public void A_snippet_typed_with_the_Arabic_keyboard_on_still_expands()
    {
        var addr = new Snippet { Abbreviation = ";addr", Text = "12 Palm Street" };
        var typed = "مرحبا " + KeyboardLayout.ToArabic(";addr");
        Assert.NotEqual(";addr", KeyboardLayout.ToArabic(";addr"));
        var match = Snippets.Match(typed, [addr]);
        Assert.NotNull(match);
        Assert.Equal(KeyboardLayout.ToArabic(";addr").Length, match.Typed);
    }

    [Fact]
    public void Snippet_placeholders_are_filled_and_the_cursor_goes_back_to_its_mark()
    {
        var now = new DateTime(2026, 3, 20, 9, 30, 0);
        var filled = Snippets.Fill("Dear {clipboard},{cursor} Thanks", now, () => "Sara");
        Assert.Equal("Dear Sara, Thanks", filled.Text);
        Assert.Equal(" Thanks".Length, filled.CursorBack);
        var hijri = Snippets.Fill("{hijri}", now).Text;
        Assert.True(KeyboardLayout.LooksArabic(hijri), hijri);
        Assert.Contains("1447", hijri.Normalize(), StringComparison.Ordinal);
        Assert.Equal(0, Snippets.Fill("{DATE}", now).CursorBack);
        Assert.DoesNotContain("{", Snippets.Fill("{date} {time} {day} {clipboard}", now).Text, StringComparison.Ordinal);
        Assert.All(Snippets.Starters(), s => Assert.StartsWith(";", s.Abbreviation, StringComparison.Ordinal));
    }

    [Fact]
    public void Private_details_are_found_in_text_and_ordinary_numbers_are_left()
    {
        static List<(string Text, PrivateKind Kind)> Found(string line) =>
            PrivateInfo.Find(line).Select(m => (line.Substring(m.Start, m.Length).Trim(), m.Kind)).ToList();

        Assert.Equal([("sara.k@example.com", PrivateKind.Email)], Found("Write to sara.k@example.com today"));
        Assert.Equal([("4111 1111 1111 1111", PrivateKind.Card)], Found("Card 4111 1111 1111 1111 exp 08/29"));
        Assert.Equal([("GB82 WEST 1234 5698 7654 32", PrivateKind.Iban)], Found("IBAN GB82 WEST 1234 5698 7654 32"));
        Assert.Equal([("SA0380000000608010167519", PrivateKind.Iban)], Found("الآيبان SA0380000000608010167519"));
        Assert.Equal([("+966 50 123 4567", PrivateKind.Phone)], Found("Call +966 50 123 4567 now"));
        Assert.Equal([("٠٥٠١٢٣٤٥٦٧", PrivateKind.Phone)], Found("جوال ٠٥٠١٢٣٤٥٦٧"));
        Assert.Equal([("192.168.1.20", PrivateKind.Address)], Found("Server 192.168.1.20 is up"));
        Assert.Equal([("hunter22", PrivateKind.Secret)], Found("Password: hunter22"));
        Assert.Equal([("Xy7#k", PrivateKind.Secret)], Found("كلمة المرور: Xy7#k"));
        // Dates, sums, versions, a number that fails the card check, and an IBAN with a wrong sum.
        Assert.Empty(Found("Due 2026-05-03, total 1,250.00, version 10.0.19045"));
        Assert.Empty(Found("Order 4111 1111 1111 1112"));
        // A mistyped IBAN is no IBAN, though its long run of digits is still covered.
        Assert.DoesNotContain(Found("IBAN GB82 WEST 1234 5698 7654 33"), f => f.Kind == PrivateKind.Iban);
        Assert.Empty(Found("999.300.1.1"));
        Assert.True(PrivateInfo.Luhn("79927398713"));
        Assert.False(PrivateInfo.Luhn("79927398710"));
    }

    [Fact]
    public void Shots_taken_together_that_look_alike_make_a_burst()
    {
        var t = new DateTime(2026, 6, 1, 12, 0, 0);
        ShotInfo Shot(ulong hash, int width = 4000, int height = 3000) => new(10, null, hash, width, height);
        List<(DateTime, ShotInfo)> timeline =
        [
            (t, Shot(0)),
            (t.AddSeconds(5), Shot(0b111)),
            (t.AddSeconds(100), Shot(0b1111)),        // within two minutes of the one before
            (t.AddMinutes(10), Shot(0)),              // too long after
            (t.AddMinutes(10).AddSeconds(3), Shot(ulong.MaxValue)), // a different picture
            (t.AddMinutes(20), Shot(5)),
            (t.AddMinutes(20).AddSeconds(2), Shot(5, 3000, 4000)), // turned on its side
            (t.AddMinutes(30), Shot(9)),
            (t.AddMinutes(30).AddSeconds(1), Shot(9)),
        ];
        var bursts = DuplicateFinder.Bursts(timeline);
        Assert.Equal(2, bursts.Count);
        Assert.Equal([0, 1, 2], bursts[0]);
        Assert.Equal([7, 8], bursts[1]);
        Assert.Empty(DuplicateFinder.Bursts([]));
    }

    [Fact]
    public void Names_from_what_a_file_holds_are_short_and_safe()
    {
        Assert.Equal("Invoice ACME Trading LLC 2026-05-03",
            SmartNamer.Invoice("ACME Trading LLC\nTax Invoice No. 55\nDate: 2026/05/03\nTotal 120.00", arabic: false));
        Assert.Equal("فاتورة مؤسسة النخيل 03-05-2026",
            SmartNamer.Invoice("فاتورة ضريبية\nمؤسسة النخيل\nالتاريخ 03.05.2026", arabic: true));
        Assert.Null(SmartNamer.Invoice("Minutes of the weekly meeting", arabic: false));
        Assert.Equal("The quarterly budget review for the",
            SmartNamer.FirstWords("2026\n\n--- 14 ---\nThe quarterly budget review for the north office\nmore"));
        Assert.Null(SmartNamer.FirstWords("123 456\n---"));
        Assert.Equal("Plan A or B", SmartNamer.Tidy("  \"Plan: A/or B?\"  ".Replace(":", "").Replace("/", " ")));
        Assert.Equal("abcd", SmartNamer.Tidy("a:b*c?\"d\""));
        Assert.Null(SmartNamer.Tidy(" .. "));
        Assert.Equal(60, SmartNamer.Tidy(new string('x', 90))!.Length);
        Assert.Equal(SmartNamer.Subjects.Length, SmartNamer.Subjects.Select(s => s.English).Distinct().Count());
    }

    [Fact]
    public void Rename_puts_in_what_the_file_holds_or_keeps_its_name()
    {
        var a = Path.Combine(_dir, "scan001.pdf");
        var b = Path.Combine(_dir, "scan002.pdf");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        var plan = BatchRename.Plan([a, b], new RenameRule { Pattern = "{content}" }, content: p => p == a ? "Invoice ACME 2026-05-03" : null);
        Assert.Equal("Invoice ACME 2026-05-03.pdf", plan[0].NewName);
        Assert.Equal("scan002.pdf", plan[1].NewName);
    }

    [Fact]
    public void Meeting_transcripts_read_with_times_and_pauses()
    {
        var transcript = new Transcript(
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(2), " Hello everyone. "),
            new(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(4), "[BLANK_AUDIO]"),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "Let's start."),
            new(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(70), "Next, the budget."),
        ], "en");
        Assert.Equal("[00:00] Hello everyone.\n[00:03] Let's start.\n\n[01:05] Next, the budget.",
            MeetingNotes.TranscriptText(transcript).Replace("\r\n", "\n"));
        Assert.Equal("1:02:03", MeetingNotes.Stamp(new TimeSpan(1, 2, 3)));
        Assert.Equal("09:05", MeetingNotes.Stamp(TimeSpan.FromSeconds(545)));
    }

    [Fact]
    public void A_long_meeting_is_cut_into_pieces_between_lines()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"[{i:000}] line number {i}").ToList();
        var text = string.Join("\n", lines) + "\n" + new string('w', 2500);
        var chunks = MeetingNotes.Chunks(text, 1000);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000, c.Length.ToString()));
        Assert.Equal(text.Replace("\n", ""), string.Concat(chunks.Select(c => c.Replace("\n", ""))));
        // No line is split unless it is longer than a piece.
        Assert.All(chunks.Where(c => c.StartsWith('[')), c => Assert.Matches(@"line number \d+$", c));
        Assert.Single(MeetingNotes.Chunks("short", 1000));
        Assert.Empty(MeetingNotes.Chunks("  \n ", 1000));
    }

    [Fact]
    public void The_notes_file_has_the_summary_before_the_transcript()
    {
        var md = MeetingNotes.Markdown(new DateTime(2026, 5, 3, 10, 0, 0), TimeSpan.FromMinutes(42), "## الملخص\nتم الاتفاق.", "[00:00] مرحبا", arabic: true);
        Assert.StartsWith("# ملاحظات الاجتماع", md, StringComparison.Ordinal);
        Assert.Contains("2026-05-03 10:00 · 42:00", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("## الملخص", StringComparison.Ordinal) < md.IndexOf("## النص الكامل", StringComparison.Ordinal));
        var plain = MeetingNotes.Markdown(DateTime.Now, TimeSpan.FromSeconds(30), null, "[00:00] Hi", arabic: false);
        Assert.Contains("## Transcript", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_wheel_comes_back_whole_without_anything_personal()
    {
        var work = WheelTemplates.Work();
        work.Name = "My work";
        var code = WheelSharing.ToCode(work);
        Assert.StartsWith("PETAL-WHEEL:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", code, StringComparison.Ordinal);
        Assert.True(code.Length < 2000, code.Length.ToString());
        foreach (var text in new[] { code, "  " + code.ToLowerInvariant()[..12] + code[12..] + "\r\n", WheelSharing.ToJson(work) })
        {
            var (wheel, problem) = WheelSharing.Read(text);
            Assert.Null(problem);
            Assert.NotNull(wheel);
            Assert.Equal("My work", wheel.Name);
            Assert.Null(wheel.Template);
            Assert.NotEqual(work.Id, wheel.Id);
            Assert.Equal(work.Items.Select(e => e.Action ?? e.Label), wheel.Items.Select(e => e.Action ?? e.Label));
        }
        Assert.Equal(work.Items.Count(e => e.Kind != EntryKind.Group), WheelSharing.Summary(work).Petals - work.Items.Where(e => e.Children is not null).Sum(e => e.Children!.Count));
    }

    [Fact]
    public void Anything_that_isnt_a_wheel_is_turned_away()
    {
        Assert.Equal("This isn't a Petal wheel.", WheelSharing.Read("hello").Problem);
        Assert.Equal("This isn't a Petal wheel.", WheelSharing.Read("PETAL-WHEEL:!!!").Problem);
        Assert.Equal("This isn't a Petal wheel.", WheelSharing.Read("{\"petalWheel\":2,\"wheel\":{\"name\":\"x\",\"items\":[]}}").Problem);
        Assert.Equal("This isn't a Petal wheel.", WheelSharing.Read("{\"petalWheel\":1,\"wheel\":{\"name\":\" \",\"items\":[]}}").Problem);
        Assert.Equal("This wheel is too large.", WheelSharing.Read("{" + new string(' ', 600_000) + "}").Problem);
    }

    [Fact]
    public void A_shared_wheel_is_cut_to_what_a_wheel_can_hold()
    {
        var big = WheelTemplates.Custom("Big");
        big.Kind = WheelKind.Quick;
        big.Name = new string('n', 70);
        big.Items = Enumerable.Range(0, 20).Select(_ => new WheelEntry { Kind = EntryKind.Action, Action = "screen.full", Icon = "no-such-icon" }).ToList();
        big.Items[0] = new WheelEntry { Kind = EntryKind.Group, Label = "G", Children = Enumerable.Range(0, 12).Select(_ => new WheelEntry { Kind = EntryKind.Action, Action = "screen.full" }).ToList() };
        var (wheel, _) = WheelSharing.Read(WheelSharing.ToCode(big));
        Assert.NotNull(wheel);
        Assert.Equal(WheelKind.Custom, wheel.Kind);
        Assert.Equal(40, wheel.Name.Length);
        Assert.Equal(WheelEditorWindow.MostPetals, wheel.Items.Count);
        Assert.Equal(8, wheel.Items[0].Children!.Count);
        Assert.All(wheel.Items.Skip(1), e => Assert.Null(e.Icon));
    }

    [Fact]
    public void The_gallery_wheels_all_read_back_and_use_known_actions()
    {
        Assert.True(WheelGallery.All.Count >= 10);
        foreach (var entry in WheelGallery.All)
        {
            var made = entry.Make();
            Assert.InRange(made.Items.Count, 1, WheelEditorWindow.MostPetals);
            var (wheel, problem) = WheelSharing.Read(WheelSharing.ToCode(made));
            Assert.Null(problem);
            Assert.Equal(0, WheelSharing.Summary(wheel!).Unknown);
        }
    }

    [Fact]
    public void Settings_from_1_6_get_the_work_wheel_and_the_private_screenshot_once()
    {
        var screen = WheelTemplates.Screen();
        screen.Items.RemoveAll(e => e.Action == "screen.private");
        var settings = new AppSettings { WheelsVersion = 2, Wheels = [WheelTemplates.Quick(), WheelTemplates.Media(), screen, WheelTemplates.Clipboard(), WheelTemplates.Custom("Mine")] };
        Assert.True(WheelTemplates.Upgrade(settings));
        Assert.Equal(["quick", "media", "screen", "clipboard", "work", null], settings.Wheels.Select(w => w.Template));
        Assert.Single(screen.Items, e => e.Action == "screen.private");
        Assert.False(WheelTemplates.Upgrade(settings));
        Assert.Single(settings.Wheels, w => w.Template == "work");
    }
}
