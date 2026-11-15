using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Localization;

namespace Petal.App.Settings;

/// <summary>Settings for the screen and text tools, and for work and sharing.</summary>
public sealed partial class SettingsWindow
{
    private UIElement ScreenTextSection()
    {
        var panel = new StackPanel();

        panel.Children.Add(Text("Circle Anything", "Petal.Label"));
        panel.Children.Add(Text("Press the shortcut, then circle anything on the screen (a sentence, a number, a picture, a QR code): the wheel opens with what can be done with it. Everything happens on this PC.", "Petal.Caption").WithMargin(0, 4, 0, 0));
        panel.Children.Add(ShortcutRow(() => _s.CircleShortcut, v => _s.CircleShortcut = v, Screen.CircleWindow.RegisterShortcut, "Ctrl+Win+C"));

        panel.Children.Add(Text("Dictation", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(ShortcutRow(() => _s.DictationShortcut, v => _s.DictationShortcut = v, Words.Dictation.RegisterShortcut, "Ctrl+Win+J"));
        var accuracy = Segments([("Fast", "Fast"), ("Balanced", "Balanced"), ("Accurate", "Accurate")], _s.DictationAccuracy);
        accuracy.Changed += v =>
        {
            _s.DictationAccuracy = v;
            Save();
        };
        panel.Children.Add(Row("Accuracy", accuracy, 120, 8));
        var language = Choice(Languages(), _s.DictationLanguage);
        language.SelectionChanged += (_, _) =>
        {
            _s.DictationLanguage = ValueOf<string>(language);
            Save();
        };
        panel.Children.Add(Row("Language", language, 120, 8));
        var setup = new Button { Content = L.T("Speech model…"), HorizontalAlignment = HorizontalAlignment.Left };
        setup.Click += (_, _) => Words.DictationSetupWindow.ShowSingle();
        panel.Children.Add(Row("", setup, 120, 8));

        panel.Children.Add(Text("Live captions", "Petal.Label").WithMargin(0, 18, 0, 0));
        Words.LiveCaptions.Settle(_s);
        var show = Segments([("both", "Both"), ("translated", "Translation"), ("original", "As spoken")], _s.CaptionsShow);
        show.Changed += v =>
        {
            _s.CaptionsShow = v;
            Save();
        };
        panel.Children.Add(Row("Show", show, 120, 6));
        var captionsInto = new Tools.Parts.LanguageChoice(_s.CaptionsInto.Length > 0 ? _s.CaptionsInto : Tools.Parts.LanguageChoice.Interface);
        var captionsWay = new Tools.Parts.ModelPanel(Words.LiveCaptions.ModelsToOffer(_s.CaptionsLanguage, captionsInto.Value));
        var anyLanguage = Text(Petal.Recipes.Translation.GemmaTranslation.HasCard
            ? "One model translates from any language into any other."
            : "Without a graphics card, a small model made for the two languages translates. It is offered here once the language spoken is chosen, or the first time that language is heard.",
            "Petal.Caption").WithMargin(120, 4, 0, 0);
        void ShowWay()
        {
            captionsWay.PointAt(Words.LiveCaptions.ModelsToOffer(_s.CaptionsLanguage, captionsInto.Value));
        }
        captionsInto.Changed += code =>
        {
            _s.CaptionsInto = Tools.Parts.LanguageChoice.Remembered(code);
            Save();
            ShowWay();
        };
        panel.Children.Add(Row("Translate into", captionsInto, 120, 6));
        var spoken = Choice(Languages(), _s.CaptionsLanguage);
        spoken.SelectionChanged += (_, _) =>
        {
            _s.CaptionsLanguage = ValueOf<string>(spoken);
            Save();
            ShowWay();
        };
        panel.Children.Add(Row("Spoken in", spoken, 120, 8));
        panel.Children.Add(Text("From the PC's sound or the microphone (a button on the captions switches). A translation model downloads the first time it is needed.", "Petal.Caption").WithMargin(120, 4, 0, 0));
        panel.Children.Add(anyLanguage);
        panel.Children.Add(captionsWay);
        ShowWay();

        panel.Children.Add(Text("Showing clicks and keys", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Show every key", "Typing shows too, not only shortcuts. Leave it off when typing passwords on screen.", _s.ShowEveryKey, v => _s.ShowEveryKey = v, top: 6));

        panel.Children.Add(Text("Screen recording", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Record the PC's sound", "What plays through the speakers goes into the video.", _s.RecordSystemSound, v => _s.RecordSystemSound = v, top: 6));
        panel.Children.Add(Switch("Record the microphone", "Your voice goes into the video, mixed with the PC's sound.", _s.RecordMicrophone, v => _s.RecordMicrophone = v));
        panel.Children.Add(Switch("Show me from the camera", "Your webcam in a round bubble over the recording. Drag the bubble anywhere and scroll on it to resize it; the camera's picture never leaves this PC.", _s.RecordCamera, v => _s.RecordCamera = v));
        panel.Children.Add(Switch("Cinematic recordings", "When you stop, the recording opens in the studio: the view goes in where you click and type, the pointer glides, and the video sits on a background. Change any of it, then save.", _s.RecordPolish, v => _s.RecordPolish = v));
        var backgrounds = new WrapPanel();
        foreach (var (key, name, from, to) in Screen.Studio.StudioBackgrounds.Gradients.Append(("none", "None", SkiaSharp.SKColors.Black, SkiaSharp.SKColors.Black)))
        {
            var swatch = new Border
            {
                Width = 30,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = L.T(name),
                BorderThickness = new Thickness(key == _s.PolishBackground ? 2.5 : 1),
                Background = key == "none" ? Brushes.Black : new LinearGradientBrush(Color.FromRgb(from.Red, from.Green, from.Blue), Color.FromRgb(to.Red, to.Green, to.Blue), 45),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, key == _s.PolishBackground ? "Petal.Accent" : "Petal.ControlBorder");
            var chosen = key;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _s.PolishBackground = chosen;
                Screen.Studio.StudioSettings.FromSettings();
                Save();
                foreach (var other in backgrounds.Children.OfType<Border>())
                {
                    bool on = (string)other.Tag == chosen;
                    other.BorderThickness = new Thickness(on ? 2.5 : 1);
                    other.SetResourceReference(Border.BorderBrushProperty, on ? "Petal.Accent" : "Petal.ControlBorder");
                }
            };
            swatch.Tag = key;
            backgrounds.Children.Add(swatch);
        }
        var ownBackground = new Border
        {
            Width = 30,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = L.T("Any colour…"),
            Tag = _s.PolishBackground.StartsWith('#') ? _s.PolishBackground : "",
            BorderThickness = new Thickness(_s.PolishBackground.StartsWith('#') ? 2.5 : 1),
            Background = Tools.Parts.ColourMath.TryParseHex(_s.PolishBackground, out var ownColour) ? Visuals.Palette.Brush(ownColour) : Tools.Parts.CustomColourChip.Rainbow(),
        };
        ownBackground.SetResourceReference(Border.BorderBrushProperty, _s.PolishBackground.StartsWith('#') ? "Petal.Accent" : "Petal.ControlBorder");
        ownBackground.MouseLeftButtonUp += (_, _) => Tools.Parts.ColourWindow.Pick(Tools.Parts.ColourMath.TryParseHex(_s.PolishBackground, out var from) ? from : Colors.White, c =>
        {
            var hex = Tools.Parts.ColourMath.Hex(c);
            _s.PolishBackground = hex;
            ownBackground.Tag = hex;
            ownBackground.Background = Visuals.Palette.Brush(c);
            Screen.Studio.StudioSettings.FromSettings();
            Save();
            foreach (var other in backgrounds.Children.OfType<Border>())
            {
                bool on = (string)other.Tag == hex;
                other.BorderThickness = new Thickness(on ? 2.5 : 1);
                other.SetResourceReference(Border.BorderBrushProperty, on ? "Petal.Accent" : "Petal.ControlBorder");
            }
        }, owner: this);
        backgrounds.Children.Add(ownBackground);
        panel.Children.Add(Row("Background", backgrounds, 150, 10));
        var zoom = Segments([(1.0, "Off"), (1.5, "Gentle"), (1.8, "Medium"), (2.3, "Strong")], _s.PolishZoom switch { <= 1.01 => 1.0, < 1.7 => 1.5, < 2.1 => 1.8, _ => 2.3 });
        zoom.Changed += v =>
        {
            _s.PolishZoom = v;
            Save();
        };
        panel.Children.Add(Row("Zoom on clicks and typing", zoom, 150, 8));
        panel.Children.Add(Switch("A larger pointer", "Easier to follow in the video.", _s.PolishLargeCursor, v =>
        {
            _s.PolishLargeCursor = v;
            Screen.Studio.StudioSettings.FromSettings();
        }));
        panel.Children.Add(Switch("Rings at clicks", "A ring shows where each click was.", _s.PolishClickRings, v =>
        {
            _s.PolishClickRings = v;
            Screen.Studio.StudioSettings.FromSettings();
        }));
        panel.Children.Add(Switch("Show clicks and shortcuts in recordings", "Rings where you click and the keys you press appear in the video.", _s.RecordShowsClicks, v => _s.RecordShowsClicks = v));
        var folder = new Button { Content = L.T("Open the recordings folder"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Screen.ScreenRecorder.Folder);
            Process.Start(new ProcessStartInfo(Screen.ScreenRecorder.Folder) { UseShellExecute = true });
        };
        panel.Children.Add(folder);

        panel.Children.Add(Text("Clipboard history", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Keep a clipboard history", "What you copy (text, pictures and files) can be pasted again from the Clipboard wheel. Passwords that apps mark as private are never kept.", _s.KeepClipboardHistory, v => _s.KeepClipboardHistory = v, top: 6));
        panel.Children.Add(Switch("Remember it after DropWheel closes", "Kept on this PC beside the settings. Pinned clips are always remembered.", _s.RememberClipboardHistory, v => _s.RememberClipboardHistory = v));
        var clear = new Button { Content = L.T("Clear the history"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        clear.Click += (_, _) =>
        {
            Clips.ClipboardHistory.Clear(keepPinned: false);
            clear.Content = L.T("Cleared");
        };
        panel.Children.Add(clear);
        return panel;
    }

    private UIElement WorkSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Command palette", "Petal.Label"));
        panel.Children.Add(ShortcutRow(() => _s.PaletteShortcut, v => _s.PaletteShortcut = v, Commands.CommandPalette.RegisterShortcut, "Ctrl+Win+K"));
        panel.Children.Add(Text("Voice commands", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Text("Press the shortcut and say what you want (\"record the screen\", \"focus for 25 minutes\"); the command palette opens with it, ready for Enter. Heard on this PC by the dictation model.", "Petal.Caption").WithMargin(0, 4, 0, 0));
        panel.Children.Add(ShortcutRow(() => _s.VoiceShortcut, v => _s.VoiceShortcut = v, Words.VoiceCommand.RegisterShortcut, "Ctrl+Win+M"));

        panel.Children.Add(Text("Snippets", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Expand snippets as I type", "An abbreviation such as ;addr becomes its text in any app. DropWheel holds only the last few letters typed, in memory, to spot one; nothing is kept or sent.", _s.SnippetsOn, v => _s.SnippetsOn = v, top: 6));
        var snippets = new Button { Content = L.T("Edit Snippets…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        snippets.Click += (_, _) => new SnippetsWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        panel.Children.Add(snippets);

        panel.Children.Add(Text("Workspaces", "Petal.Label").WithMargin(0, 18, 0, 0));
        var workspaces = new StackPanel();
        void FillWorkspaces()
        {
            workspaces.Children.Clear();
            if (_s.Workspaces.Count == 0)
                workspaces.Children.Add(Text("None yet. Arrange your apps, then choose Save Workspace on the Work wheel.", "Petal.Caption"));
            foreach (var workspace in _s.Workspaces.ToList())
            {
                var item = workspace;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var remove = IconButton("trash", "Delete", 14);
                remove.Click += (_, _) =>
                {
                    _s.Workspaces.Remove(item);
                    Save();
                    FillWorkspaces();
                };
                var open = IconButton("play", "Open", 14);
                open.Click += async (_, _) => await Screen.Workspaces.OpenAsync(item);
                DockPanel.SetDock(remove, Dock.Right);
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(open);
                row.Children.Add(Text(item.Name + " · " + L.P(item.Windows.Count, "{0} window", "{0} windows"), "Petal.Body"));
                workspaces.Children.Add(row);
            }
        }
        FillWorkspaces();
        panel.Children.Add(workspaces);
        var saveNow = new Button { Content = L.T("Save the Windows Open Now…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        saveNow.Click += (_, _) => Screen.SaveWorkspaceWindow.ShowSingle();
        panel.Children.Add(saveNow);

        panel.Children.Add(Text("Quick Look", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Space shows the selected file", "In File Explorer and on the desktop: a picture, a page of a PDF, a video or a song, a document's words, what a ZIP or a folder holds, a font. Space or Esc closes it, and the arrow keys move along.", _s.QuickLook, v => _s.QuickLook = v));
        if (!Shell.Packaged.IsPackaged)
            panel.Children.Add(Switch("Thumbnails in File Explorer", "Photoshop, Illustrator, EPS, InDesign, Krita and OpenRaster files show their picture, for file types no other program draws.", _s.ExplorerThumbnails, v =>
            {
                _s.ExplorerThumbnails = v;
                _ = Task.Run(() => Shell.ExplorerThumbnails.Apply(v));
            }));
        panel.Children.Add(Text("Other programs", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Let other programs run petals", "Stream Deck, AutoHotkey, Power Automate, scripts and the petal command can run DropWheel's own petals and open its wheels, for this Windows user only. Your own scripts on the wheels can't be run this way.", _s.LocalApi, v => _s.LocalApi = v));
        var deck = System.IO.Path.Combine(AppContext.BaseDirectory, "integrations", "Petal.streamDeckPlugin");
        if (System.IO.File.Exists(deck))
        {
            var add = new Button { Content = L.T("Add to Stream Deck…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), ToolTip = L.T("Keys that run petals, open wheels and the command palette. Needs Stream Deck 6.4 or later.") };
            add.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(deck) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Log.Error(ex, "Opening the Stream Deck plugin");
                }
            };
            panel.Children.Add(add);
        }
        panel.Children.Add(Text("Other PCs", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Let other PCs send me files", "PCs with DropWheel on the same network can send files with Send to PC. DropWheel asks every time, and saves them in Downloads\\DropWheel.", _s.ReceiveFromPcs, v =>
        {
            _s.ReceiveFromPcs = v;
            _ = Actions.PcDrop.ApplyAsync(v);
        }, top: 6));
        panel.Children.Add(Text(L.F("Other PCs see this one as “{0}”.", Actions.PcDrop.Name), "Petal.Caption").WithMargin(0, 6, 0, 0));
        return panel;
    }

    private static List<(string, string)> Languages()
    {
        var list = new List<(string, string)> { ("auto", "Detect automatically"), ("ar", "العربية"), ("en", "English") };
        list.AddRange(Petal.Engines.Media.Transcriber.Languages.Where(c => c is not "ar" and not "en").Select(c => (c, System.Globalization.CultureInfo.GetCultureInfo(c).NativeName)));
        return list;
    }

    /// <summary>A shortcut row: click, then press the keys; with the default back and off.</summary>
    private UIElement ShortcutRow(Func<string> get, Action<string> set, Func<bool> register, string usual)
    {
        var warning = Text("", "Petal.Caption");
        warning.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        warning.Visibility = Visibility.Collapsed;
        var saved = Shortcut.Parse(get());
        bool recording = false;
        var record = new Button { MinWidth = 180, ToolTip = L.T("Click, then press the keys you want.") };
        void Show() => record.Content = recording ? L.T("Press the new shortcut…") : saved?.Display ?? L.T("Off");
        void Warn(string? text)
        {
            warning.Text = text ?? "";
            warning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
        void Apply(Shortcut? shortcut)
        {
            recording = false;
            var before = get();
            set(shortcut?.ToString() ?? "");
            if (register())
            {
                saved = shortcut;
                Save();
                Warn(null);
            }
            else
            {
                set(before);
                register();
                Warn(L.F("Another program already uses {0}. Choose a different shortcut.", shortcut!.Display));
            }
            Show();
        }
        record.Click += (_, _) =>
        {
            if (recording) return;
            recording = true;
            Warn(null);
            Show();
            record.Focus();
        };
        record.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                recording = false;
                Show();
                return;
            }
            if (Shortcut.IsModifier(key) || key is Key.ImeProcessed or Key.DeadCharProcessed)
                return;
            var candidate = new Shortcut(Keyboard.Modifiers, key);
            if (!candidate.IsValid)
            {
                Warn(L.T("Use Ctrl, Alt or Windows together with another key."));
                return;
            }
            Apply(candidate);
        };
        record.LostKeyboardFocus += (_, _) =>
        {
            if (!recording) return;
            recording = false;
            Show();
        };
        var reset = IconButton("rotateCcw", "Restore the default shortcut", 14);
        reset.Click += (_, _) => Apply(Shortcut.Parse(usual));
        var off = IconButton("x", "Turn the shortcut off", 14);
        off.Click += (_, _) => Apply(null);
        Show();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { record, reset.WithMargin(6, 0, 0, 0), off.WithMargin(2, 0, 0, 0) } };
        return new StackPanel { Children = { Row("Shortcut", row, 120, 8), warning.WithMargin(120, 4, 0, 0) } };
    }
}
