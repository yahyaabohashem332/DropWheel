using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Visuals;
using Petal.Core.Text;

namespace Petal.App.Notes;

/// <summary>One note: its words, colour, place and size on the screen, and whether it stays on top.</summary>
public sealed class StickyNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Text { get; set; } = "";
    public string Colour { get; set; } = "yellow";
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 220;
    public bool OnTop { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>
/// Sticky notes on the desktop: written in any language (Arabic runs right to left by itself), in six
/// colours, moved and sized freely, kept on top when wanted, and back where they were when Petal starts.
/// Kept in notes.json beside the settings.
/// </summary>
public static class StickyNotes
{
    public static readonly (string Key, Color Paper, Color Band)[] Colours =
    [
        ("yellow", Color.FromRgb(0xFF, 0xF4, 0xA8), Color.FromRgb(0xF7, 0xE2, 0x6B)),
        ("green", Color.FromRgb(0xCF, 0xF5, 0xC8), Color.FromRgb(0xA6, 0xE5, 0x9B)),
        ("blue", Color.FromRgb(0xC9, 0xE6, 0xFF), Color.FromRgb(0x9E, 0xCF, 0xFB)),
        ("pink", Color.FromRgb(0xFF, 0xD6, 0xE7), Color.FromRgb(0xFB, 0xB1, 0xD0)),
        ("purple", Color.FromRgb(0xE4, 0xD8, 0xFF), Color.FromRgb(0xCA, 0xB5, 0xFC)),
        ("grey", Color.FromRgb(0xEC, 0xEC, 0xEE), Color.FromRgb(0xD4, 0xD4, 0xD8)),
    ];

    private static readonly List<StickyNote> Notes = [];
    private static readonly Dictionary<string, NoteWindow> Open = [];
    private static DispatcherTimer? _save;
    private static bool _loaded;

    private static string FilePath => Path.Combine(SettingsStore.Directory, "notes.json");

    public static int Count => Notes.Count;

    public static bool Showing => Open.Count > 0;

    /// <summary>At start: the notes come back where they were, unless they were tucked away.</summary>
    public static void Restore()
    {
        Load();
        if (!SettingsStore.Current.NotesHidden)
            foreach (var note in Notes)
                Show(note, focus: false);
    }

    private static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                Notes.AddRange(JsonSerializer.Deserialize<List<StickyNote>>(File.ReadAllText(FilePath), SettingsStore.Options) ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Reading the notes");
        }
    }

    /// <summary>A new note, near the pointer.</summary>
    public static void New()
    {
        Load();
        GetCursorPosLocal(out var at);
        var colour = Colours[Notes.Count % Colours.Length].Key;
        var note = new StickyNote { Colour = colour, Left = at.X - 120, Top = at.Y - 40 };
        Notes.Add(note);
        if (SettingsStore.Current.NotesHidden)
        {
            SettingsStore.Current.NotesHidden = false;
            SettingsStore.Save();
            foreach (var other in Notes.Where(n => n != note))
                Show(other, focus: false);
        }
        Show(note, focus: true);
        SaveSoon();
    }

    /// <summary>Shows every note, or tucks them all away (they stay saved).</summary>
    public static void ToggleAll()
    {
        Load();
        if (Showing)
        {
            foreach (var window in Open.Values.ToList())
                window.Close();
            SettingsStore.Current.NotesHidden = true;
        }
        else
        {
            if (Notes.Count == 0)
            {
                New();
                return;
            }
            foreach (var note in Notes)
                Show(note, focus: false);
            SettingsStore.Current.NotesHidden = false;
        }
        SettingsStore.Save();
    }

    private static void Show(StickyNote note, bool focus)
    {
        if (Open.TryGetValue(note.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new NoteWindow(note);
        Open[note.Id] = window;
        window.Closed += (_, _) => Open.Remove(note.Id);
        window.Show();
        if (focus)
            window.FocusText();
    }

    internal static void Delete(StickyNote note)
    {
        Notes.Remove(note);
        if (Open.TryGetValue(note.Id, out var window))
            window.Close();
        SaveSoon();
        if (note.Text.Trim().Length > 0)
        {
            var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "");
            card.Succeed(L.T("Note deleted"), L.T("Click to bring it back"), () =>
            {
                Notes.Add(note);
                Show(note, focus: false);
                SaveSoon();
            });
        }
    }

    internal static void SaveSoon()
    {
        _save ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _save!.Stop();
            Save();
        }, Application.Current.Dispatcher);
        _save.Stop();
        _save.Start();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Notes, SettingsStore.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving the notes");
        }
    }

    private static void GetCursorPosLocal(out Point at)
    {
        Interop.NativeMethods.GetCursorPos(out var p);
        var monitor = ScreenNative.UnderPointer();
        at = new Point(p.X / monitor.Scale, p.Y / monitor.Scale);
    }

    internal static (Color Paper, Color Band) Paint(string key)
    {
        if (Tools.Parts.ColourMath.TryParseHex(key, out var own))
            return (own, Darker(own));
        var c = Colours.FirstOrDefault(c => c.Key == key);
        return c.Key is null ? (Colours[0].Paper, Colours[0].Band) : (c.Paper, c.Band);
    }

    /// <summary>What a note keeps for a colour: the name of a ready-made one, or its hex code.</summary>
    internal static string KeyOf(Color paper)
    {
        var c = Colours.FirstOrDefault(c => c.Paper == paper);
        return c.Key ?? Tools.Parts.ColourMath.Hex(paper);
    }

    private static Color Darker(Color c) => Color.FromRgb((byte)(c.R * 0.86), (byte)(c.G * 0.86), (byte)(c.B * 0.86));
}

