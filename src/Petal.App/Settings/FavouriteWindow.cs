using System.Windows;
using System.Windows.Controls;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Settings;

/// <summary>Adds or changes one favourite: a format plus an optional size and quality.</summary>
public sealed class FavouriteWindow : ToolWindow
{
    private readonly Favourite _favourite;
    private readonly ComboBox _format;
    private readonly ContentControl _sizeHost = new() { Focusable = false };
    private readonly ContentControl _qualityHost = new() { Focusable = false };
    private readonly Grid _sizeRow, _qualityRow;
    private readonly TextBox _name = new();
    private readonly TextBlock _nameHint;
    private readonly TextBlock _summary;
    private ComboBox? _size, _quality;

    /// <param name="existing">The favourite to change, or null for a new one.</param>
    /// <param name="saved">Called with the result; <paramref name="deleted"/> when it is removed instead.</param>
    public FavouriteWindow(Favourite? existing, Action<Favourite> saved, Action<Favourite>? deleted = null)
        : base(existing is null ? "New Favourite" : "Favourite", 420)
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
        Body.Children.Add(Row("Format", _format, 110, 0));
        _sizeRow = Row("Longest side", _sizeHost, 110);
        Body.Children.Add(_sizeRow);
        _qualityRow = Row("Quality", _qualityHost, 110);
        Body.Children.Add(_qualityRow);

        _name.MaxLength = 24;
        _name.TextChanged += (_, _) =>
        {
            _favourite.Name = _name.Text.Trim();
            Describe();
        };
        Body.Children.Add(Row("Name", _name, 110, 10));
        _nameHint = Text("", "Petal.Caption");
        _nameHint.Margin = new Thickness(110, 4, 0, 0);
        Body.Children.Add(_nameHint);

        _summary = Text("", "Petal.Caption");
        _summary.Margin = new Thickness(0, 14, 0, 0);
        _summary.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_summary);

        _name.Text = _favourite.Name;
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

    /// <summary>The size and quality choices depend on the format.</summary>
    private void Rebuild()
    {
        if (_favourite.Target is not { } target)
            return;
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
