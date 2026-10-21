using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Visuals;
using Petal.App.Wheels;

namespace Petal.App.Settings;

/// <summary>The colours a petal can have, and none.</summary>
internal static class PetalTints
{
    public static readonly (string? Hex, string Name)[] All =
    [
        (null, "No colour"),
        ("#E5484D", "Red"), ("#F76B15", "Orange"), ("#E8A317", "Amber"), ("#30A46C", "Green"), ("#12A594", "Teal"),
        ("#0090FF", "Blue"), ("#3E63DD", "Indigo"), ("#8E4EC6", "Violet"), ("#D6409F", "Pink"), ("#8B8D98", "Grey"),
    ];

    /// <summary>A row of colour dots; the chosen one has a ring. <paramref name="chosen"/> gets the new colour, or null for none.</summary>
    public static WrapPanel Picker(string? current, Action<string?> chosen)
    {
        var row = new WrapPanel();
        var rings = new List<(string? Hex, Ellipse Ring)>();
        foreach (var (hex, name) in All)
        {
            var swatch = new Grid { Width = 24, Height = 24 };
            var ring = new Ellipse { StrokeThickness = 2, Visibility = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Hidden };
            ring.SetResourceReference(Shape.StrokeProperty, "Petal.Text");
            swatch.Children.Add(ring);
            if (hex is null)
            {
                var none = new Ellipse { Margin = new Thickness(5), StrokeThickness = 1.2, StrokeDashArray = [2, 1.5] };
                none.SetResourceReference(Shape.StrokeProperty, "Petal.TextSecondary");
                swatch.Children.Add(none);
            }
            else
            {
                swatch.Children.Add(new Ellipse { Margin = new Thickness(4), Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) });
            }
            var button = new Button { Content = swatch, Style = (Style)Application.Current.FindResource("Petal.IconButton"), ToolTip = L.T(name), Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0) };
            System.Windows.Automation.AutomationProperties.SetName(button, L.T(name));
            rings.Add((hex, ring));
            button.Click += (_, _) =>
            {
                foreach (var (h, r) in rings)
                    r.Visibility = h == hex ? Visibility.Visible : Visibility.Hidden;
                chosen(hex);
            };
            row.Children.Add(button);
        }
        return row;
    }
}

/// <summary>A list of petals to put in order, change, remove and add to: a wheel's, or a group's.</summary>
internal sealed class EntryList : StackPanel
{
    private readonly List<WheelEntry> _entries;
    private readonly int _most;
    private readonly Window _owner;
    private readonly StackPanel _rows = new();
    private readonly Button _add;
    private readonly TextBlock _note;