/// <summary>A note on the screen: a coloured paper with a band to drag it by and its buttons on the band.</summary>
internal sealed class NoteWindow : Window
{
    private readonly StickyNote _note;
    private readonly Border _paper, _band;
    private readonly TextBox _text;
    private readonly StackPanel _buttons;
    private readonly ContentControl _pin = new();

    public NoteWindow(StickyNote note)
    {
        _note = note;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Topmost = note.OnTop;
        Title = L.T("Note");
        MinWidth = 150;
        MinHeight = 110;
        Width = note.Width;
        Height = note.Height;
        if (!double.IsNaN(note.Left))
        {
            Left = note.Left;
            Top = note.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _text = new TextBox
        {
            Text = note.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x26, 0x1C)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x26, 0x1C)),
            FontFamily = UiFonts.Text,
            FontSize = 15,
            Padding = new Thickness(12, 8, 12, 14),
        };
        _text.TextChanged += (_, _) =>
        {
            _note.Text = _text.Text;
            Direction();
            StickyNotes.SaveSoon();
        };
        Direction();
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.35 };
        _buttons.Children.Add(BandButton("plus", "New note", StickyNotes.New));
        _buttons.Children.Add(BandButton("palette", "Colour", ChooseColour));
        _pin.Content = Icons.Create("pin", 13, new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 2);
        var pin = BandButton(null, "Keep on top", ToggleOnTop, _pin);
        _buttons.Children.Add(pin);
        _buttons.Children.Add(BandButton("trash", "Delete note", () => StickyNotes.Delete(_note)));
        _band = new Border { Height = 28, CornerRadius = new CornerRadius(10, 10, 0, 0), Child = _buttons, Cursor = Cursors.SizeAll };
        Screen.ScreenUi.DragFromBackground(this, _band);
        var layout = new DockPanel();
        DockPanel.SetDock(_band, Dock.Top);
        layout.Children.Add(_band);
        layout.Children.Add(_text);
        _paper = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(8),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.28 },
        };
        Content = _paper;
        MouseEnter += (_, _) => _buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        MouseLeave += (_, _) => _buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, TimeSpan.FromMilliseconds(200)));
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
        Recolour();
        ShowPin();
    }

    public void FocusText() => Dispatcher.BeginInvoke(() =>
    {
        Activate();
        _text.Focus();
        _text.CaretIndex = _text.Text.Length;
    }, DispatcherPriority.Input);

    private void Direction()
    {
        var first = _text.Text.FirstOrDefault(char.IsLetter);
        _text.FlowDirection = first == default ? L.Direction : KeyboardLayout.LooksArabic(first.ToString()) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private Border BandButton(string? icon, string tip, Action click, UIElement? face = null)
    {
        var button = new Border
        {
            Width = 26,
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = L.T(tip),
            Child = face ?? Icons.Create(icon!, 13, new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 2),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(tip));
        if (button.Child is FrameworkElement f)
        {
            f.HorizontalAlignment = HorizontalAlignment.Center;
            f.VerticalAlignment = VerticalAlignment.Center;
        }
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    /// <summary>The note's own colours and any other, from the colour panel; the note changes as you choose.</summary>
    private void ChooseColour()
    {
        void Show(Color paper)
        {
            _note.Colour = StickyNotes.KeyOf(paper);
            Recolour();
        }
        Tools.Parts.ColourWindow.Pick(StickyNotes.Paint(_note.Colour).Paper, paper =>
        {
            Show(paper);
            StickyNotes.SaveSoon();
        }, Show, StickyNotes.Colours.Select(c => c.Paper).ToList(), this);
    }

    private void Recolour()
    {
        var (paper, band) = StickyNotes.Paint(_note.Colour);
        _paper.Background = new SolidColorBrush(paper);
        _band.Background = new SolidColorBrush(band);
        // Dark paper takes light words.
        var ink = new SolidColorBrush(Tools.Parts.ColourMath.Readable(paper) == Colors.White ? Colors.White : Color.FromRgb(0x2A, 0x26, 0x1C));
        _text.Foreground = ink;
        _text.CaretBrush = ink;
    }

    private void ToggleOnTop()
    {
        _note.OnTop = !_note.OnTop;
        Topmost = _note.OnTop;
        ShowPin();
        StickyNotes.SaveSoon();
    }

    private void ShowPin() => _pin.Opacity = _note.OnTop ? 1 : 0.4;

    private void Remember()
    {
        if (WindowState != WindowState.Normal || !IsLoaded)
            return;
        _note.Left = Left;
        _note.Top = Top;
        _note.Width = ActualWidth;
        _note.Height = ActualHeight;
        StickyNotes.SaveSoon();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            StickyNotes.New();
            e.Handled = true;
        }
    }
}
