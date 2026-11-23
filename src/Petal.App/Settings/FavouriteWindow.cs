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

/// <summary>
/// Adds or changes one favourite: a format plus an optional size and quality, the resolution pages are
/// drawn at, whether metadata stays, where results go and how they are named.
/// </summary>
public sealed class FavouriteWindow : ToolWindow
{
    private readonly Favourite _favourite;
    private readonly ComboBox _format;
    private readonly ContentControl _sizeHost = new() { Focusable = false };
    private readonly ContentControl _qualityHost = new() { Focusable = false };
    private readonly Grid _sizeRow, _qualityRow, _dpiRow, _metadataRow;
    private readonly ComboBox _dpi, _metadata, _naming;
    private readonly Button _folder = new() { HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button _clearFolder;
    private readonly TextBox _name = new();
    private readonly TextBlock _nameHint;
    private readonly TextBlock _summary;
    private ComboBox? _size, _quality;

    /// <param name="existing">The favourite to change, or null for a new one.</param>
    /// <param name="saved">Called with the result; <paramref name="deleted"/> when it is removed instead.</param>
    public FavouriteWindow(Favourite? existing, Action<Favourite> saved, Action<Favourite>? deleted = null)
        : base(existing is null ? "New Favourite" : "Favourite", 460)
    {
        _favourite = existing?.Copy() ?? new Favourite { Format = Formats.Webp.Id, LongestSide = 1600 };

        Body.Children.Add(Text("A favourite is a conversion you use often. It appears first on the conversion wheel, with a star.", "Petal.Caption").WithMargin(0, 0, 0, 10));

        _format = Choice(FavouriteText.Targets.Select(f => (f.Id, f.Label)), _favourite.Format);
        _format.MaxDropDownHeight = 320;
        _format.SelectionChanged += (_, _) =>
        {
            _favourite.Format = ValueOf<string>(_format);
            Rebuild();
        };
        Body.Children.Add(Row("Format", _format, Labels, 0));
        _sizeRow = Row("Longest side", _sizeHost, Labels);
        Body.Children.Add(_sizeRow);
        _qualityRow = Row("Quality", _qualityHost, Labels);
        Body.Children.Add(_qualityRow);

        _dpi = Choice(FavouriteText.ResolutionChoices, FavouriteText.Resolutions.Contains(_favourite.Dpi) ? _favourite.Dpi : 0);
        _dpi.SelectionChanged += (_, _) =>
        {
            _favourite.Dpi = ValueOf<int>(_dpi);
            Describe();
        };
        _dpiRow = Row("Page resolution", _dpi, Labels);
        Body.Children.Add(_dpiRow);
        _metadata = Choice([(false, L.T("Keep")), (true, L.T("Remove"))], _favourite.RemoveMetadata);
        _metadata.SelectionChanged += (_, _) =>
        {
            _favourite.RemoveMetadata = ValueOf<bool>(_metadata);
            Describe();
        };
        _metadataRow = Row("Metadata", _metadata, Labels);
        Body.Children.Add(_metadataRow);

        _clearFolder = IconButton("x", "Use the folder in Settings", 14);
        _clearFolder.Margin = new Thickness(6, 0, 0, 0);
        _clearFolder.Click += (_, _) =>
        {
            _favourite.Folder = null;
            ShowFolder();
        };
        _folder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("Save results in"), InitialDirectory = _favourite.FolderIfThere ?? "" };
            if (dialog.ShowDialog(this) == true)
            {
                _favourite.Folder = dialog.FolderName;
                ShowFolder();
            }
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(_clearFolder, Dock.Right);
        folderRow.Children.Add(_clearFolder);
        folderRow.Children.Add(_folder);
        Body.Children.Add(Row("Save to", folderRow, Labels, 10));

        var names = FavouriteText.NameChoices.ToList();
        var pattern = _favourite.NamePattern ?? "";
        if (!names.Any(n => n.Value == pattern))
            names.Add((pattern, FavouriteText.NameLabel(pattern)));
        _naming = Choice(names, pattern);
        _naming.SelectionChanged += (_, _) =>
        {
            var chosen = ValueOf<string>(_naming);
            _favourite.NamePattern = string.IsNullOrEmpty(chosen) ? null : chosen;
            Describe();
        };
        Body.Children.Add(Row("File names", _naming, Labels));

        _name.MaxLength = 24;
        _name.TextChanged += (_, _) =>
        {
            _favourite.Name = _name.Text.Trim();
            Describe();
        };
        Body.Children.Add(Row("Name", _name, Labels, 10));
        _nameHint = Text("", "Petal.Caption");
        _nameHint.Margin = new Thickness(Labels, 4, 0, 0);
        Body.Children.Add(_nameHint);

        _summary = Text("", "Petal.Caption");
        _summary.Margin = new Thickness(0, 14, 0, 0);
        _summary.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_summary);