    public EntryList(List<WheelEntry> entries, int most, Window owner)
    {
        _entries = entries;
        _most = most;
        _owner = owner;
        Children.Add(_rows);
        _add = new Button { Content = L.T("Add a Petal…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _add.Click += (_, _) =>
        {
            if (PetalPickerWindow.Pick(_owner) is { } entry)
            {
                _entries.Add(entry);
                Fill();
            }
        };
        Children.Add(_add);
        _note = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        Children.Add(_note);
        Fill();
    }

    private void Fill()
    {
        _rows.Children.Clear();
        _add.IsEnabled = _entries.Count < _most;
        _note.Text = L.F("Up to {0} petals.", _most);
        if (_entries.Count == 0)
            _rows.Children.Add(new TextBlock { Text = L.T("No petals yet."), Margin = new Thickness(0, 2, 0, 2) });
        for (int index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            int i = index;
            var item = Rings.Item(entry);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement icon = item.Picture is { } picture
                ? new Image { Source = picture, Width = 18, Height = 18 }
                : ToolWindowParts.Icon(item.Icon ?? "puzzle", 16, entry.Tint);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = L.T(item.Name), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = Describe(entry, item), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, item.Unavailable is null ? "Petal.TextSecondary" : "Petal.Danger");
            words.Children.Add(detail);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = ToolWindowParts.IconButton("chevronUp", "Move up");
            up.IsEnabled = i > 0;
            up.Click += (_, _) =>
            {
                _entries.RemoveAt(i);
                _entries.Insert(i - 1, entry);
                Fill();
            };
            var edit = ToolWindowParts.IconButton("pencil", "Change");
            edit.Click += (_, _) =>
            {
                var copy = Copy(entry);
                if (new EntryEditorWindow(copy) { Owner = _owner, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog() == true)
                {
                    _entries[i] = copy;
                    Fill();
                }
            };
            var remove = ToolWindowParts.IconButton("trash", "Remove");
            remove.Click += (_, _) =>
            {
                _entries.RemoveAt(i);
                Fill();
            };
            buttons.Children.Add(up);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            _rows.Children.Add(row);
        }
    }

    private static string Describe(WheelEntry entry, Overlay.WheelItem item)
    {
        if (item.Unavailable is { } why)
            return L.T(why);
        return entry.Kind switch
        {
            EntryKind.Action => L.T(WheelActions.Get(entry.Action)?.Description ?? ""),
            EntryKind.App => entry.Path ?? "",
            EntryKind.Keystroke => item.Note ?? "",
            EntryKind.Script => L.T(entry.Shell == ScriptShell.Cmd ? "Command Prompt" : "PowerShell") + " · " + L.T(ConfirmScriptWindow.InputName(entry.Input)),
            EntryKind.Folder or EntryKind.Link => entry.Path ?? "",
            EntryKind.Group => L.P(entry.Children?.Count ?? 0, "{0} petal", "{0} petals"),
            EntryKind.RunningApps => L.T("The windows that are open, to switch to one"),
            _ => "",
        };
    }

    internal static WheelEntry Copy(WheelEntry entry) =>
        System.Text.Json.JsonSerializer.Deserialize<WheelEntry>(System.Text.Json.JsonSerializer.Serialize(entry, SettingsStore.Options), SettingsStore.Options)!;
}

/// <summary>Small building blocks the editors share with the tool windows.</summary>
internal static class ToolWindowParts
{
    public static Button IconButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("Petal.IconButton"),
            ToolTip = L.T(tooltip),
            Content = ToolWindow.ThemedIcon(icon, 15),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(tooltip));
        return button;
    }

    /// <summary>An icon in the petal's colour, or the text colour without one.</summary>
    public static FrameworkElement Icon(string icon, double size, string? tint)
    {
        if (Rings.Colour(tint) is { } colour)
            return Icons.Create(icon, size, new SolidColorBrush(colour), 2);
        return ToolWindow.ThemedIcon(icon, size);
    }
}

/// <summary>A wheel's name, the apps it belongs to, its own shortcut, and its petals.</summary>
internal sealed class WheelEditorWindow : ToolWindow
{
    public const int MostPetals = 12;

