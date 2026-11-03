using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;
using Microsoft.Win32;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.App.Wheels;
using Petal.Core.Actions;

namespace Petal.App.Settings;

/// <summary>Settings for the wheels without files, the wheel's behaviour, the actions wheel, statistics and backup.</summary>
public sealed partial class SettingsWindow
{
    private ScrollViewer? _scroll;
    private readonly Dictionary<string, FrameworkElement> _cards = [];

    // ---------- the wheels without files ----------

    private readonly StackPanel _wheels = new();

    private UIElement WheelsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_wheels);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var add = new Button { Content = L.T("New Wheel…"), Margin = new Thickness(0, 0, 8, 6) };
        var ready = new Button { Content = L.T("Ready-Made Wheels…"), Margin = new Thickness(0, 0, 8, 6) };
        var tryIt = new Button { Content = L.T("Try Them"), Margin = new Thickness(0, 0, 8, 6) };
        var gallery = new Button { Content = L.T("Wheel Gallery…"), Margin = new Thickness(0, 0, 8, 6) };
        var import = new Button { Content = L.T("Import…"), Margin = new Thickness(0, 0, 8, 6) };
        buttons.Children.Add(add);
        buttons.Children.Add(ready);
        buttons.Children.Add(gallery);
        buttons.Children.Add(import);
        buttons.Children.Add(tryIt);
        gallery.Click += (_, _) => new WheelGalleryWindow(FillWheels) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        import.Click += (_, _) => new ImportWheelWindow(FillWheels) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        panel.Children.Add(buttons);
        add.Click += (_, _) =>
        {
            var wheel = WheelTemplates.Custom(L.T("My Wheel"));
            if (EditWheel(wheel))
            {
                _s.Wheels.Add(wheel);
                Save();
                FillWheels();
            }
        };
        ready.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = ready, Placement = PlacementMode.Bottom };
            L.Apply(menu);
            foreach (var template in WheelTemplates.All())
            {
                var present = _s.Wheels.Any(w => w.Template == template.Template);
                var item = new MenuItem { Header = L.T(template.Name), IsEnabled = !present, ToolTip = present ? L.T("Already on your list") : null };
                item.Click += (_, _) =>
                {
                    _s.Wheels.Add(template);
                    Save();
                    FillWheels();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        tryIt.Click += async (_, _) =>
        {
            Interop.NativeMethods.GetCursorPos(out var cursor);
            if (App.Current?.Controller is { } controller)
                await controller.OpenRingsAsync(IntPtr.Zero, new Input.ScreenPoint(cursor.X, cursor.Y));
        };
        FillWheels();
        return panel;
    }

    /// <summary>Edits a wheel in its own window; true when saved.</summary>
    private bool EditWheel(UserWheel wheel)
    {
        var editor = new WheelEditorWindow(wheel) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        return editor.ShowDialog() == true;
    }

    private void FillWheels()
    {
        _wheels.Children.Clear();
        if (_s.Wheels.Count == 0)
        {
            _wheels.Children.Add(Text("None yet. Add a ready-made wheel or make your own.", "Petal.Caption"));
            return;
        }
        for (int index = 0; index < _s.Wheels.Count; index++)
        {
            var wheel = _s.Wheels[index];
            int i = index;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var on = new CheckBox { IsChecked = wheel.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("On") };
            System.Windows.Automation.AutomationProperties.SetName(on, L.F("{0} on", L.T(wheel.Name)));
            on.Click += (_, _) =>
            {
                wheel.Enabled = on.IsChecked == true;
                Save();
            };
            row.Children.Add(on);
            var icon = ThemedIcon(wheel.Kind switch { WheelKind.Quick => "sparkles", WheelKind.Media => "disc", WheelKind.App => "appWindow", _ => "layers" }, 16, "Petal.Accent");
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Text(wheel.Name, "Petal.Body");
            name.FontWeight = FontWeights.SemiBold;
            words.Children.Add(name);
            var detail = new List<string> { L.P(wheel.Items.Count, "{0} petal", "{0} petals") };
            if (wheel.Kind == WheelKind.App)
                detail.Insert(0, L.F("First while {0} is in front", string.Join(", ", wheel.Apps)));
            if (Input.Shortcut.Parse(wheel.Shortcut) is { } own)
                detail.Add(own.Display);
            var caption = Text(string.Join(" · ", detail), "Petal.Caption");
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(caption);
            Grid.SetColumn(words, 2);
            row.Children.Add(words);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = IconButton("chevronUp", "Move up", 15);
            up.IsEnabled = i > 0;
            up.Click += (_, _) =>
            {
                _s.Wheels.RemoveAt(i);
                _s.Wheels.Insert(i - 1, wheel);
                Save();
                FillWheels();
            };
            var edit = IconButton("pencil", "Change", 15);
            edit.Click += (_, _) =>
            {
                var copy = Clone(wheel);
                if (EditWheel(copy))
                {
                    _s.Wheels[i] = copy;
                    Save();
                    FillWheels();
                }
            };
            var remove = IconButton("trash", "Remove", 15);
            remove.Click += (_, _) =>
            {
                _s.Wheels.Remove(wheel);
                Save();
                FillWheels();
            };
            var share = IconButton("share", "Share this wheel", 15);
            share.Click += (_, _) =>
            {
                var menu = new ContextMenu { PlacementTarget = share, Placement = PlacementMode.Bottom };
                L.Apply(menu);
                var file = new MenuItem { Header = L.T("Save as a File…") };
                file.Click += (_, _) =>
                {
                    var dialog = new SaveFileDialog { FileName = L.T(wheel.Name), DefaultExt = WheelSharing.Extension, Filter = L.T("DropWheel wheel") + $"|*{WheelSharing.Extension}" };
                    if (dialog.ShowDialog(this) == true)
                        File.WriteAllText(dialog.FileName, WheelSharing.ToJson(wheel));
                };
                var code = new MenuItem { Header = L.T("Copy as Text") };
                code.Click += (_, _) =>
                {
                    if (Words.SelectedText.TrySet(WheelSharing.ToCode(wheel)))
                        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Wheel copied"), L.T("Paste it in a chat or an e-mail; Import adds it."), null);
                };
                menu.Items.Add(file);
                menu.Items.Add(code);
                menu.IsOpen = true;
            };
            buttons.Children.Add(up);
            buttons.Children.Add(share);
            buttons.Children.Add(edit);
            if (WheelTemplates.Fresh(wheel.Template) is not null)
            {
                var reset = IconButton("rotateCcw", "Put back as it was", 15);
                reset.Click += (_, _) =>
                {
                    if (WheelTemplates.Fresh(wheel.Template) is not { } fresh) return;
                    fresh.Id = wheel.Id;
                    fresh.Enabled = wheel.Enabled;
                    fresh.Shortcut = wheel.Shortcut;
                    _s.Wheels[i] = fresh;
                    Save();
                    FillWheels();
                };
                buttons.Children.Add(reset);
            }
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 3);
            row.Children.Add(buttons);
            _wheels.Children.Add(row);
        }
    }

    /// <summary>A deep copy, so a cancelled edit leaves the wheel as it was.</summary>
    internal static UserWheel Clone(UserWheel wheel) =>
        System.Text.Json.JsonSerializer.Deserialize<UserWheel>(System.Text.Json.JsonSerializer.Serialize(wheel, SettingsStore.Options), SettingsStore.Options)!;

    // ---------- how the wheel behaves ----------

    private UIElement BehaviourSection()
    {
        var panel = new StackPanel();
        var zone = Segments([(CancelZone.Small, "Small"), (CancelZone.Medium, "Medium"), (CancelZone.Large, "Large")], _s.CancelZone);
        zone.Changed += v => { _s.CancelZone = v; Save(); };
        panel.Children.Add(Row("Cancel zone", zone, 150, 0));
        panel.Children.Add(Text("Letting go this close to the middle cancels. A larger zone forgives a drop that ends too soon.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        panel.Children.Add(Switch("Show what a petal makes", "Rest on a petal and the centre says how large the result will be, or shows the cut-out before Remove Background.", _s.PetalPreviews, v => _s.PetalPreviews = v));
        panel.Children.Add(Switch("Grow under the pointer", "The petal you point at swells a little towards you.", _s.GrowPetal, v => _s.GrowPetal = v));
        panel.Children.Add(Switch("Number keys", "1 to 9 choose a petal when the wheel is open for the keyboard; their numbers show around it.", _s.ShowNumberKeys, v => _s.ShowNumberKeys = v));
        panel.Children.Add(Switch("Mouse wheel switches wheels", "Turn the mouse wheel to go from Convert to Tools to Actions while you drag, or to the next wheel without files.", _s.ScrollSwitchesWheels, v => _s.ScrollSwitchesWheels = v));
        panel.Children.Add(Switch("Remember the last petal", "The wheel opens where you left it, with the petal you chose last lit up, so Enter does it again.", _s.RememberLastPetal, v =>
        {
            _s.RememberLastPetal = v;
            if (!v)
                WheelMemory.Clear();
        }));
        return panel;
    }

    /// <summary>The accent colours as swatches; the one in use has a ring.</summary>
    private UIElement AccentPicker()
    {
        var row = new WrapPanel();
        var choices = new[]
        {
            (AccentChoice.Violet, "DropWheel violet"), (AccentChoice.Windows, "Windows accent"), (AccentChoice.Ocean, "Ocean"),
            (AccentChoice.Mint, "Mint"), (AccentChoice.Rose, "Rose"), (AccentChoice.Amber, "Amber"), (AccentChoice.Graphite, "Graphite"),
        };
        var rings = new List<(AccentChoice Choice, Ellipse Ring)>();
        var own = new Tools.Parts.CustomColourChip(26, () => Theme.ColourOf(_s.AccentColour))
        {
            Chosen = _s.AccentColour == AccentChoice.Custom,
            Colour = _s.AccentColour == AccentChoice.Custom ? Theme.ColourOf(AccentChoice.Custom) : null,
        };
        own.Picked += c =>
        {
            _s.Accent = AccentChoice.Custom;
            _s.AccentCustom = Tools.Parts.ColourMath.Hex(c);
            _s.UseWindowsAccent = false;
            Theme.Configure(_s.Theme, AccentChoice.Custom, _s.AccentCustom);
            Save();
            foreach (var (_, r) in rings)
                r.Visibility = Visibility.Hidden;
            own.Chosen = true;
            own.Colour = c;
        };
        foreach (var (choice, name) in choices)
        {
            var colour = Theme.ColourOf(choice);
            var swatch = new Grid { Width = 26, Height = 26 };
            var ring = new Ellipse { StrokeThickness = 2, Visibility = choice == _s.AccentColour ? Visibility.Visible : Visibility.Hidden };
            ring.SetResourceReference(Shape.StrokeProperty, "Petal.Text");
            swatch.Children.Add(ring);
            swatch.Children.Add(new Ellipse { Fill = new SolidColorBrush(colour), Margin = new Thickness(4) });
            var button = new Button
            {
                Content = swatch,
                Style = (Style)FindResource("Petal.IconButton"),
                ToolTip = L.T(name),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, L.T(name));
            rings.Add((choice, ring));
            button.Click += (_, _) =>
            {
                _s.Accent = choice;
                _s.UseWindowsAccent = choice == AccentChoice.Windows;
                Theme.Configure(_s.Theme, choice);
                Save();
                foreach (var (c, r) in rings)
                    r.Visibility = c == choice ? Visibility.Visible : Visibility.Hidden;
                own.Chosen = false;
            };
            row.Children.Add(button);
        }
        row.Children.Add(own);
        return row;
    }

    // ---------- the actions wheel ----------

    private readonly StackPanel _actionList = new();
    private readonly StackPanel _destinations = new();

    private UIElement ActionsWheelSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_actionList);
        var defaults = new Button { Content = L.T("Use the Usual Actions"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        defaults.Click += (_, _) =>
        {
            _s.ActionsOnWheel = null;
            Save();
            FillActions();
        };
        panel.Children.Add(defaults);
        panel.Children.Add(Text("Folders", "Petal.Label").WithMargin(0, 16, 0, 6));
        panel.Children.Add(_destinations);
        var addFolder = new Button { Content = L.T("Add a Folder…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        addFolder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("A folder for the actions wheel") };
            if (dialog.ShowDialog(this) != true)
                return;
            _s.Destinations.Add(new FolderDestination { Folder = dialog.FolderName });
            Save();
            FillDestinations(addFolder);
        };
        panel.Children.Add(addFolder);
        FillActions();
        FillDestinations(addFolder);
        return panel;
    }

    private const int MostActionsOnWheel = 11;

    private void FillActions()
    {
        _actionList.Children.Clear();
        var on = ActionWheel.Parse(_s.ActionsOnWheel).ToList();
        var order = on.Concat(FileActionRegistry.All.Select(a => a.Id).Where(id => !on.Contains(id))).ToList();
        foreach (var id in order)
        {
            var action = FileActionRegistry.All.First(a => a.Id == id);
            bool shown = on.Contains(id);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var up = IconButton("chevronUp", "Move up", 14);
            up.IsEnabled = shown && on.IndexOf(id) > 0;
            up.Click += (_, _) =>
            {
                int at = on.IndexOf(id);
                on.RemoveAt(at);
                on.Insert(at - 1, id);
                _s.ActionsOnWheel = on.Select(a => a.ToString()).ToList();
                Save();
                FillActions();
            };
            DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(up);
            var check = new CheckBox
            {
                IsChecked = shown,
                IsEnabled = shown || on.Count < MostActionsOnWheel,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { ThemedIcon(action.Icon, 15).WithMargin(0, 0, 8, 0), new TextBlock { Text = L.T(action.Name), VerticalAlignment = VerticalAlignment.Center } },
                },
                ToolTip = L.T(action.Description),
            };
            System.Windows.Automation.AutomationProperties.SetName(check, L.T(action.Name));
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true)
                    on.Add(id);
                else
                    on.Remove(id);
                _s.ActionsOnWheel = on.Select(a => a.ToString()).ToList();
                Save();
                FillActions();
            };
            row.Children.Add(check);
            _actionList.Children.Add(row);
        }
        _actionList.Children.Add(Text(L.F("Up to {0} on the wheel; the others are under More.", MostActionsOnWheel), "Petal.Caption").WithMargin(0, 4, 0, 0));
    }

    private void FillDestinations(Button add)
    {
        _destinations.Children.Clear();
        add.IsEnabled = _s.Destinations.Count < PetalController.MaxDestinations;
        if (_s.Destinations.Count == 0)
        {
            _destinations.Children.Add(Text("None yet. Add a folder you often put files in, such as a project or a shared folder.", "Petal.Caption"));
            return;
        }
        foreach (var destination in _s.Destinations.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var remove = IconButton("trash", "Remove", 15);
            remove.Click += (_, _) =>
            {
                _s.Destinations.Remove(destination);
                Save();
                FillDestinations(add);
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var mode = Segments([(false, "Copy"), (true, "Move")], destination.Move);
            mode.Changed += v => { destination.Move = v; Save(); };
            mode.Margin = new Thickness(8, 0, 4, 0);
            DockPanel.SetDock(mode, Dock.Right);
            row.Children.Add(mode);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Text(destination.Name ?? Path.GetFileName(destination.Folder.TrimEnd('\\')), "Petal.Body");
            name.FontWeight = FontWeights.SemiBold;
            words.Children.Add(name);
            var where = Text(destination.Folder, "Petal.Caption");
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            where.FlowDirection = FlowDirection.LeftToRight;
            where.HorizontalAlignment = HorizontalAlignment.Left;
            if (!Directory.Exists(destination.Folder))
                where.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            words.Children.Add(where);
            row.Children.Add(words);
            _destinations.Children.Add(row);
        }
    }

    // ---------- statistics ----------

    private UIElement StatisticsSection()
    {
        var panel = new StackPanel();
        var data = Stats.Statistics.Snapshot();
        panel.Children.Add(Text(L.F("{0} · {1} saved · {2}",
            L.P(data.Files, "{0} file handled", "{0} files handled"),
            Util.Sizes.Format(data.Saved),
            L.P(Stats.Statistics.Streak(data), "{0} day in a row", "{0} days in a row")), "Petal.Body"));
        var open = new Button { Content = L.T("Open Statistics"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        open.Click += (_, _) => Stats.StatsWindow.ShowSingle();
        panel.Children.Add(open);
        panel.Children.Add(Switch("Keep counting", "Counts stay on this PC and are never sent anywhere.", _s.KeepStatistics, v => _s.KeepStatistics = v));
        return panel;
    }

    // ---------- backup ----------

    private UIElement BackupSection()
    {
        var panel = new StackPanel();
        var buttons = new WrapPanel();
        var export = new Button { Content = L.T("Export Settings…"), Margin = new Thickness(0, 0, 8, 6) };
        var import = new Button { Content = L.T("Import Settings…"), Margin = new Thickness(0, 0, 8, 6) };
        var folder = new Button { Content = L.T("Open the Settings Folder"), Margin = new Thickness(0, 0, 8, 6) };
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        buttons.Children.Add(folder);
        panel.Children.Add(buttons);
        panel.Children.Add(Text("DropWheel notices when settings.json is changed by hand and uses the change at once. Which of your own petals and scripts may run is not exported: on another PC, each is shown once before it runs.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        var status = Text("", "Petal.Caption");
        status.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(status);
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog
            {
                Title = L.T("Export DropWheel's settings"),
                FileName = $"DropWheel Settings {DateTime.Now:yyyy-MM-dd}.{SettingsTransfer.Extension}",
                Filter = $"{L.T("DropWheel settings")} (*.{SettingsTransfer.Extension})|*.{SettingsTransfer.Extension}",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                SettingsTransfer.Export(_s, dialog.FileName);
                status.Text = L.F("Saved as {0}", Path.GetFileName(dialog.FileName));
                status.ClearValue(TextBlock.ForegroundProperty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = L.T("The file couldn't be saved there.");
                status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            }
        };
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = L.T("Import DropWheel's settings"), Filter = $"{L.T("DropWheel settings")} (*.{SettingsTransfer.Extension})|*.{SettingsTransfer.Extension}|JSON (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                var (settings, petals, summary) = SettingsTransfer.Read(dialog.FileName);
                var question = L.F("Replace your settings with the ones in {0}?", Path.GetFileName(dialog.FileName)) + "\n\n"
                    + string.Join("\n", new[]
                    {
                        L.P(summary.Wheels, "{0} wheel", "{0} wheels"),
                        L.P(summary.Favourites, "{0} favourite", "{0} favourites"),
                        L.P(summary.Recipes, "{0} recipe", "{0} recipes"),
                        L.P(summary.Automations, "{0} automation", "{0} automations"),
                        L.P(summary.Petals, "{0} petal of your own", "{0} petals of your own"),
                    });
                if (MessageBox.Show(this, question, L.T("Import Settings"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                SettingsTransfer.Apply(settings, petals);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                status.Text = L.T(ex is InvalidDataException ? ex.Message : "The file couldn't be read.");
                status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            }
        };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo(SettingsStore.Directory) { UseShellExecute = true });
        return panel;
    }

    /// <summary>
    /// Settings read again from the file (an import, or an edit by hand) are shown at once: this window is
    /// built again where it was, at the same place in the page.
    /// </summary>
    private void WatchForChanges()
    {
        void Reloaded(AppSettings _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            var (left, top, offset) = (Left, Top, _scroll?.VerticalOffset ?? 0);
            Closed -= Unhook;
            SettingsStore.Reloaded -= Reloaded;
            Close();
            ShowSingle();
            if (_open is not null)
            {
                _open.WindowStartupLocation = WindowStartupLocation.Manual;
                (_open.Left, _open.Top) = (left, top);
                _open.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _open._scroll?.ScrollToVerticalOffset(offset));
            }
        });
        void Unhook(object? sender, EventArgs e) => SettingsStore.Reloaded -= Reloaded;
        SettingsStore.Reloaded += Reloaded;
        Closed += Unhook;
    }

    /// <summary>Opens Settings at one of its cards.</summary>
    public static void ShowSection(string title)
    {
        ShowSingle();
        _open?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_open?._cards.GetValueOrDefault(title) is { } card)
                card.BringIntoView();
        });
    }

    // ---------- the tour ----------

    /// <summary>Opens Settings and starts the tour, as the welcome window offers.</summary>
    public static void ShowTour()
    {
        ShowSingle();
        _open?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _open?.StartTour());
    }

    /// <summary>Cards that point at each part of Settings in turn, with Next and Back.</summary>
    private void StartTour()
    {
        var stops = new (string Card, string Title, string Text)[]
        {
            ("Keys", "Keys", "Drag a file and tap Shift for conversions, Ctrl for tools, Alt for actions. The quick shortcut opens the wheel without dragging."),
            ("Wheels", "Wheels without files", "Quick actions, what's playing, and shortcuts for the app in front. Change any of them, or make your own."),
            ("Appearance", "Make it yours", "Colours, the wheel's look, how it opens, and its sounds."),
            ("The Wheel", "Behaviour", "Number keys, the mouse wheel, and what the wheel remembers."),
            ("Actions Wheel", "Actions and folders", "Choose the actions on the wheel, and add folders to drop files into."),
            ("Favourites", "Favourites", "Your usual conversions, first on the wheel with a star."),
            ("Recipes", "Recipes", "Several steps in one petal."),
            ("Statistics", "Statistics", "What DropWheel has done for you, on this PC only."),
            ("Backup", "Backup", "Everything in one file, for another PC."),
        };
        new TourOverlay(this, _scroll!, stops.Where(s => _cards.ContainsKey(s.Card)).Select(s => (_cards[s.Card], s.Title, s.Text)).ToList()).Start();
    }
}
