using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Recipes;

namespace Petal.App.Settings;

/// <summary>Builds a recipe: a name, then steps in order, each with its own settings.</summary>
public sealed class RecipeWindow : ToolWindow
{
    private readonly Recipe _recipe;
    private readonly TextBox _name = new() { MaxLength = 30 };
    private readonly StackPanel _steps = new();
    private readonly ContentControl _options = new() { Focusable = false };
    private readonly TextBlock _hint;
    private readonly ComboBox _kind;
    private int _selected = -1;

    public RecipeWindow(Recipe? existing, Action<Recipe> saved, Action<Recipe>? deleted = null)
        : base(existing is null ? "New Recipe" : "Recipe", 480)
    {
        _recipe = existing?.Copy() ?? new Recipe();
        Body.Children.Add(Text("A recipe is several steps in one petal, such as removing the background, resizing and saving as WEBP. Steps run in order, each on what the one before made.", "Petal.Caption").WithMargin(0, 0, 0, 10));

        _name.Text = _recipe.Name;
        _name.TextChanged += (_, _) =>
        {
            _recipe.Name = _name.Text.Trim();
            Sync();
        };
        Body.Children.Add(Row("Name", _name, 90, 0));

        Body.Children.Add(SectionTitle("Steps").WithMargin(0, 14, 0, 6));
        Body.Children.Add(new ScrollViewer { Content = _steps, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _kind = Choice(RecipeText.Kinds.Select(k => (k.Kind, k.Label)), RecipeStepKind.Convert);
        var add = new Button { Content = L.T("Add Step"), Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            var step = new RecipeStep { Kind = ValueOf<RecipeStepKind>(_kind) };
            if (step.Kind == RecipeStepKind.Convert)
                step.Format = Formats.Webp.Id;
            if (step.Kind == RecipeStepKind.Resize)
                step.LongestSide = 1600;
            if (step.Kind == RecipeStepKind.Rename)
                step.Pattern = "{name} {n}";
            _recipe.Steps.Add(step);
            _selected = _recipe.Steps.Count - 1;
            Fill();
        };
        var adder = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        adder.Children.Add(add);
        adder.Children.Add(_kind);
        Body.Children.Add(adder);

        _options.Margin = new Thickness(0, 12, 0, 0);
        Body.Children.Add(_options);

        var onWheel = new CheckBox { Content = L.T("Show it on the tools wheel"), IsChecked = _recipe.OnWheel, Margin = new Thickness(0, 12, 0, 0) };
        onWheel.Click += (_, _) => _recipe.OnWheel = onWheel.IsChecked == true;
        Body.Children.Add(onWheel);
        _hint = Text("", "Petal.Caption");
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 8, 0, 0);
        Body.Children.Add(_hint);

        PrimaryButton.Content = L.T(existing is null ? "Add" : "Save");
        PrimaryButton.Click += (_, _) =>
        {
            saved(_recipe);
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
        if (_recipe.Steps.Count > 0)
            _selected = 0;
        Fill();
    }

    private void Fill()
    {
        _steps.Children.Clear();
        if (_recipe.Steps.Count == 0)
            _steps.Children.Add(Text("No steps yet. Choose one below and add it.", "Petal.Caption"));
        for (int i = 0; i < _recipe.Steps.Count; i++)
            _steps.Children.Add(StepRow(i));
        _options.Content = _selected >= 0 && _selected < _recipe.Steps.Count ? Options(_recipe.Steps[_selected]) : null;
        Sync();
    }

    private UIElement StepRow(int index)
    {
        var step = _recipe.Steps[index];
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var select = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 10, 6), Style = (Style)FindResource("Petal.SubtleButton") };
        var words = new StackPanel();
        var title = Text($"{index + 1}. {RecipeText.KindName(step.Kind)}", "Petal.Body");
        title.FontWeight = index == _selected ? FontWeights.SemiBold : FontWeights.Normal;
        words.Children.Add(title);
        words.Children.Add(Text(RecipeText.Describe(step), "Petal.Caption"));
        select.Content = words;
        select.Click += (_, _) =>
        {
            _selected = index;
            Fill();
        };
        if (index == _selected)
            select.SetResourceReference(BackgroundProperty, "Petal.AccentSoft");
        grid.Children.Add(select);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var up = IconButton("chevronUp", "Move up", 14);
        up.IsEnabled = index > 0;
        up.Click += (_, _) =>
        {
            (_recipe.Steps[index - 1], _recipe.Steps[index]) = (_recipe.Steps[index], _recipe.Steps[index - 1]);
            _selected = index - 1;
            Fill();
        };
        var remove = IconButton("trash", "Remove", 14);
        remove.Click += (_, _) =>
        {
            _recipe.Steps.RemoveAt(index);
            _selected = Math.Min(_selected, _recipe.Steps.Count - 1);
            Fill();
        };
        buttons.Children.Add(up);
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>The settings of one step, changed in place.</summary>
    private UIElement Options(RecipeStep step)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle(L.F("Step {0}: {1}", _selected + 1, RecipeText.KindName(step.Kind))).WithMargin(0, 0, 0, 4));
        void Changed() => RefreshRow();
        switch (step.Kind)
        {
            case RecipeStepKind.Convert:
            {
                var format = Choice(FavouriteText.Targets.Select(f => (f.Id, f.Label)), step.Format ?? Formats.Webp.Id);
                format.MaxDropDownHeight = 300;
                var size = new ContentControl { Focusable = false };
                var quality = new ContentControl { Focusable = false };
                void Rebuild()
                {
                    var target = Formats.FromId(step.Format ?? "") ?? Formats.Webp;
                    var favourite = new Favourite { Format = target.Id };
                    if (favourite.SizeApplies)
                    {
                        var sizes = Choice(FavouriteText.SizesFor(target).Select(s => (s.Value, s.Label)), step.LongestSide);
                        sizes.SelectionChanged += (_, _) => { step.LongestSide = ValueOf<int>(sizes); Changed(); };
                        size.Content = Row("Longest side", sizes, 110, 8);
                    }
                    else
                    {
                        step.LongestSide = 0;
                        size.Content = null;
                    }
                    var qualities = FavouriteText.QualitiesFor(target);
                    if (qualities.Length > 0)
                    {
                        if (!qualities.Any(q => q.Value == step.Quality))
                            step.Quality = 0;
                        var choice = Choice(qualities.Select(q => (q.Value, q.Label)), step.Quality);
                        choice.SelectionChanged += (_, _) => { step.Quality = ValueOf<int>(choice); Changed(); };
                        quality.Content = Row("Quality", choice, 110, 8);
                    }
                    else
                    {
                        step.Quality = 0;
                        quality.Content = null;
                    }
                }
                format.SelectionChanged += (_, _) => { step.Format = ValueOf<string>(format); Rebuild(); Changed(); };
                panel.Children.Add(Row("Format", format, 110, 4));
                panel.Children.Add(size);
                panel.Children.Add(quality);
                Rebuild();
                break;
            }
            case RecipeStepKind.Resize:
            {
                var sizes = Choice(FavouriteText.SizesFor(Formats.Jpg).Where(s => s.Value > 0).Select(s => (s.Value, s.Label)), step.LongestSide > 0 ? step.LongestSide : 1600);
                step.LongestSide = ValueOf<int>(sizes);
                sizes.SelectionChanged += (_, _) => { step.LongestSide = ValueOf<int>(sizes); Changed(); };
                panel.Children.Add(Row("Longest side", sizes, 110, 4));
                panel.Children.Add(Text("Smaller pictures are left as they are.", "Petal.Caption").WithMargin(110, 4, 0, 0));
                break;
            }
            case RecipeStepKind.Rotate:
            {
                var turns = Segments([(90, "90° →"), (180, "180°"), (270, "90° ←")], step.Turns is 180 or 270 ? step.Turns : 90);
                turns.Changed += v => { step.Turns = v; Changed(); };
                panel.Children.Add(Row("Turn", turns, 110, 4));
                break;
            }
            case RecipeStepKind.Compress:
            {
                var apps = Choice(new[] { ("", "No app: a preset") }.Concat(ShareTargets.All.Select(t => (t.Id, t.Name))), step.App ?? "");
                var preset = Segments([(false, "Balanced"), (true, "Strong")], step.Strong);
                preset.Changed += v => { step.Strong = v; Changed(); };
                apps.SelectionChanged += (_, _) =>
                {
                    var id = ValueOf<string>(apps);
                    step.App = id.Length == 0 ? null : id;
                    preset.IsEnabled = step.App is null;
                    Changed();
                };
                preset.IsEnabled = step.App is null;
                panel.Children.Add(Row("For", apps, 110, 4));
                panel.Children.Add(Row("Preset", preset, 110, 8));
                break;
            }
            case RecipeStepKind.RemoveBackground:
            {
                var background = Segments([("", "Transparent"), ("#FFFFFF", "White"), ("#000000", "Black")], step.Background ?? "");
                background.Changed += v => { step.Background = v.Length == 0 ? null : v; Changed(); };
                panel.Children.Add(Row("Background", background, 110, 4));
                break;
            }
            case RecipeStepKind.Watermark:
            {
                var text = new TextBox { Text = step.Text ?? "" };
                text.TextChanged += (_, _) => { step.Text = text.Text; Changed(); };
                panel.Children.Add(Row("Text", text, 110, 4));
                var place = new PlacePicker(step.Place);
                place.Changed += p => { step.Place = p; Changed(); };
                panel.Children.Add(Row("Position", place, 110, 8));
                var color = new ColorWell((Color)ColorConverter.ConvertFromString(step.Color));
                color.Changed += c => { step.Color = ColorWell.Hex(c); Changed(); };
                panel.Children.Add(Row("Colour", color, 110, 8));
                var opacity = Slider(10, 100, step.Opacity * 100, v => $"{v:0}%", 5);
                opacity.Changed += v => { step.Opacity = v / 100; Changed(); };
                panel.Children.Add(Row("Opacity", opacity, 110, 8));
                var tiled = new CheckBox { Content = L.T("Repeat across"), IsChecked = step.Tiled, Margin = new Thickness(110, 8, 0, 0) };
                tiled.Click += (_, _) => { step.Tiled = tiled.IsChecked == true; Changed(); };
                panel.Children.Add(tiled);
                break;
            }
            case RecipeStepKind.Rename:
            {
                var pattern = new TextBox { Text = step.Pattern ?? "{name}", FlowDirection = FlowDirection.LeftToRight };
                pattern.TextChanged += (_, _) => { step.Pattern = pattern.Text; Changed(); };
                panel.Children.Add(Row("Name", pattern, 110, 4));
                panel.Children.Add(Text("{name} is the original name, {n} a running number, {date} the date a photo was taken.", "Petal.Caption").WithMargin(110, 4, 0, 0));
                break;
            }
            case RecipeStepKind.Zip or RecipeStepKind.CombinePdf:
            {
                var name = new TextBox { Text = step.Name ?? "" };
                name.TextChanged += (_, _) => { step.Name = name.Text.Trim(); Changed(); };
                panel.Children.Add(Row("File name", name, 110, 4));
                panel.Children.Add(Text("Leave it empty to use the recipe's name.", "Petal.Caption").WithMargin(110, 4, 0, 0));
                break;
            }
            case RecipeStepKind.Clean:
                panel.Children.Add(Text("Removes where photos and videos were taken, the device, and the author of PDFs and Office files.", "Petal.Caption"));
                break;
            case RecipeStepKind.Searchable:
                panel.Children.Add(Text("Reads the text of scanned pages with Windows OCR, so it can be searched and copied. Pictures become PDFs.", "Petal.Caption"));
                break;
        }
        return panel;
    }

    /// <summary>Updates the selected row's description without rebuilding the options (keeps focus).</summary>
    private void RefreshRow()
    {
        if (_selected >= 0 && _selected < _steps.Children.Count && _steps.Children[_selected] is Grid grid
            && grid.Children[0] is Button { Content: StackPanel words } && words.Children[1] is TextBlock description)
            description.Text = RecipeText.Describe(_recipe.Steps[_selected]);
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = _recipe.Name.Length > 0 && _recipe.Steps.Count > 0;
        _hint.Text = _recipe.Steps.Count == 0
            ? ""
            : RecipeText.Accepts(_recipe) + " " + L.F("Results are saved beside the originals as “name {0}”.", _recipe.Name.Length > 0 ? _recipe.Name : L.T("Recipe"));
        FooterNote = RecipeText.Describe(_recipe);
    }
}