    public WheelEditorWindow(UserWheel wheel) : base("Wheel", 540)
    {
        var name = new TextBox { Text = wheel.Name };
        Body.Children.Add(Row("Name", name, 110, 0));
        TextBox? apps = null;
        if (wheel.Kind == WheelKind.App)
        {
            apps = new TextBox { Text = string.Join(", ", wheel.Apps), FlowDirection = FlowDirection.LeftToRight };
            Body.Children.Add(Row("Apps", apps, 110, 8));
            Body.Children.Add(Text("Program names without .exe, separated by commas: chrome, msedge. The wheel comes first while one of them is in front.", "Petal.Caption").WithMargin(110, 4, 0, 0));
        }
        var shortcut = new ShortcutBox(wheel.Shortcut);
        Body.Children.Add(Row("Own shortcut", shortcut, 110, 8));
        Body.Children.Add(Text("Opens straight onto this wheel. Optional.", "Petal.Caption").WithMargin(110, 4, 0, 0));
        Body.Children.Add(Text("Petals", "Petal.Section").WithMargin(0, 16, 0, 6));
        var items = wheel.Items.Select(EntryList.Copy).ToList();
        Body.Children.Add(new ScrollViewer { Content = new EntryList(items, MostPetals, this), MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.Click += (_, _) =>
        {
            wheel.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : wheel.Name;
            if (apps is not null)
                wheel.Apps = apps.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            wheel.Shortcut = shortcut.Value;
            wheel.Items = items;
            foreach (var script in Flatten(items).Where(e => e.Kind == EntryKind.Script))
                ScriptRunner.Approve(script);
            DialogResult = true;
        };
    }

    private static IEnumerable<WheelEntry> Flatten(IEnumerable<WheelEntry> entries) =>
        entries.SelectMany(e => e.Children is { } children ? Flatten(children).Prepend(e) : [e]);
}

/// <summary>A box that records a shortcut: click it and press the keys; Esc or the cross clears it.</summary>
internal sealed class ShortcutBox : DockPanel
{
    private readonly Button _record = new() { MinWidth = 160 };
    private bool _recording;

    public string? Value { get; private set; }

    public ShortcutBox(string? value)
    {
        Value = Input.Shortcut.Parse(value)?.ToString();
        var clear = ToolWindowParts.IconButton("x", "No shortcut");
        clear.Click += (_, _) => { Value = null; Show(); };
        DockPanel.SetDock(clear, Dock.Right);
        Children.Add(clear);
        _record.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(_record);
        _record.Click += (_, _) =>
        {
            _recording = true;
            App.Current?.Hotkey?.Suspend();
            Show();
            _record.Focus();
        };
        _record.PreviewKeyDown += (_, e) =>
        {
            if (!_recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                Stop();
                return;
            }
            if (Input.Shortcut.IsModifier(key)) return;
            var candidate = new Input.Shortcut(Keyboard.Modifiers, key);
            if (candidate.IsValid)
            {
                Value = candidate.ToString();
                Stop();
            }
        };
        _record.LostKeyboardFocus += (_, _) => { if (_recording) Stop(); };
        Show();
    }

    private void Stop()
    {
        _recording = false;
        App.Current?.Hotkey?.Resume();
        Show();
    }

    private void Show() => _record.Content = _recording ? L.T("Press the new shortcut…") : Input.Shortcut.Parse(Value)?.Display ?? L.T("None");
}

/// <summary>One petal: its name and label, icon and colour, and what it does.</summary>
internal sealed class EntryEditorWindow : ToolWindow
{
    public EntryEditorWindow(WheelEntry entry) : base("Petal", 500)
    {
        var item = Rings.Item(entry);
        var name = new TextBox { Text = entry.Name ?? "" };
        Body.Children.Add(Row("Name", name, 110, 0));
        Body.Children.Add(Text(L.F("Leave empty for “{0}”.", L.T(item.Name)), "Petal.Caption").WithMargin(110, 4, 0, 0));
        var label = new TextBox { Text = entry.Label ?? "" };
        Body.Children.Add(Row("On the petal", label, 110, 8));
        string? icon = entry.Icon;
        var iconButton = new Button { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
        void ShowIcon() => iconButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { ThemedIcon(icon ?? item.Icon ?? "puzzle", 16), new TextBlock { Text = L.T(icon is null ? "Usual icon" : "Change…"), Margin = new Thickness(8, 0, 0, 0) } },
        };
        ShowIcon();
        iconButton.Click += (_, _) => IconPicker.Open(iconButton, chosen => { icon = chosen; ShowIcon(); });
        Body.Children.Add(Row("Icon", iconButton, 110, 8));
        string? tint = entry.Tint;
        Body.Children.Add(Row("Colour", PetalTints.Picker(tint, t => tint = t), 110, 8));

        Func<bool>? apply = null;
        switch (entry.Kind)
        {
            case EntryKind.Action:
                Body.Children.Add(Text(L.T(WheelActions.Get(entry.Action)?.Description ?? ""), "Petal.Caption").WithMargin(110, 12, 0, 0));
                break;
            case EntryKind.App:
            {
                var path = new TextBox { Text = entry.Path ?? "", FlowDirection = FlowDirection.LeftToRight };
                var browse = new Button { Content = L.T("Browse…"), Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Title = L.T("A program"), Filter = $"{L.T("Programs")} (*.exe;*.lnk)|*.exe;*.lnk" };
                    if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName;
                };
                Body.Children.Add(Row("Program", WithButton(path, browse), 110, 12));
                var arguments = new TextBox { Text = entry.Arguments ?? "", FlowDirection = FlowDirection.LeftToRight };
                Body.Children.Add(Row("Arguments", arguments, 110, 8));
                apply = () =>
                {
                    entry.Path = path.Text.Trim();
                    entry.Arguments = string.IsNullOrWhiteSpace(arguments.Text) ? null : arguments.Text.Trim();
                    return entry.Path.Length > 0;
                };
                break;
            }
            case EntryKind.Keystroke:
            {
                var keys = new KeyRecorder(entry.Keys);
                Body.Children.Add(Row("Keys", keys, 110, 12));
                Body.Children.Add(Text("Click the box and press the keys, one press after another for a sequence. They go to the app that was in front when the wheel opened.", "Petal.Caption").WithMargin(110, 4, 0, 0));
                apply = () =>
                {
                    entry.Keys = keys.Value;
                    return entry.Keys is not null;
                };
                break;
            }
            case EntryKind.Script:
            {
                var shell = Segments([(ScriptShell.PowerShell, "PowerShell"), (ScriptShell.Cmd, "Command Prompt")], entry.Shell);
                Body.Children.Add(Row("Runs with", shell, 110, 12));
                var input = Choice([(ScriptInput.None, "Nothing"), (ScriptInput.Clipboard, "The text on the clipboard"), (ScriptInput.SelectedText, "The selected text"), (ScriptInput.SelectedFiles, "The files selected in File Explorer")], entry.Input);
                Body.Children.Add(Row("Gets", input, 110, 8));
                var script = new TextBox
                {
                    Text = entry.Script ?? "",
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    TextWrapping = TextWrapping.NoWrap,
                    Height = 150,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FlowDirection = FlowDirection.LeftToRight,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                Body.Children.Add(script);
                Body.Children.Add(Text("What it gets is in %PETAL_INPUT% ($env:PETAL_INPUT), or the file %PETAL_INPUT_FILE%; selected files are in %PETAL_FILES%, one per line.", "Petal.Caption").WithMargin(0, 4, 0, 0));
                var output = new CheckBox { Content = L.T("Show what it prints when it finishes"), IsChecked = entry.ShowOutput, Margin = new Thickness(0, 8, 0, 0) };
                Body.Children.Add(output);
                apply = () =>
                {
                    entry.Shell = shell.Value;
                    entry.Input = ValueOf<ScriptInput>(input);
                    entry.Script = script.Text;
                    entry.ShowOutput = output.IsChecked == true;
                    return !string.IsNullOrWhiteSpace(entry.Script);
                };
                break;
            }
            case EntryKind.Folder:
            {
                var path = new TextBox { Text = entry.Path ?? "", FlowDirection = FlowDirection.LeftToRight };
                var browse = new Button { Content = L.T("Browse…"), Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFolderDialog { Title = L.T("A folder") };
                    if (dialog.ShowDialog(this) == true) path.Text = dialog.FolderName;
                };
                Body.Children.Add(Row("Folder", WithButton(path, browse), 110, 12));
                apply = () =>
                {
                    entry.Path = path.Text.Trim();
                    return entry.Path.Length > 0;
                };
                break;
            }
            case EntryKind.Link:
            {
                var path = new TextBox { Text = entry.Path ?? "https://", FlowDirection = FlowDirection.LeftToRight };
                Body.Children.Add(Row("Address", path, 110, 12));
                Body.Children.Add(Text("A web address, or a Windows one such as ms-settings:display.", "Petal.Caption").WithMargin(110, 4, 0, 0));
                apply = () =>
                {
                    entry.Path = path.Text.Trim();
                    return Uri.TryCreate(entry.Path, UriKind.Absolute, out _);
                };
                break;
            }
            case EntryKind.Group:
            {
                entry.Children ??= [];
                var children = entry.Children.Select(EntryList.Copy).ToList();
                Body.Children.Add(Text("Petals in the group", "Petal.Section").WithMargin(0, 14, 0, 6));
                Body.Children.Add(new ScrollViewer { Content = new EntryList(children, 8, this), MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                apply = () =>
                {
                    entry.Children = children;
                    return true;
                };
                break;
            }
        }
        var problem = Text("", "Petal.Caption");
        problem.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        problem.Margin = new Thickness(0, 8, 0, 0);
        Body.Children.Add(problem);
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.Click += (_, _) =>
        {
            entry.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            entry.Label = string.IsNullOrWhiteSpace(label.Text) ? null : label.Text.Trim().ToUpper(System.Globalization.CultureInfo.CurrentCulture);
            entry.Icon = icon;
            entry.Tint = tint;
            if (apply is not null && !apply())
            {
                problem.Text = L.T("Fill in what the petal needs first.");
                return;
            }
            if (entry.Kind == EntryKind.Script)
                ScriptRunner.Approve(entry);
            DialogResult = true;
        };
    }

    private static DockPanel WithButton(TextBox box, Button button)
    {
        var panel = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        panel.Children.Add(button);
        panel.Children.Add(box);
        return panel;
    }
}

/// <summary>Records keys pressed into it: "Ctrl+Shift+T", or a sequence such as "Ctrl+K Ctrl+S".</summary>
internal sealed class KeyRecorder : DockPanel
{
    private readonly TextBox _box = new() { IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Cursor = Cursors.Hand };
    private readonly List<KeyChord> _chords = [];

    public string? Value => _chords.Count == 0 ? null : KeySender.Normalise(_chords);

    public KeyRecorder(string? keys)
    {
        if (KeySender.Parse(keys) is { } chords)
            _chords.AddRange(chords);
        var clear = ToolWindowParts.IconButton("x", "Clear");
        clear.Click += (_, _) => { _chords.Clear(); Show(); _box.Focus(); };
        DockPanel.SetDock(clear, Dock.Right);
        Children.Add(clear);
        Children.Add(_box);
        System.Windows.Automation.AutomationProperties.SetName(_box, L.T("Keys"));
        _box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Input.Shortcut.IsModifier(key) || key is Key.ImeProcessed or Key.DeadCharProcessed)
                return;
            if (_chords.Count >= 4)
                _chords.Clear();
            _chords.Add(new KeyChord(Keyboard.Modifiers, key));
            Show();
        };
        Show();
    }

    private void Show() => _box.Text = _chords.Count == 0 ? L.T("Click here and press keys") : string.Join("   ", _chords.Select(c => c.Display));
}

/// <summary>A grid of icons to choose one from.</summary>
internal static class IconPicker
{
    public static void Open(FrameworkElement near, Action<string?> chosen)
    {
        var grid = new WrapPanel { Width = 330 };
        var popup = new Popup { PlacementTarget = near, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var usual = new Button { Content = L.T("Usual icon"), Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 3) };
        usual.Click += (_, _) => { popup.IsOpen = false; chosen(null); };
        grid.Children.Add(usual);
        foreach (var key in Icons.Keys.Order())
        {
            var button = new Button { Content = ToolWindow.ThemedIcon(key, 16), Style = (Style)Application.Current.FindResource("Petal.IconButton"), ToolTip = key, Margin = new Thickness(1) };
            System.Windows.Automation.AutomationProperties.SetName(button, key);
            button.Click += (_, _) => { popup.IsOpen = false; chosen(key); };
            grid.Children.Add(button);
        }
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = grid, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        popup.Child = frame;
        popup.IsOpen = true;
    }
}

/// <summary>
/// Adding a petal: a search over Petal's actions, the apps on this PC and the kinds of petal to make (keys, a
/// script, a folder, a link, a group, the open windows).
/// </summary>
internal sealed class PetalPickerWindow : ToolWindow
{
    private readonly TextBox _search = new();
    private readonly StackPanel _results = new();
    private IReadOnlyList<(string Name, string Path)> _apps = [];
    private WheelEntry? _chosen;

    private PetalPickerWindow() : base("Add a Petal", 520)
    {
        System.Windows.Automation.AutomationProperties.SetName(_search, L.T("Search actions, apps and more"));
        var hint = new TextBlock { Text = L.T("Search actions, apps and more"), IsHitTestVisible = false, Margin = new Thickness(8, 5, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        Body.Children.Add(new Grid { Children = { _search, hint } });
        Body.Children.Add(new ScrollViewer { Content = _results, Height = 420, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Content = L.T("Cancel");
        PrimaryButton.Click += (_, _) => Close();
        Loaded += async (_, _) =>
        {
            _search.Focus();
            _apps = await InstalledApps.ListAsync();
            Fill();
        };
        Fill();
    }

    /// <summary>Shows the picker; the new petal, or null when nothing was chosen.</summary>
    public static WheelEntry? Pick(Window owner)
    {
        var picker = new PetalPickerWindow { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        picker.ShowDialog();
        return picker._chosen;
    }

    private bool Matches(params string[] texts)
    {
        var query = _search.Text.Trim();
        return query.Length == 0 || texts.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase) || L.T(t).Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Fill()
    {
        _results.Children.Clear();
        var make = new (EntryKind Kind, string Name, string Icon, string Description)[]
        {
            (EntryKind.Keystroke, "Keys", "keyboard", "Press a shortcut in the app in front"),
            (EntryKind.App, "A program", "appWindow", "Open a program, or switch to it"),
            (EntryKind.Script, "Script", "terminal", "Run PowerShell or the Command Prompt"),
            (EntryKind.Folder, "Folder", "folderOpen", "Open a folder"),
            (EntryKind.Link, "Link", "globe", "Open a web address"),
            (EntryKind.Group, "Group", "layers", "Petals of its own, around it"),
            (EntryKind.RunningApps, "Open windows", "windows", "Switch to a window that is open"),
        };
        var own = make.Where(m => Matches(m.Name, m.Description)).ToList();
        if (own.Count > 0)
        {
            Section("Make your own");
            foreach (var m in own)
                Result(m.Icon, null, m.Name, m.Description, () => Make(m.Kind));
        }
        foreach (var group in WheelActions.All.Where(a => Matches(a.Name, a.Description, a.Label)).GroupBy(a => a.Section))
        {
            Section(group.Key switch { "Windows" => "Windows", "Screen" => "Screenshots", "Media" => "Media", _ => "Petal" });
            foreach (var action in group)
                Result(action.Icon, null, action.Name, action.Description, () => Choose(new WheelEntry { Kind = EntryKind.Action, Action = action.Id }));
        }
        var apps = _apps.Where(a => Matches(a.Name)).Take(_search.Text.Length == 0 ? 30 : 60).ToList();
        if (apps.Count > 0)
        {
            Section("Apps");
            foreach (var (name, path) in apps)
                Result(null, Rings.IconOf(path), name, "", () => Choose(new WheelEntry { Kind = EntryKind.App, Path = path, Name = name }));
        }
        if (_results.Children.Count == 0)
            _results.Children.Add(Text("Nothing found.", "Petal.Caption"));
    }

    private void Section(string title) => _results.Children.Add(Text(title, "Petal.Label").WithMargin(2, _results.Children.Count == 0 ? 0 : 12, 0, 4));

    private void Result(string? icon, ImageSource? picture, string name, string description, Action choose)
    {
        FrameworkElement image = picture is not null ? new Image { Source = picture, Width = 20, Height = 20 } : ThemedIcon(icon ?? "puzzle", 16);
        image.Margin = new Thickness(0, 0, 10, 0);
        image.VerticalAlignment = VerticalAlignment.Center;
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = L.T(name), FontWeight = FontWeights.SemiBold });
        if (description.Length > 0)
        {
            var detail = new TextBlock { Text = L.T(description), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
            words.Children.Add(detail);
        }
        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { image, words } },
            Style = (Style)FindResource("Petal.SubtleButton"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(name));
        button.Click += (_, _) => choose();
        _results.Children.Add(button);
    }

    private void Choose(WheelEntry entry)
    {
        _chosen = entry;
        Close();
    }

    /// <summary>A petal that needs filling in: its editor first, and only a saved one is added.</summary>
    private void Make(EntryKind kind)
    {
        var entry = new WheelEntry { Kind = kind, Children = kind == EntryKind.Group ? [] : null };
        if (kind == EntryKind.RunningApps)
        {
            Choose(entry);
            return;
        }
        if (new EntryEditorWindow(entry) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog() == true)
            Choose(entry);
    }
}

/// <summary>The apps in the Start menu, desktop and Store alike, as Windows lists them in All apps.</summary>
internal static class InstalledApps
{
    private static IReadOnlyList<(string Name, string Path)>? _cache;

    public static Task<IReadOnlyList<(string Name, string Path)>> ListAsync()
    {
        if (_cache is not null)
            return Task.FromResult(_cache);
        var done = new TaskCompletionSource<IReadOnlyList<(string, string)>>();
        var thread = new Thread(() =>
        {
            var apps = new List<(string, string)>();
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application")!;
                var shell = Activator.CreateInstance(type)!;
                var folder = type.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"])!;
                var items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null)!;
                int count = (int)items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null)!;
                for (int i = 0; i < count; i++)
                {
                    var item = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, [i])!;
                    var name = item.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    var id = item.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                        apps.Add((name, @"shell:AppsFolder\" + id));
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or InvalidCastException or NullReferenceException)
            {
                Log.Error(ex, "Listing the installed apps");
            }
            _cache = apps.DistinctBy(a => a.Item1).OrderBy(a => a.Item1, StringComparer.CurrentCultureIgnoreCase).ToList();
            done.TrySetResult(_cache);
        })
        { IsBackground = true, Name = "Petal app list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>
/// Colours for any petal on the file wheels: tools, actions, formats, favourites and recipes, with a search.
/// </summary>
internal sealed class PetalColoursWindow : ToolWindow
{
    private readonly TextBox _search = new();
    private readonly StackPanel _list = new();
    private readonly AppSettings _s = SettingsStore.Current;

    public PetalColoursWindow() : base("Petal Colours", 560)
    {
        var hint = new TextBlock { Text = L.T("Search petals"), IsHitTestVisible = false, Margin = new Thickness(8, 5, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        System.Windows.Automation.AutomationProperties.SetName(_search, L.T("Search petals"));
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        Body.Children.Add(Text("A colour makes a petal easy to find: it tints the petal and its icon, and lights it in that colour.", "Petal.Caption"));
        Body.Children.Add(new Grid { Margin = new Thickness(0, 10, 0, 0), Children = { _search, hint } });
        Body.Children.Add(new ScrollViewer { Content = _list, Height = 420, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ResetButton.Content = L.T("Clear All Colours");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _s.PetalColours.Clear();
            SettingsStore.Save();
            Fill();
        };
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var petals = new List<(string Section, Overlay.WheelItem Item)>();
        petals.AddRange(_s.Favourites.Select(f => ("Favourites", Overlay.WheelItem.From(f))));
        petals.AddRange(_s.Recipes.Select(r => ("Recipes", Overlay.WheelItem.From(r))));
        petals.AddRange(Petal.Core.Tools.ToolRegistry.All.Select(t => ("Tools", Overlay.WheelItem.From(t))));
        petals.AddRange(Petal.Core.Actions.FileActionRegistry.All.Select(a => ("Actions", Overlay.WheelItem.From(a))));
        petals.AddRange(Petal.Core.Formats.Formats.All.Where(f => f.CanWrite).Select(f => ("Formats", Overlay.WheelItem.From(f))));
        var query = _search.Text.Trim();
        foreach (var group in petals.Where(p => query.Length == 0 || L.T(p.Item.Name).Contains(query, StringComparison.CurrentCultureIgnoreCase) || p.Item.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).GroupBy(p => p.Section))
        {
            _list.Children.Add(Text(group.Key, "Petal.Label").WithMargin(0, _list.Children.Count == 0 ? 0 : 12, 0, 4));
            foreach (var (_, item) in group)
            {
                var key = item.Key;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var picker = PetalTints.Picker(_s.PetalColours.GetValueOrDefault(key), hex =>
                {
                    if (hex is null)
                        _s.PetalColours.Remove(key);
                    else
                        _s.PetalColours[key] = hex;
                    SettingsStore.Save();
                });
                DockPanel.SetDock(picker, Dock.Right);
                row.Children.Add(picker);
                var name = new TextBlock { Text = L.T(item.Name), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                row.Children.Add(name);
                _list.Children.Add(row);
            }
        }
    }
}
