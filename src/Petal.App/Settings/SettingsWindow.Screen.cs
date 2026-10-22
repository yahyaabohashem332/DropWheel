using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Petal.App.Input;
using Petal.App.Localization;

namespace Petal.App.Settings;

/// <summary>Settings for the screen and text tools: dictation, showing keys, recording, and the clipboard history.</summary>
public sealed partial class SettingsWindow
{
    private UIElement ScreenTextSection()
    {
        var panel = new StackPanel();

        panel.Children.Add(Text("Dictation", "Petal.Label"));
        var warning = Text("", "Petal.Caption");
        warning.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        warning.Visibility = Visibility.Collapsed;
        panel.Children.Add(Row("Shortcut", DictationShortcutPicker(warning), 120, 8));
        panel.Children.Add(warning.WithMargin(120, 4, 0, 0));
        var accuracy = Segments([("Fast", "Fast"), ("Balanced", "Balanced"), ("Accurate", "Accurate")], _s.DictationAccuracy);
        accuracy.Changed += v =>
        {
            _s.DictationAccuracy = v;
            Save();
        };
        panel.Children.Add(Row("Accuracy", accuracy, 120, 8));
        var languages = new List<(string, string)> { ("auto", "Detect automatically"), ("ar", "العربية"), ("en", "English") };
        languages.AddRange(Petal.Engines.Media.Transcriber.Languages.Where(c => c is not "ar" and not "en").Select(c => (c, System.Globalization.CultureInfo.GetCultureInfo(c).NativeName)));
        var language = Choice(languages, _s.DictationLanguage);
        language.SelectionChanged += (_, _) =>
        {
            _s.DictationLanguage = ValueOf<string>(language);
            Save();
        };
        panel.Children.Add(Row("Language", language, 120, 8));
        var setup = new Button { Content = L.T("Speech model…"), HorizontalAlignment = HorizontalAlignment.Left };
        setup.Click += (_, _) => Words.DictationSetupWindow.ShowSingle();
        panel.Children.Add(Row("", setup, 120, 8));

        panel.Children.Add(Text("Showing clicks and keys", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Show every key", "Typing shows too, not only shortcuts. Leave it off when typing passwords on screen.", _s.ShowEveryKey, v => _s.ShowEveryKey = v, top: 6));

        panel.Children.Add(Text("Screen recording", "Petal.Label").WithMargin(0, 18, 0, 0));
        panel.Children.Add(Switch("Record the PC's sound", "What plays through the speakers goes into the video.", _s.RecordSystemSound, v => _s.RecordSystemSound = v, top: 6));
        panel.Children.Add(Switch("Record the microphone", "Your voice goes into the video, mixed with the PC's sound.", _s.RecordMicrophone, v => _s.RecordMicrophone = v));
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
        panel.Children.Add(Switch("Remember it after Petal closes", "Kept on this PC beside the settings. Pinned clips are always remembered.", _s.RememberClipboardHistory, v => _s.RememberClipboardHistory = v));
        var clear = new Button { Content = L.T("Clear the history"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        clear.Click += (_, _) =>
        {
            Clips.ClipboardHistory.Clear(keepPinned: false);
            clear.Content = L.T("Cleared");
        };
        panel.Children.Add(clear);
        return panel;
    }

    /// <summary>The dictation shortcut: click, then press the keys.</summary>
    private UIElement DictationShortcutPicker(TextBlock warning)
    {
        var saved = Shortcut.Parse(_s.DictationShortcut);
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
            var before = _s.DictationShortcut;
            _s.DictationShortcut = shortcut?.ToString() ?? "";
            if (Words.Dictation.RegisterShortcut())
            {
                saved = shortcut;
                Save();
                Warn(null);
            }
            else
            {
                _s.DictationShortcut = before;
                Words.Dictation.RegisterShortcut();
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
        reset.Click += (_, _) => Apply(Shortcut.Parse("Ctrl+Win+J"));
        var off = IconButton("x", "Turn the shortcut off", 14);
        off.Click += (_, _) => Apply(null);
        Show();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { record, reset.WithMargin(6, 0, 0, 0), off.WithMargin(2, 0, 0, 0) } };
    }
}