        _name.Text = _favourite.Name;
        ShowFolder();
        PrimaryButton.Content = L.T(existing is null ? "Add" : "Save");
        PrimaryButton.Click += (_, _) =>
        {
            saved(_favourite);
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
        Rebuild();
    }

    private const double Labels = 130;

    /// <summary>The folder chosen, or that results go where Settings says.</summary>
    private void ShowFolder()
    {
        var folder = _favourite.Folder;
        bool chosen = !string.IsNullOrWhiteSpace(folder);
        _folder.Content = chosen
            ? Directory.Exists(folder) ? FavouriteText.FolderName(folder!) : L.F("{0} (not found)", FavouriteText.FolderName(folder!))
            : L.T("As in Settings…");
        _folder.ToolTip = chosen ? folder : null;
        _clearFolder.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
        Describe();
    }

    /// <summary>The size and quality choices depend on the format.</summary>
    private void Rebuild()
    {
        if (_favourite.Target is not { } target)
            return;
        _dpiRow.Visibility = _favourite.DpiApplies ? Visibility.Visible : Visibility.Collapsed;
        _metadataRow.Visibility = _favourite.MetadataApplies ? Visibility.Visible : Visibility.Collapsed;
        _sizeRow.Visibility = _favourite.SizeApplies ? Visibility.Visible : Visibility.Collapsed;
        if (_favourite.SizeApplies)
        {
            var sizes = FavouriteText.SizesFor(target);
            if (!sizes.Any(s => s.Value == _favourite.LongestSide))
                _favourite.LongestSide = sizes.OrderBy(s => Math.Abs(s.Value - _favourite.LongestSide)).First().Value;
            _size = Choice(sizes.Select(s => (s.Value, s.Label)), _favourite.LongestSide);
            _size.SelectionChanged += (_, _) =>
            {
                _favourite.LongestSide = ValueOf<int>(_size);
                Describe();
            };
            _sizeHost.Content = _size;
        }

        var qualities = FavouriteText.QualitiesFor(target);
        _qualityRow.Visibility = qualities.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (qualities.Length > 0)
        {
            if (!qualities.Any(q => q.Value == _favourite.Quality))
                _favourite.Quality = 0;
            _quality = Choice(qualities.Select(q => (q.Value, q.Label)), _favourite.Quality);
            _quality.SelectionChanged += (_, _) =>
            {
                _favourite.Quality = ValueOf<int>(_quality);
                Describe();
            };
            _qualityHost.Content = _quality;
        }
        Describe();
    }

    private void Describe()
    {
        var auto = FavouriteText.Label(new Favourite { Format = _favourite.Format, LongestSide = _favourite.LongestSide, Quality = _favourite.Quality });
        _nameHint.Text = L.F("Optional. Without a name the petal says “{0}”.", auto);
        if (_favourite.Target is not { } target)
            return;
        var what = target.Family switch
        {
            FormatFamily.Image => L.F("Pictures are saved as {0}", target.Label),
            FormatFamily.Video => L.F("Videos are saved as {0}", target.Label),
            FormatFamily.Audio => L.F("Audio is saved as {0}", target.Label),
            _ => L.F("Files are saved as {0}", target.Label),
        };
        var details = FavouriteText.Describe(_favourite).Split(FavouriteText.Separator).Skip(1).ToList();
        _summary.Text = details.Count > 0 ? what + FavouriteText.Separator + string.Join(FavouriteText.Separator, details) + "." : what + ".";
        if (_favourite.SizeApplies && _favourite.LongestSide > 0)
            _summary.Text += " " + L.T("Smaller ones are never enlarged.");
        if (!_favourite.ChangesMoreThanFormat)
            _summary.Text += " " + L.F("It isn't offered for files that are already {0}.", target.Label);
    }
}
