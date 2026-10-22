using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Visuals;
using Petal.App.Wheels;
using Petal.Core.Text;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Commands;

/// <summary>Where a palette command runs: the point it was asked at, the window in front, and the files selected there.</summary>
internal sealed record PaletteContext(ScreenPoint At, IntPtr Target, IReadOnlyList<string> Files);

/// <summary>One line of the palette: what it does, how to say it, and how to do it.</summary>
internal sealed record PaletteItem(string Key, string Title, string Subtitle, string Icon, Func<PaletteContext, Task> Run)
{
    /// <summary>Other words that find it: the English name under an Arabic interface, a label, a few keywords.</summary>
    public string[] Also { get; init; } = [];

    /// <summary>What kind of thing it is, shown on the right ("Action", "App", "File").</summary>
    public string Kind { get; init; } = "";

    public ImageSource? Picture { get; init; }

    /// <summary>Why it can't run now; it is listed, dimmed, and choosing it only explains.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Ctrl+Enter: something else to do with it (open the folder of a file).</summary>
    public Func<PaletteContext, Task>? Alternate { get; init; }

    /// <summary>Ranks above others of the same match (the calculator's answer, a file wheel's petal).</summary>
    public int Boost { get; init; }

    internal int Score { get; set; }
}

/// <summary>
/// Petal's command palette: one shortcut (Ctrl+Win+K), then type what you want in English or Arabic.
/// Every action of every wheel, every petal for the files selected in File Explorer, Petal's windows, the
/// apps on the PC, what was copied lately, files found by Search by Meaning, sums and unit conversions,
/// snippets and workspaces; Enter does it. Nothing has to be remembered: "rec" finds Record the Screen,
/// "تسجيل" too.
/// </summary>
internal sealed class CommandPalette : Window
{
    private const int MaxShown = 40;
    private static CommandPalette? _open;
    private static Shell.GlobalHotkey? _hotkey;

    /// <summary>Other features add their own lines here (snippets, workspaces).</summary>
    public static readonly List<Func<string, PaletteContext, IEnumerable<PaletteItem>>> Sources = [];

    private readonly TextBox _query = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _placeholder;
    private readonly TextBlock _context;
    private readonly PaletteContext _ctx;
    private readonly DispatcherTimer _searchSoon = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private List<PaletteItem> _items = [];
    private List<PaletteItem> _found = [];
    private IReadOnlyList<(string Name, string Path)> _apps = [];
    private CancellationTokenSource? _search;

    public static bool RegisterShortcut()
    {
        _hotkey ??= new Shell.GlobalHotkey();
        _hotkey.Pressed -= OnShortcut;
        _hotkey.Pressed += OnShortcut;
        var text = SettingsStore.Current.PaletteShortcut;
        return _hotkey.Register(string.IsNullOrWhiteSpace(text) ? null : Shortcut.Parse(text));
    }

    public static void Dispose() => _hotkey?.Dispose();

    private static void OnShortcut() => _ = ToggleAsync(GetForegroundWindow());

