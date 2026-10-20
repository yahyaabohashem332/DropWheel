using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Files.Metadata;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Inspect, edit or strip metadata. One file shows every field grouped and searchable; a selection
/// offers "Remove All" for all of them at once.
/// </summary>
public sealed class MetadataWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { Width = 200 };
    private readonly StackPanel _summary = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly Dictionary<string, MetadataEdit> _edits = [];
    private IReadOnlyList<MetadataField> _fields = [];
    private readonly Button _removeAll = new() { Content = "Remove All", Margin = new Thickness(0, 0, 10, 0) };

    public MetadataWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, files.Count == 1 ? 520 : 400)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;

        PrimaryButton.Content = "Save";
        _removeAll.Click += (_, _) => RemoveAll();
        // "Remove All" sits beside Save, as a second action.
        if (PrimaryButton.Parent is DockPanel dock)
        {
            DockPanel.SetDock(_removeAll, Dock.Right);
            dock.Children.Insert(1, _removeAll);
        }

        if (files.Count > 1)
        {
            Body.Children.Add(Text($"{files.Count} files selected", "Petal.Section"));
            Body.Children.Add(Text("Remove All strips location, camera, author and editing history from every file, saving clean copies next to the originals.", "Petal.Caption"));
            PrimaryButton.Visibility = Visibility.Collapsed;
            _removeAll.Style = (Style)FindResource("Petal.PrimaryButton");
            _removeAll.Margin = new Thickness(0);
            FooterNote = "Originals stay as they are.";
            return;
        }

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_search, Dock.Right);
        _search.ToolTip = "Search metadata";
        _search.TextChanged += (_, _) => Fill();
        header.Children.Add(_search);
        var name = Text(Path.GetFileName(files[0]), "Petal.Caption");
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(name);
        Body.Children.Add(header);
        Body.Children.Add(_summary);

        var scroll = new ScrollViewer { Content = _list, Height = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(scroll);
        Body.Children.Add(Text("For photos, Remove All also clears hidden camera and processing data. Colour and orientation are kept.", "Petal.Caption").WithMarginT(10));

        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) => SaveEdits();
        FooterNote = "Changes are saved to a copy.";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!ExifToolMetadata.IsAvailable)
        {
            _list.Children.Add(Text("ExifTool isn't available, so metadata can't be shown. Remove All still works for photos and media.", "Petal.Caption"));
            return;
        }
        _list.Children.Add(Text("Reading…", "Petal.Caption"));
        try
        {
            _fields = await ExifToolMetadata.ReadAsync(_files[0]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading metadata");
            _list.Children.Clear();
            _list.Children.Add(Text("Couldn't read this file's metadata.", "Petal.Caption"));
            return;
        }
        _summary.Children.Clear();
        foreach (var (label, value) in ExifToolMetadata.Summary(_fields))
            _summary.Children.Add(Row(label, Text(value), 90, 2));
        _summary.Visibility = _summary.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        var groups = _fields
            .Where(f => query.Length == 0 || f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase) || f.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Group)
            .OrderBy(g => GroupOrder(g.Key));
        foreach (var group in groups)
        {
            var title = Text(GroupTitle(group.Key), "Petal.Caption");
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 12, 0, 4);
            _list.Children.Add(title);
            foreach (var field in group)
                _list.Children.Add(FieldRow(field));
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(Text(query.Length > 0 ? "No matching fields." : "This file has no metadata.", "Petal.Caption"));
    }

    private UIElement FieldRow(MetadataField field)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        var label = Text(field.Name + ":", "Petal.Label");
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Margin = new Thickness(0, 0, 10, 0);
        label.ToolTip = $"{field.Group}:{field.Name}";
        grid.Children.Add(label);

        bool removed = _edits.TryGetValue(field.Key, out var edit) && edit.Value is null;
        UIElement value;
        if (field.Editable && !removed)
        {
            var box = new TextBox { Text = edit?.Value ?? field.Value, MinHeight = 26, Padding = new Thickness(6, 2, 6, 2) };
            box.TextChanged += (_, _) =>
            {
                if (box.Text == field.Value) _edits.Remove(field.Key);
                else _edits[field.Key] = new MetadataEdit(field.Group, field.Name, box.Text);
                PrimaryButton.IsEnabled = _edits.Count > 0;
            };
            value = box;
        }
        else
        {
            var t = Text(removed ? "Removed" : field.Value);
            t.TextWrapping = TextWrapping.Wrap;
            t.VerticalAlignment = VerticalAlignment.Center;
            if (removed) t.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
            value = t;
        }
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        if (field.Removable)
        {
            var minus = IconButton(removed ? "undo" : "minus", removed ? "Keep this field" : "Remove this field", 13);
            minus.Width = minus.Height = 24;
            minus.Click += (_, _) =>
            {
                if (removed) _edits.Remove(field.Key);
                else _edits[field.Key] = new MetadataEdit(field.Group, field.Name, null);
                PrimaryButton.IsEnabled = _edits.Count > 0;
                Fill();
            };
            Grid.SetColumn(minus, 2);
            grid.Children.Add(minus);
        }
        return grid;
    }

    private static int GroupOrder(string group) => group switch
    {
        _ when group.StartsWith("IFD0") => 0,
        "ExifIFD" => 1,
        "GPS" => 2,
        _ when group.StartsWith("XMP") => 3,
        "IPTC" => 4,
        "QuickTime" or "ItemList" or "Keys" or "UserData" => 5,
        "PDF" => 6,
        "ID3v2_3" or "ID3v2_4" or "ID3v1" or "Vorbis" => 7,
        "File" or "System" => 20,
        "Composite" => 21,
        _ => 10,
    };

    private static string GroupTitle(string group) => group switch
    {
        "IFD0" => "Image",
        "ExifIFD" => "Camera (EXIF)",
        "GPS" => "Location (GPS)",
        "File" or "System" => "File",
        "Composite" => "Calculated",
        "QuickTime" or "ItemList" or "Keys" or "UserData" => $"Movie ({group})",
        _ => group,
    };

    private void SaveEdits()
    {
        var edits = _edits.Values.ToList();
        var file = _files[0];
        Close();
        var output = OutputNaming.ForTool(file, _tool, "Edited");
        _ = _jobs.RunAsync("Saving metadata", "Metadata saved", [new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
        {
            await ExifToolMetadata.SaveEditsAsync(file, output, edits, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }

    private void RemoveAll()
    {
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
        {
            var output = OutputNaming.ForTool(file, _tool);
            await MetadataRemoval.RemoveAllAsync(file, output, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Removing metadata", "Metadata removed", work, _at);
    }
}

/// <summary>Strips metadata with ExifTool, or FFmpeg for media when ExifTool can't write the container.</summary>
public static class MetadataRemoval
{
    public static async Task RemoveAllAsync(string input, string output, CancellationToken ct)
    {
        try
        {
            await ExifToolMetadata.RemoveAllAsync(input, output, ct);
            if (File.Exists(output))
                return;
        }
        catch (Exception ex)
        {
            Log.Info($"ExifTool couldn't strip {input}, trying FFmpeg: {ex.Message}");
        }
        var ffmpeg = Petal.Core.Util.ToolLocator.Ffmpeg ?? throw new InvalidOperationException("No metadata tool is available.");
        var temp = output + ".petal-tmp" + Path.GetExtension(output);
        var result = await Petal.Core.Util.ProcessRunner.RunAsync(ffmpeg,
            ["-y", "-i", input, "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy", temp], cancellationToken: ct);
        if (!result.Succeeded)
            throw new Petal.Core.Util.ExternalToolException("FFmpeg", result);
        File.Move(temp, output);
    }
}
