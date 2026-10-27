using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Stats;
using Petal.App.Visuals;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>
/// The wheels without files and what came with them: keystrokes, the ready-made wheels, the order wheels
/// come in, petals for each kind of entry, the cancel zone, memory, statistics, settings files and scripts.
/// They touch the shared settings, so they run with the history tests, one at a time.
/// </summary>
[Collection("History")]
public sealed class WheelFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-wheels-").FullName;
    private readonly AppSettings _saved = SettingsStore.Current;

    public void Dispose()
    {
        SettingsStore.Replace(_saved);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------- keystrokes ----------

    [Fact]
    public void Keystrokes_are_read_as_chords_and_sequences()
    {
        var one = KeySender.Parse("Ctrl+Shift+T")!;
        Assert.Equal(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, Key.T), Assert.Single(one));
        var two = KeySender.Parse("Ctrl+K Z")!;
        Assert.Equal([new KeyChord(ModifierKeys.Control, Key.K), new KeyChord(ModifierKeys.None, Key.Z)], two);
        Assert.Equal(Key.OemPlus, KeySender.Parse("Ctrl++")![0].Key);
        Assert.Equal(Key.Oem3, KeySender.Parse("Ctrl+`")![0].Key);
        Assert.Equal(Key.Escape, KeySender.Parse("Esc")![0].Key);
        Assert.Equal(Key.F5, KeySender.Parse("F5")![0].Key);
        Assert.Equal(Key.D1, KeySender.Parse("Ctrl+Alt+1")![0].Key);
        Assert.Null(KeySender.Parse(""));
        Assert.Null(KeySender.Parse("Ctrl+Banana"));
        Assert.Null(KeySender.Parse("Ctrl+A+B"));
        Assert.Null(KeySender.Parse("Ctrl+Shift"));
        // Written back the way it is saved, and read again the same.
        Assert.Equal("Ctrl+Shift+T", KeySender.Normalise(one));
        Assert.Equal(two, KeySender.Parse(KeySender.Normalise(two)));
        Assert.Equal("Ctrl + Shift + T", one[0].Display);
    }

    [Fact]
    public void A_chord_presses_modifiers_first_and_lets_them_go_last()
    {
        var inputs = KeySender.Inputs(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, Key.Left));
        Assert.Equal(6, inputs.Length);
        Assert.Equal([0x11, 0x10, 0x25, 0x25, 0x10, 0x11], inputs.Select(i => (int)i.u.ki.wVk));
        Assert.Equal([false, false, false, true, true, true], inputs.Select(i => (i.u.ki.dwFlags & 2) != 0));
        // The arrow is an extended key; the modifiers are not.
        Assert.True((inputs[2].u.ki.dwFlags & 1) != 0);
        Assert.True((inputs[0].u.ki.dwFlags & 1) == 0);
    }

    [Fact]
    public async Task Keys_are_never_sent_to_nowhere_or_to_Petal_itself()
    {
        var chords = KeySender.Parse("Ctrl+C")!;
        Assert.False(await KeySender.SendAsync(IntPtr.Zero, chords));
        Assert.False(await KeySender.SendAsync(new IntPtr(0x7FFF_FFF0), chords));
    }

    // ---------- the ready-made wheels ----------

    [Fact]
    public void Ready_made_wheels_are_complete_and_arrive_once()
    {
        var settings = new AppSettings();
        Assert.True(WheelTemplates.Upgrade(settings));
        Assert.False(WheelTemplates.Upgrade(settings));
        Assert.Equal(WheelTemplates.Version, settings.WheelsVersion);
        Assert.Contains(settings.Wheels, w => w.Kind == WheelKind.Quick);
        Assert.Contains(settings.Wheels, w => w.Kind == WheelKind.Media);
        Assert.True(settings.Wheels.Count(w => w.Kind == WheelKind.App) >= 10);
        // A removed wheel doesn't come back on the next start.
        settings.Wheels.RemoveAll(w => w.Template == "zoom");
        Assert.False(WheelTemplates.Upgrade(settings));
        Assert.DoesNotContain(settings.Wheels, w => w.Template == "zoom");

        foreach (var wheel in settings.Wheels)
        {
            Assert.InRange(wheel.Items.Count, 1, WheelEditorWindow.MostPetals);
            Assert.Equal(wheel.Template, WheelTemplates.Fresh(wheel.Template)!.Template);
            if (wheel.Kind == WheelKind.App)
                Assert.NotEmpty(wheel.Apps);
            foreach (var entry in Flatten(wheel.Items))
            {
                if (entry.Icon is { } icon)
                    Assert.True(Icons.Keys.Contains(icon), $"{wheel.Name}: no icon {icon}");
                switch (entry.Kind)
                {
                    case EntryKind.Action:
                        Assert.NotNull(WheelActions.Get(entry.Action));
                        break;
                    case EntryKind.Keystroke:
                        Assert.NotNull(KeySender.Parse(entry.Keys));
                        Assert.False(string.IsNullOrWhiteSpace(entry.Label));
                        break;
                    case EntryKind.Group:
                        Assert.InRange(entry.Children!.Count, 1, 8);
                        break;
                }
            }
        }
        // Every built-in action has a known icon, and only switches say on or off.
        foreach (var action in WheelActions.All)
        {
            Assert.True(Icons.Keys.Contains(action.Icon), action.Id);
            Assert.Contains(action.Section, new[] { "Windows", "Screen", "Draw", "Text", "Arrange", "Clipboard", "Notes", "Work", "Media", "DropWheel" });
        }
        Assert.Equal(WheelActions.All.Count, WheelActions.All.Select(a => a.Id).Distinct().Count());
    }

    private static IEnumerable<WheelEntry> Flatten(IEnumerable<WheelEntry> entries) =>
        entries.SelectMany(e => e.Children is { } children ? Flatten(children).Prepend(e) : [e]);

    [Fact]
    public void The_app_in_front_comes_first_then_the_others_in_order()
    {
        var settings = new AppSettings();
        WheelTemplates.Upgrade(settings);
        settings.RememberLastPetal = false;

        var (inChrome, start) = Rings.ForProgram(settings, "chrome");
        Assert.Equal(0, start);
        Assert.Equal(settings.Wheels.First(w => w.Template == "browser").Id, inChrome[0].Key);
        // Other apps' wheels stay out; quick actions, media and the other ready-made wheels follow.
        string[] others = ["Quick", "Media", "Screen", "Draw & Present", "Text", "Arrange", "Clipboard", "Work"];
        Assert.Equal(others.Prepend("Browser"), inChrome.Select(p => p.Title));

        var (elsewhere, _) = Rings.ForProgram(settings, "notepad");
        Assert.Equal(others, elsewhere.Select(p => p.Title));

        // A wheel that is off isn't shown; a wheel's own shortcut opens straight onto it.
        settings.Wheels.First(w => w.Kind == WheelKind.Media).Enabled = false;
        Assert.Equal(others.Where(t => t != "Media"), Rings.ForProgram(settings, null).Pages.Select(p => p.Title));
        var zoom = settings.Wheels.First(w => w.Template == "zoom");
        var (only, at) = Rings.ForProgram(settings, null, zoom.Id);
        Assert.Equal(zoom.Id, only[at].Key);
    }

    [Fact]
    public void The_wheel_used_last_opens_first_unless_an_app_has_one()
    {
        var settings = new AppSettings();
        WheelTemplates.Upgrade(settings);
        settings.RememberLastPetal = true;
        WheelMemory.FilePath = Path.Combine(_dir, "memory.json");
        var media = settings.Wheels.First(w => w.Kind == WheelKind.Media);
        WheelMemory.Remember("ring:" + media.Id, new WheelItem("PLAY", "Play", null, new WheelEntry()));
        var (pages, start) = Rings.ForProgram(settings, "notepad");
        Assert.Equal(media.Id, pages[start].Key);
        var (inCode, codeStart) = Rings.ForProgram(settings, "code");
        Assert.Equal("VS Code", inCode[codeStart].Title);
    }

    // ---------- petals for the user's entries ----------

    [Fact]
    public void Each_kind_of_entry_becomes_a_petal_that_says_what_it_does()
    {
        var keys = Rings.Item(new WheelEntry { Kind = EntryKind.Keystroke, Label = "NEW TAB", Name = "New Tab", Keys = "Ctrl+T" });
        Assert.Equal("NEW TAB", keys.Label);
        Assert.Equal("Ctrl + T", keys.Note);
        Assert.Null(keys.Unavailable);
        Assert.Equal("keyboard", keys.Icon);

        Assert.NotNull(Rings.Item(new WheelEntry { Kind = EntryKind.Keystroke, Keys = "Ctrl+Nothing" }).Unavailable);
        Assert.NotNull(Rings.Item(new WheelEntry { Kind = EntryKind.Folder, Path = Path.Combine(_dir, "gone") }).Unavailable);
        var folder = Rings.Item(new WheelEntry { Kind = EntryKind.Folder, Path = _dir });
        Assert.Null(folder.Unavailable);
        Assert.Equal(Path.GetFileName(_dir).ToUpperInvariant(), folder.Label);
        Assert.NotNull(Rings.Item(new WheelEntry { Kind = EntryKind.App, Path = Path.Combine(_dir, "nothing.exe") }).Unavailable);
        Assert.NotNull(Rings.Item(new WheelEntry { Kind = EntryKind.Script, Script = " " }).Unavailable);
        Assert.Equal("PowerShell", Rings.Item(new WheelEntry { Kind = EntryKind.Script, Script = "Get-Date" }).Note);

        var group = Rings.Item(new WheelEntry
        {
            Kind = EntryKind.Group,
            Name = "Tabs",
            Children = [new WheelEntry { Kind = EntryKind.Keystroke, Keys = "Ctrl+T" }, new WheelEntry { Kind = EntryKind.Keystroke, Keys = "Ctrl+W" }],
        });
        Assert.True(group.IsGroup);
        Assert.Equal(2, group.Children!.Count);
        Assert.Equal("TABS", group.Label);

        var dark = Rings.Item(new WheelEntry { Kind = EntryKind.Action, Action = "system.darkmode", Tint = "#E5484D" });
        Assert.Equal(SystemToggles.IsDarkMode, dark.State);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D), dark.Tint);
        var unknown = Rings.Item(new WheelEntry { Kind = EntryKind.Action, Action = "system.nothing" });
        Assert.True(unknown.Blocked);
        Assert.NotNull(unknown.Unavailable);
        // Every entry has a key of its own for memory and statistics.
        Assert.StartsWith("entry:", keys.Key);
    }

    [Fact]
    public void Reading_the_PC_never_fails()
    {
        // Reading only: nothing here changes a setting of Windows.
        _ = SystemToggles.IsDarkMode;
        _ = SystemToggles.ShowsHiddenFiles;
        _ = SystemToggles.ShowsExtensions;
        _ = SystemToggles.ShowsDesktopIcons;
        _ = SystemToggles.IsMuted;
        _ = SystemToggles.IsMicMuted;
        _ = SystemToggles.RemovableDrives().ToList();
        var windows = RunningApps.List();
        Assert.True(windows.Count <= RunningApps.Most);
        Assert.DoesNotContain(windows, w => w.ExePath is { } p && p.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        foreach (var action in WheelActions.All)
        {
            _ = action.State?.Invoke();
            _ = action.Unavailable?.Invoke();
            _ = action.Note?.Invoke();
        }
    }

    [Fact]
    public async Task What_plays_is_read_or_nothing()
    {
        await WheelActions.RefreshAsync();
        var playing = await MediaControl.CurrentAsync();
        if (playing is not null)
            Assert.NotNull(playing.Title);
    }

    // ---------- the wheel's geometry and memory ----------

    [Fact]
    public void A_larger_cancel_zone_takes_in_the_petals_inner_ends()
    {
        var near = new System.Windows.Vector(0, -55);
        Assert.Equal(0, WheelGeometry.HitTest(near, 6, WheelGeometry.CancelRadius(CancelZone.Small)));
        Assert.Null(WheelGeometry.HitTest(near, 6, WheelGeometry.CancelRadius(CancelZone.Medium)));
        Assert.Null(WheelGeometry.HitTest(new System.Windows.Vector(0, -70), 6, WheelGeometry.CancelRadius(CancelZone.Large)));
        Assert.Equal(0, WheelGeometry.HitTest(new System.Windows.Vector(0, -100), 6, WheelGeometry.CancelRadius(CancelZone.Large)));
        Assert.True(WheelGeometry.CancelRadius(CancelZone.Small) < WheelGeometry.CancelRadius(CancelZone.Medium));
        Assert.True(WheelGeometry.CancelRadius(CancelZone.Large) < WheelGeometry.PetalOuter - 30);
    }

    [Fact]
    public void The_wheel_remembers_the_last_petal_of_each_page_across_runs()
    {
        WheelMemory.FilePath = Path.Combine(_dir, "memory.json");
        var webp = WheelItem.From(F.Webp);
        var png = WheelItem.From(F.Png);
        var content = new WheelContent([png, webp], "");
        Assert.Null(WheelMemory.IndexIn("files:Conversions", content));
        WheelMemory.Remember("files:Conversions", webp);
        WheelMemory.LastFileMode = WheelMode.Tools;
        Assert.Equal(1, WheelMemory.IndexIn("files:Conversions", content));
        Assert.Null(WheelMemory.IndexIn("files:Tools", content));
        Thread.Sleep(1500);
        // Read again from the file, as after a restart.
        WheelMemory.FilePath = Path.Combine(_dir, "memory.json");
        Assert.Equal(1, WheelMemory.IndexIn("files:Conversions", content));
        Assert.Equal(WheelMode.Tools, WheelMemory.LastFileMode);
        WheelMemory.Clear();
        Assert.Null(WheelMemory.IndexIn("files:Conversions", content));
    }

    // ---------- statistics ----------

    [Fact]
    public void Statistics_count_uses_files_and_only_real_savings()
    {
        Statistics.FilePath = Path.Combine(_dir, "stats.json");
        Statistics.Reset();
        Statistics.RecordPetal("format:webp", 0, 0, 0, "Convert to WEBP");
        Statistics.RecordPetal("format:webp", 0, 0, 0, "Convert to WEBP");
        Statistics.RecordPetal("tool:Compress", 0, 0, 0, "Compress");
        Statistics.RecordWork(3, 3_000_000, 1_000_000, 2);
        Statistics.RecordWork(1, 100, 400, 1);
        var data = Statistics.Snapshot();
        Assert.Equal(3, data.Uses);
        Assert.Equal(4, data.Files);
        Assert.Equal(2_000_000, data.Saved);
        Assert.Equal(2, data.Petals["format:webp"]);
        Assert.Equal("Compress", data.Names["tool:Compress"]);
        Assert.Equal(1, Statistics.Streak(data));
        Statistics.Save();
        Statistics.Reload();
        Assert.Equal(4, Statistics.Snapshot().Files);
        Statistics.Reset();
        Assert.Equal(0, Statistics.Snapshot().Files);
    }

    [Fact]
    public void Streaks_and_milestones_follow_the_days()
    {
        var data = new StatsData { Files = 1200, Saved = 2L * 1024 * 1024 * 1024 };
        var today = new DateOnly(2026, 10, 21);
        foreach (var back in new[] { 0, 1, 2, 5, 6, 7, 8, 9, 10, 11, 12 })
            data.Days[Statistics.Key(today.AddDays(-back))] = new DayCount { Uses = 1 };
        Assert.Equal(3, Statistics.Streak(data, today));
        // Nothing yet today: yesterday's run still counts.
        Assert.Equal(3, Statistics.Streak(data, today.AddDays(1)));
        Assert.Equal(0, Statistics.Streak(data, today.AddDays(3)));
        Assert.Equal(8, Statistics.LongestStreak(data));
        var milestones = Statistics.Milestones(data);
        Assert.Contains("files:1000", milestones);
        Assert.DoesNotContain("files:10000", milestones);
        Assert.Contains("saved:1000", milestones);
        Assert.DoesNotContain("saved:10000", milestones);
        Assert.Contains("streak:7", milestones);
    }

    [Fact]
    public void Counting_stops_when_turned_off()
    {
        Statistics.FilePath = Path.Combine(_dir, "stats-off.json");
        Statistics.Reset();
        var enabled = Statistics.Enabled;
        Statistics.Enabled = () => false;
        try
        {
            Statistics.RecordPetal("format:png", 2, 10, 5);
            Assert.Equal(0, Statistics.Snapshot().Uses);
        }
        finally
        {
            Statistics.Enabled = enabled;
        }
    }

    // ---------- settings as a file ----------

    [Fact]
    public void Exported_settings_come_back_whole_except_permissions()
    {
        var settings = new AppSettings { CancelZone = CancelZone.Large, WheelStyle = WheelStyle.Mono, Accent = AccentChoice.Mint };
        WheelTemplates.Upgrade(settings);
        settings.Favourites.Add(new Favourite { Format = "webp", LongestSide = 1600 });
        settings.ApprovedPetals.Add("petal-fingerprint");
        settings.PetalColours["tool:Compress"] = "#30A46C";
        settings.Destinations.Add(new FolderDestination { Folder = _dir, Move = true });
        Directory.CreateDirectory(Petal.Recipes.CustomPetal.Folder);
        File.WriteAllText(Path.Combine(Petal.Recipes.CustomPetal.Folder, "mine.json"), """{ "name": "Mine", "accepts": ["png"], "program": "notepad" }""");

        var file = Path.Combine(_dir, "Petal Settings.petal");
        SettingsTransfer.Export(settings, file);
        Assert.DoesNotContain("petal-fingerprint", File.ReadAllText(file));

        var (read, petals, summary) = SettingsTransfer.Read(file);
        Assert.Equal(CancelZone.Large, read.CancelZone);
        Assert.Equal(WheelStyle.Mono, read.WheelStyle);
        Assert.Equal(AccentChoice.Mint, read.Accent);
        Assert.Equal(settings.Wheels.Count, read.Wheels.Count);
        Assert.Equal("#30A46C", read.PetalColours["tool:Compress"]);
        Assert.True(read.Destinations.Single().Move);
        Assert.Empty(read.ApprovedPetals);
        Assert.Equal(settings.Wheels.Count, summary.Wheels);
        Assert.Equal(1, summary.Favourites);
        Assert.Contains(petals, p => p.File == "mine.json");
        Assert.True(petals.Count >= 1);

        // Imported: this PC's permissions stay, the petals are written, everything takes effect.
        SettingsStore.Current.ApprovedPetals.Add("this-pc");
        File.Delete(Path.Combine(Petal.Recipes.CustomPetal.Folder, "mine.json"));
        AppSettings? applied = null;
        void Reloaded(AppSettings s) => applied = s;
        SettingsStore.Reloaded += Reloaded;
        try
        {
            SettingsTransfer.Apply(read, petals);
        }
        finally
        {
            SettingsStore.Reloaded -= Reloaded;
        }
        Assert.Same(read, applied);
        Assert.Contains("this-pc", SettingsStore.Current.ApprovedPetals);
        Assert.DoesNotContain("petal-fingerprint", SettingsStore.Current.ApprovedPetals);
        Assert.True(File.Exists(Path.Combine(Petal.Recipes.CustomPetal.Folder, "mine.json")));
        Assert.True(File.Exists(SettingsStore.FilePath));
    }

    [Fact]
    public void Files_that_are_not_Petal_settings_are_refused()
    {
        var other = Path.Combine(_dir, "other.json");
        File.WriteAllText(other, """{ "format": "something-else" }""");
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(other));
        File.WriteAllText(other, "not json at all");
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(other));
        // A petal file name can't climb out of the petals folder.
        var sneaky = Path.Combine(_dir, "sneaky.petal");
        File.WriteAllText(sneaky, JsonSerializer.Serialize(new
        {
            format = "petal-settings",
            version = 1,
            settings = new { },
            petals = new[] { new { file = "..\\..\\evil.json", content = "{}" } },
        }));
        var (_, petals, _) = SettingsTransfer.Read(sneaky);
        Assert.All(petals, p => Assert.Equal(Path.GetFileName(p.File), p.File));
    }

    [Fact]
    public void A_settings_file_changed_by_hand_is_read_again_but_Petals_own_writes_are_not()
    {
        SettingsStore.Replace(new AppSettings());
        SettingsStore.Save();
        Assert.False(SettingsStore.ReadAgain());
        var json = File.ReadAllText(SettingsStore.FilePath).Replace("\"GrowPetal\": true", "\"GrowPetal\": false");
        File.WriteAllText(SettingsStore.FilePath, json);
        Assert.True(SettingsStore.ReadAgain());
        Assert.False(SettingsStore.Current.GrowPetal);
        // A file that doesn't read leaves the settings as they were.
        File.WriteAllText(SettingsStore.FilePath, "{ broken");
        Assert.False(SettingsStore.ReadAgain());
        Assert.False(SettingsStore.Current.GrowPetal);
    }

    // ---------- scripts ----------

    [Fact]
    public void A_PowerShell_script_gets_its_input_and_says_what_it_printed()
    {
        var entry = new WheelEntry { Kind = EntryKind.Script, Shell = ScriptShell.PowerShell, Input = ScriptInput.Clipboard, Script = "Write-Output ('got ' + $env:PETAL_INPUT.ToUpper())\nexit 0" };
        var (code, output, _) = ScriptRunner.Run(entry, "مرحبا petal", []);
        Assert.Equal(0, code);
        Assert.Contains("got مرحبا PETAL", output);
        var failing = new WheelEntry { Kind = EntryKind.Script, Script = "Write-Error 'no good'; exit 3" };
        var (failed, _, error) = ScriptRunner.Run(failing, "", []);
        Assert.Equal(3, failed);
        Assert.Contains("no good", error);
    }

    [Fact]
    public void A_Command_Prompt_script_gets_the_selected_files()
    {
        var file = Path.Combine(_dir, "a b.txt");
        File.WriteAllText(file, "x");
        var entry = new WheelEntry { Kind = EntryKind.Script, Shell = ScriptShell.Cmd, Input = ScriptInput.SelectedFiles, Script = "echo files=%PETAL_FILES%" };
        var (code, output, _) = ScriptRunner.Run(entry, "", [file]);
        Assert.Equal(0, code);
        Assert.Contains("files=" + file, output);
    }

    [Fact]
    public void Scripts_written_here_run_and_others_ask_first()
    {
        var mine = new WheelEntry { Kind = EntryKind.Script, Script = "Get-Date" };
        Assert.DoesNotContain(ScriptRunner.Fingerprint(mine), SettingsStore.Current.ApprovedPetals);
        ScriptRunner.Approve(mine);
        Assert.Contains(ScriptRunner.Fingerprint(mine), SettingsStore.Current.ApprovedPetals);
        // Changing the script, what it gets or its shell needs asking again.
        Assert.NotEqual(ScriptRunner.Fingerprint(mine), ScriptRunner.Fingerprint(new WheelEntry { Kind = EntryKind.Script, Script = "Get-Date; Remove-Item x" }));
        Assert.NotEqual(ScriptRunner.Fingerprint(mine), ScriptRunner.Fingerprint(new WheelEntry { Kind = EntryKind.Script, Script = "Get-Date", Shell = ScriptShell.Cmd }));
        Assert.NotEqual(ScriptRunner.Fingerprint(mine), ScriptRunner.Fingerprint(new WheelEntry { Kind = EntryKind.Script, Script = "Get-Date", Input = ScriptInput.Clipboard }));
    }

    // ---------- the actions wheel ----------

    [Fact]
    public void The_actions_wheel_shows_its_eleven_and_keeps_the_rest_under_More()
    {
        Assert.Equal(11, ActionWheel.Default.Count);
        Assert.Equal(ActionWheel.Default, ActionWheel.Parse(null));
        Assert.Equal([FileActionId.Copy, FileActionId.Zip], ActionWheel.Parse(["Copy", "Nothing", "Zip", "Copy"]));
        Assert.Empty(ActionWheel.Parse([]));
        // Every action is either on the wheel or under More, never both, never lost.
        var rest = FileActionRegistry.All.Select(a => a.Id).Except(ActionWheel.Default).ToList();
        Assert.Equal(FileActionRegistry.All.Count, ActionWheel.Default.Count + rest.Count);
        Assert.Contains(FileActionId.CopyName, rest);
        Assert.Contains(FileActionId.OpenWith, ActionWheel.Default);
    }

    [Fact]
    public void Open_With_finds_what_Windows_suggests()
    {
        var handlers = Petal.App.Actions.OpenWithWindow.Handlers(".txt");
        Assert.NotEmpty(handlers);
        Assert.All(handlers, h => Assert.False(string.IsNullOrWhiteSpace(h.Name)));
        Assert.Empty(Petal.App.Actions.OpenWithWindow.Handlers(""));
    }

    // ---------- clarity ----------

    [Fact]
    public void Conversions_this_PC_cannot_do_stay_on_the_wheel_dim_with_a_reason()
    {
        var empty = new ConverterRegistry();
        var missing = Availability.MissingConversions([F.Jpg], empty).ToList();
        Assert.NotEmpty(missing);
        Assert.All(missing, m =>
        {
            Assert.True(m.Blocked);
            Assert.Equal("Not available on this PC", m.Unavailable);
        });
        Assert.Contains(missing, m => m.Payload == F.Png);
        // Slides say what they need.
        Assert.All(Availability.MissingConversions([F.Pptx], empty), m => Assert.Equal("Needs Microsoft Office or LibreOffice", m.Unavailable));
        Assert.Empty(Availability.MissingConversions([], empty));
    }

    [Fact]
    public void Every_format_note_has_Arabic_and_writable_formats_have_one()
    {
        foreach (var note in FormatNotes.All)
            Assert.True(Petal.App.Localization.Arabic.Table.ContainsKey(note), note);
        foreach (var format in new[] { F.Jpg, F.Png, F.Webp, F.Heic, F.Avif, F.Pdf, F.Mp4, F.Mp3, F.Gif, F.Docx })
            Assert.NotNull(FormatNotes.For(format));
        Assert.Equal("Smallest, same look", WheelItem.From(F.Avif).Note);
    }

    [Fact]
    public void Practice_says_what_would_have_happened()
    {
        var photo = Path.Combine(_dir, "sunset.jpg");
        Assert.Equal("DropWheel would save sunset.jpg as PNG beside the original.", Petal.App.Onboarding.Practice.Describe(WheelItem.From(F.Png), [photo]));
        var tool = Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.ResizeImage);
        Assert.Equal("The Resize & Rotate window would open for sunset.jpg.", Petal.App.Onboarding.Practice.Describe(WheelItem.From(tool), [photo]));
        var entry = new WheelEntry { Kind = EntryKind.Action, Action = "system.lock" };
        Assert.Equal("“Lock” would run.", Petal.App.Onboarding.Practice.Describe(Rings.Item(entry), []));
    }
}