    /// <summary>Opens over the window in front (taking its File Explorer selection along), or closes.</summary>
    public static async Task ToggleAsync(IntPtr front, string query = "")
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }
        GetCursorPos(out var p);
        var files = front == IntPtr.Zero ? [] : await Shell.ExplorerSelection.SelectionInAsync(front);
        Show(new PaletteContext(new ScreenPoint(p.X, p.Y), front, files), query);
    }

    private static void Show(PaletteContext ctx, string query)
    {
        _open = new CommandPalette(ctx, query);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    internal static CommandPalette ForSnapshot(string query, IReadOnlyList<string> files) =>
        new(new PaletteContext(new ScreenPoint(0, 0), IntPtr.Zero, files), query);

    private CommandPalette(PaletteContext ctx, string query)
    {
        _ctx = ctx;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        Title = L.T("Command Palette");
        FontFamily = UiFonts.Text;
        L.Apply(this);
        SetResourceReference(ForegroundProperty, "Petal.Text");

        var icon = ToolWindowIcon("search", 20);
        icon.Margin = new Thickness(18, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        _query.FontSize = 20;
        // Only the text and its caret: the field is the whole top of the palette.
        _query.Template = BareTextBox();
        _query.BorderThickness = new Thickness(0);
        _query.Background = Brushes.Transparent;
        _query.Padding = new Thickness(0, 14, 16, 14);
        _query.VerticalContentAlignment = VerticalAlignment.Center;
        _query.SetResourceReference(TextBox.ForegroundProperty, "Petal.Text");
        _query.SetResourceReference(TextBox.CaretBrushProperty, "Petal.Text");
        _placeholder = new TextBlock
        {
            Text = L.T("Type a command, an app, a file, a sum…"),
            FontSize = 20,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextDisabled");
        var field = new Grid { Children = { _query, _placeholder } };
        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        _context = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        _context.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        DockPanel.SetDock(_context, Dock.Right);
        top.Children.Add(_context);
        top.Children.Add(field);

        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.MaxHeight = 430;
        _list.Padding = new Thickness(6, 4, 6, 8);
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseUp += async (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && _list.SelectedItem is ListBoxItem { Tag: PaletteItem })
                await RunSelectedAsync(alternate: false);
        };
        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "Petal.Divider");
        var footer = new TextBlock
        {
            Text = L.T("↑↓ choose · Enter run · Ctrl+Enter other action · Esc close"),
            FontSize = 11,
            Margin = new Thickness(18, 6, 18, 9),
        };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextDisabled");
        var stack = new StackPanel { Children = { top, divider, _list, footer } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(24),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 270, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        card.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Content = card;

        _query.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
            _searchSoon.Stop();
            _searchSoon.Start();
        };
        _query.PreviewKeyDown += OnKey;
        _searchSoon.Tick += async (_, _) =>
        {
            _searchSoon.Stop();
            await SearchFilesAsync();
        };
        Deactivated += (_, _) =>
        {
            if (IsVisible)
                Close();
        };
        Closed += (_, _) => _search?.Cancel();
        Loaded += async (_, _) =>
        {
            Place();
            _query.Focus();
            _apps = await InstalledApps.ListAsync();
            Refresh();
        };
        _context.Text = ctx.Files.Count == 0 ? "" : ctx.Files.Count == 1 ? Path.GetFileName(ctx.Files[0]) : L.P(ctx.Files.Count, "{0} file", "{0} files");
        _items = Build();
        _query.Text = query;
        Refresh();
    }

    private static FrameworkElement ToolWindowIcon(string icon, double size) => Tools.ToolWindow.ThemedIcon(icon, size, "Petal.TextSecondary");

    /// <summary>In the upper middle of the screen the pointer is on.</summary>
    private void Place()
    {
        var monitor = Screen.ScreenNative.UnderPointer();
        double s = monitor.Scale;
        Left = (monitor.Work.Left + monitor.Work.Width / 2.0) / s - ActualWidth / 2;
        Top = monitor.Work.Top / s + monitor.Work.Height / s * 0.16;
    }

    // ---------- what can be found ----------

    private List<PaletteItem> Build()
    {
        var items = new List<PaletteItem>();
        var controller = App.Current.Controller;
        // The files selected in File Explorer: every petal of their three wheels, groups opened.
        if (_ctx.Files.Count > 0)
        {
            foreach (var mode in new[] { WheelMode.Conversions, WheelMode.Tools, WheelMode.Actions })
            {
                foreach (var item in Flatten(controller.ContentFor(mode, _ctx.Files).Items))
                {
                    var petal = item;
                    items.Add(new PaletteItem("petal:" + petal.Key, L.T(petal.Name), petal.Note is { } note ? L.T(note) : L.T(mode switch { WheelMode.Conversions => "Convert", WheelMode.Tools => "Tool", _ => "Action" }),
                        petal.Icon ?? "fileType", c => { controller.RunOnFiles(petal, c.Files, c.At); return Task.CompletedTask; })
                    {
                        Also = [petal.Name, petal.Label],
                        Kind = L.T("Selected files"),
                        Unavailable = petal.Unavailable is { } why && petal.Blocked ? L.T(why) : null,
                        Boost = 120,
                    });
                }
            }
        }
        // Every built-in action, as a petal would run it over the window in front.
        foreach (var action in WheelActions.All)
        {
            var a = action;
            string? unavailable = null;
            try { unavailable = a.Unavailable?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            items.Add(new PaletteItem("action:" + a.Id, L.T(a.Name), L.T(a.Description), a.Icon, c => a.Run(new WheelActionContext(c.At, c.Target, controller)))
            {
                Also = [a.Name, a.Label, L.T(a.Label)],
                Kind = L.T("Action"),
                Unavailable = unavailable is null ? null : L.T(unavailable),
            });
        }
        // The wheels without files, to open straight onto one.
        foreach (var wheel in SettingsStore.Current.Wheels.Where(w => w.Enabled))
        {
            var id = wheel.Id;
            items.Add(new PaletteItem("wheel:" + id, L.F("The {0} wheel", L.T(wheel.Name)), L.T("Opens it over the window in front"), "layers",
                c => controller.OpenRingsAsync(c.Target, c.At, id))
            {
                Also = [wheel.Name, "wheel " + wheel.Name, "عجلة " + L.T(wheel.Name)],
                Kind = L.T("Wheel"),
            });
        }
        // Petal's own windows.
        foreach (var (key, name, icon, run) in new (string, string, string, Action<PaletteContext>)[]
        {
            ("settings", "Petal Settings", "settings", _ => App.Current.ShowSettings()),
            ("history", "History", "history", _ => Tools.Windows.HistoryWindow.ShowSingle()),
            ("search", "Search by Meaning", "search", _ => Search.SearchWindow.ShowSingle()),
            ("scan", "Scan with Phone", "scanLine", _ => Actions.ScanWindow.ShowSingle()),
            ("qr", "Create QR Code", "qr", _ => Tools.Windows.QrWindow.ShowSingle()),
            ("stats", "Your Statistics", "chart", _ => Stats.StatsWindow.ShowSingle()),
            ("duplicates", "Find Duplicates", "copy", _ => Tools.Windows.DuplicatesWindow.ChooseAndShow()),
            ("practice", "Practice", "target", _ => Onboarding.PracticeWindow.ShowSingle()),
            ("ask", "Ask Petal", "ask", c => { if (c.Files.Count > 0) Tools.Windows.AskWindow.Show(c.Files, controller.Jobs, c.At); else Tools.Windows.AskWindow.ChooseAndShow(controller.Jobs); }),
        })
        {
            // Windows that are petals too are listed once, as the petal.
            if (Wheels.WheelActions.All.Any(a => a.Name == name))
                continue;
            var go = run;
            items.Add(new PaletteItem("petal:" + key, L.T(name), L.T("Petal"), icon, c => { go(c); return Task.CompletedTask; }) { Also = [name], Kind = L.T("Petal") });
        }
        return items;
    }

    private static ControlTemplate BareTextBox()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
    }

    private static IEnumerable<WheelItem> Flatten(IEnumerable<WheelItem> items) =>
        items.SelectMany(i => i.Children is { } children ? Flatten(children) : [i]);

    /// <summary>Lines that depend on what was typed: the answer to a sum, apps, clips, a question, the web.</summary>
    private IEnumerable<PaletteItem> Typed(string query)
    {
        if (query.Length == 0)
            yield break;
        if (Calculator.Evaluate(query) is { } answer)
        {
            var shown = answer.Text + (answer.Unit.Length > 0 ? " " + answer.Unit : "");
            yield return new PaletteItem("calc", "= " + shown, L.T("Enter copies the answer"), "calculator", _ =>
            {
                Words.SelectedText.TrySet(answer.Text);
                return Task.CompletedTask;
            })
            { Kind = L.T("Calculator"), Boost = 2000 };
        }
        foreach (var (name, path) in _apps)
        {
            if (Fuzzy.Score(query, name) is var score and > 0)
            {
                var target = path;
                yield return new PaletteItem("app:" + path, name, L.T("Open"), "appWindow", _ =>
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return Task.CompletedTask;
                })
                { Kind = L.T("App"), Picture = Rings.IconOf(path), Score = score };
            }
        }
        foreach (var clip in Clips.ClipboardHistory.All.Where(c => c.Kind == Clips.ClipKind.Text && c.Text!.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(4))
        {
            var item = clip;
            yield return new PaletteItem("clip:" + clip.Id, clip.Preview, L.T("Paste it"), "clipboardList", c => Clips.ClipboardHistory.PasteAsync(item, c.Target))
            { Kind = L.T("Clipboard"), Score = 500 };
        }
        if (_ctx.Files.Count > 0 && query.Contains(' '))
        {
            yield return new PaletteItem("ask", L.F("Ask Petal: {0}", query), L.T("Petal shows the steps before anything runs"), "ask", c =>
            {
                Tools.Windows.AskWindow.Show(c.Files, App.Current.Controller.Jobs, c.At, query);
                return Task.CompletedTask;
            })
            { Kind = L.T("Ask Petal"), Score = 300 };
        }
        foreach (var source in Sources)
        {
            IEnumerable<PaletteItem> extra;
            try { extra = source(query, _ctx).ToList(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error(ex, "A palette source"); continue; }
            foreach (var e in extra)
                yield return e;
        }
        yield return new PaletteItem("web", L.F("Search the web for “{0}”", query), L.T("Opens in your browser"), "globe", _ =>
        {
            Process.Start(new ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true });
            return Task.CompletedTask;
        })
        { Kind = L.T("Web"), Score = 1, Boost = -10_000 };
    }

    /// <summary>Files found by Search by Meaning, a moment after typing stops.</summary>
    private async Task SearchFilesAsync()
    {
        var query = _query.Text.Trim();
        _search?.Cancel();
        _found = [];
        if (query.Length < 3 || !File.Exists(Petal.Recipes.Search.SearchIndex.DefaultPath))
        {
            Refresh();
            return;
        }
        var cts = _search = new CancellationTokenSource();
        try
        {
            var results = await Task.Run(() => Search.SearchService.Search(query), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _found = results.ByWords.Concat(results.ByMeaning).DistinctBy(h => h.Path, StringComparer.OrdinalIgnoreCase).Take(6).Select((hit, i) =>
            {
                var path = hit.Path;
                return new PaletteItem("file:" + path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", "file", _ =>
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return Task.CompletedTask;
                })
                {
                    Kind = L.T("File"),
                    Picture = Shell.ShellIcons.Thumbnail(path, 32),
                    Score = 450 - i * 10,
                    Alternate = _ =>
                    {
                        Process.Start("explorer.exe", "/select,\"" + path + "\"");
                        return Task.CompletedTask;
                    },
                };
            }).ToList();
            Refresh();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
        }
    }

    // ---------- showing and choosing ----------

    private void Refresh()
    {
        var query = _query.Text.Trim();
        var usage = PaletteMemory.Counts();
        IEnumerable<PaletteItem> ranked;
        if (query.Length == 0)
        {
            // Nothing typed: the selected files' petals first, then what was used most.
            ranked = _items.Where(i => i.Boost > 0).Take(8)
                .Concat(_items.Where(i => usage.ContainsKey(i.Key)).OrderByDescending(i => usage[i.Key]).Take(8))
                .DistinctBy(i => i.Key);
        }
        else
        {
            var scored = new List<PaletteItem>();
            foreach (var item in _items)
            {
                int score = Fuzzy.Best(query, [item.Title, .. item.Also]);
                if (score <= 0)
                    continue;
                item.Score = score;
                scored.Add(item);
            }
            scored.AddRange(Typed(query));
            scored.AddRange(_found);
            ranked = scored
                .OrderByDescending(i => i.Score + i.Boost + Math.Min(150, usage.GetValueOrDefault(i.Key) * 25) - (i.Unavailable is null ? 0 : 200))
                .DistinctBy(i => i.Key)
                .Take(MaxShown);
        }
        _list.Items.Clear();
        foreach (var item in ranked)
            _list.Items.Add(Row(item));
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private ListBoxItem Row(PaletteItem item)
    {
        FrameworkElement icon = item.Picture is { } picture
            ? new Image { Source = picture, Width = 22, Height = 22 }
            : Tools.ToolWindow.ThemedIcon(Icons.Keys.Contains(item.Icon) ? item.Icon : "sparkles", 18);
        icon.Margin = new Thickness(6, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = new TextBlock { Text = item.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        var subtitle = new TextBlock { Text = item.Unavailable ?? item.Subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, item.Unavailable is null ? "Petal.TextSecondary" : "Petal.Danger");
        var kind = new TextBlock { Text = item.Kind, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextDisabled");
        var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 5), Opacity = item.Unavailable is null ? 1 : 0.6 };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(kind, Dock.Right);
        dock.Children.Add(icon);
        dock.Children.Add(kind);
        dock.Children.Add(new StackPanel { Children = { title, subtitle }, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = dock, Tag = item, Padding = new Thickness(6, 2, 6, 2) };
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_query.Text.Length > 0)
                    _query.Clear();
                else
                    Close();
                break;
            case Key.Down:
                e.Handled = true;
                Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                Move(6);
                break;
            case Key.PageUp:
                e.Handled = true;
                Move(-6);
                break;
            case Key.Return:
                e.Handled = true;
                await RunSelectedAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
        }
    }

    private void Move(int by)
    {
        if (_list.Items.Count == 0)
            return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + by, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private async Task RunSelectedAsync(bool alternate)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: PaletteItem item })
            return;
        if (item.Unavailable is not null)
        {
            Hud.HudCard.ShowAt(_ctx.At, "", "").Fail(item.Title, item.Unavailable);
            return;
        }
        var run = alternate && item.Alternate is { } other ? other : item.Run;
        PaletteMemory.Count(item.Key);
        Close();
        // Let the window in front have the keyboard back first.
        await Task.Delay(120);
        try
        {
            await run(_ctx);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, $"Running {item.Title} from the palette");
            Hud.HudCard.ShowAt(_ctx.At, "", "").Fail(item.Title, L.T("Something went wrong. The activity log has the details."));
        }
    }
}

/// <summary>How often each palette line was chosen, so the usual ones come first. Kept beside the settings.</summary>
internal static class PaletteMemory
{
    private static Dictionary<string, int>? _counts;

    private static string FilePath => Path.Combine(SettingsStore.Directory, "palette.json");

    public static IReadOnlyDictionary<string, int> Counts()
    {
        if (_counts is null)
        {
            try
            {
                _counts = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _counts = null;
            }
            _counts ??= [];
        }
        return _counts;
    }

    public static void Count(string key)
    {
        Counts();
        _counts![key] = _counts.GetValueOrDefault(key) + 1;
        // Only the most used are worth keeping.
        if (_counts.Count > 200)
            foreach (var old in _counts.OrderBy(c => c.Value).Take(_counts.Count - 200).Select(c => c.Key).ToList())
                _counts.Remove(old);
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_counts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
