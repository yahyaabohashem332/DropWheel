using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Settings;

/// <summary>Adds or changes one watched folder: where, which files, and what to do with them.</summary>
public sealed class AutomationWindow : ToolWindow
{
    private readonly WatchRule _rule;
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _subfolders = new() { Content = L.T("Include subfolders"), Margin = new Thickness(110, 8, 0, 0) };
    private readonly ComboBox _kind;
    private readonly ContentControl _actionHost = new() { Focusable = false };
    private readonly TextBlock _summary;
    private ComboBox? _action;

    public static readonly (WatchedKind Value, string Label)[] Kinds =
        [(WatchedKind.Pictures, "Pictures"), (WatchedKind.Videos, "Videos"), (WatchedKind.Audio, "Audio"), (WatchedKind.Documents, "Documents"), (WatchedKind.Any, "Any file")];

    public AutomationWindow(WatchRule? existing, Action<WatchRule> saved, Action<WatchRule>? deleted = null)
        : base(existing is null ? "New Automation" : "Automation", 440)
    {
        _rule = existing?.Copy() ?? new WatchRule { Folder = DefaultFolder() };
        _folder.Style = (Style)FindResource("Petal.Body");

        Body.Children.Add(Text("Petal watches the folder and converts each new file as soon as it has finished arriving. Results go to a “Petal” folder inside it; the originals stay where they are.", "Petal.Caption").WithMargin(0, 0, 0, 10));

        var choose = new Button { Content = L.T("Choose…"), Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("Folder to watch") };
            if (Directory.Exists(_rule.Folder))
                dialog.InitialDirectory = _rule.Folder;
            if (dialog.ShowDialog(this) == true)
            {
                _rule.Folder = dialog.FolderName;
                Sync();
            }
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(choose, Dock.Right);
        folderRow.Children.Add(choose);
        folderRow.Children.Add(_folder);
        Body.Children.Add(Row("Folder", folderRow, 110, 0));
        _subfolders.IsChecked = _rule.IncludeSubfolders;
        _subfolders.Click += (_, _) =>
        {
            _rule.IncludeSubfolders = _subfolders.IsChecked == true;
            Sync();
        };
        Body.Children.Add(_subfolders);

        _kind = Choice(Kinds, _rule.Kind);
        _kind.SelectionChanged += (_, _) =>
        {
            _rule.Kind = ValueOf<WatchedKind>(_kind);
            BuildActions();
        };
        Body.Children.Add(Row("New files", _kind, 110, 12));
        Body.Children.Add(Row("Then", _actionHost, 110));

        _summary = Text("", "Petal.Caption");
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 14, 0, 0);
        Body.Children.Add(_summary);

        PrimaryButton.Content = L.T(existing is null ? "Add" : "Save");
        PrimaryButton.Click += (_, _) =>
        {
            saved(_rule);
            Close();
        };
        if (existing is not null && deleted is not null)
        {
            ResetButton.Content = L.T("Remove");
            ResetButton.Visibility = Visibility.Visible;
            ResetButton.Click += (_, _) =>
            {
                deleted(existing);
                Close();
            };
        }
        BuildActions();
    }

    private static string DefaultFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    /// <summary>"Convert to …" for formats files of that kind can become, then the favourites that fit.</summary>
    private void BuildActions()
    {
        var options = Options(_rule.Kind);
        string current = Key(_rule.Action, _rule.Target);
        if (!options.Any(o => o.Value == current) && options.Count > 0)
            current = options[0].Value;
        _action = Choice(options, current);
        _action.MaxDropDownHeight = 320;
        _action.SelectionChanged += (_, _) => Take(ValueOf<string>(_action));
        _actionHost.Content = _action;
        Take(current);
    }

    private void Take(string? key)
    {
        if (key is not null)
        {
            _rule.Action = key.StartsWith('*') ? WatchAction.Favourite : WatchAction.Convert;
            _rule.Target = key.TrimStart('*');
        }
        Sync();
    }

    private static string Key(WatchAction action, string target) => action == WatchAction.Favourite ? "*" + target : target;

    public static List<(string Value, string Label)> Options(WatchedKind kind)
    {
        var sources = Formats.All.Where(f => f.CanRead && WatchRule.Accepts(kind, f)).ToList();
        var options = FavouriteText.Targets
            .Where(t => sources.Any(s => ConversionMatrix.CanConvert(s, t)))
            .Select(t => (t.Id, L.F("Convert to {0}", t.Label)))
            .ToList();
        foreach (var favourite in SettingsStore.Current.Favourites)
        {
            if (favourite.Target is { } t && sources.Any(s => s == t ? favourite.ChangesMoreThanFormat : ConversionMatrix.CanConvert(s, t)))
                options.Add(("*" + favourite.Id, "★ " + FavouriteText.Label(favourite)));
        }
        return options;
    }

    /// <summary>"New pictures in Downloads become JPG, saved in Downloads\Petal."</summary>
    public static string Describe(WatchRule rule)
    {
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(rule.Folder));
        if (folder.Length == 0)
            folder = rule.Folder;
        var what = rule.Kind switch
        {
            WatchedKind.Pictures => L.T("New pictures"),
            WatchedKind.Videos => L.T("New videos"),
            WatchedKind.Audio => L.T("New audio files"),
            WatchedKind.Documents => L.T("New documents"),
            _ => L.T("New files"),
        };
        var into = rule.Action == WatchAction.Favourite
            ? Guid.TryParse(rule.Target, out var id) && SettingsStore.Current.Favourites.FirstOrDefault(f => f.Id == id) is { } fav
                ? "★ " + FavouriteText.Label(fav)
                : L.T("a favourite that was removed")
            : Formats.FromId(rule.Target)?.Label ?? rule.Target.ToUpperInvariant();
        return L.F("{0} in {1} → {2}", what, folder, into);
    }

    private void Sync()
    {
        _folder.Text = _rule.Folder;
        _folder.ToolTip = _rule.Folder;
        bool exists = Directory.Exists(_rule.Folder);
        bool hasAction = _action is { Items.Count: > 0 };
        PrimaryButton.IsEnabled = exists && hasAction;
        _summary.Text = !exists
            ? L.T("Choose a folder that exists.")
            : !hasAction
                ? L.T("Nothing Petal can convert these files to.")
                : Describe(_rule) + ". " + L.F("Results are saved in {0}.", _rule.OutputFolder);
        FooterNote = "";
    }
}
