using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Screen;

/// <summary>A recorded guide: its title and steps to edit, reorder or remove, saved as PDF, Word, a web page or Markdown.</summary>
internal sealed class StepGuideWindow : ToolWindow
{
    private readonly List<GuideStep> _steps;
    private readonly string _folder;
    private Task<string>? _saving;
    private readonly TextBox _title = new() { FontSize = 15 };
    private readonly StackPanel _list = new();
    private readonly Segmented<string> _format;

    public static void Open(IReadOnlyList<GuideStep> steps, string folder)
    {
        var window = new StepGuideWindow(steps, folder) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Show();
        window.Activate();
    }

    /// <summary>For design snapshots, with steps already made.</summary>
    internal static StepGuideWindow ForSnapshot(IReadOnlyList<GuideStep> steps) => new(steps, Path.GetTempPath());

    private StepGuideWindow(IReadOnlyList<GuideStep> steps, string folder) : base("Step-by-Step Guide", 720)
    {
        _steps = [.. steps];
        _folder = folder;
        _title.Text = L.F("Steps · {0}", DateTime.Now.ToString("d MMMM", L.Culture));
        Body.Children.Add(Row("Title", _title, 90, 0));
        Body.Children.Add(new ScrollViewer { Content = _list, Height = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) });
        _format = Segments([("pdf", "PDF"), ("docx", "Word"), ("html", "Web Page"), ("md", "Markdown")], "pdf");
        Body.Children.Add(Row("Save as", _format, 90, 12));
        PrimaryButton.Content = L.T("Save Guide");
        PrimaryButton.Click += async (_, _) => await SaveAsync();
        Closed += async (_, _) =>
        {
            // The pictures were only needed until the guide was saved (and a save under way still needs them).
            if (_saving is { } saving)
            {
                try { await saving; }
                catch (Exception) { /* SaveAsync has said so already. */ }
            }
            try
            {
                if (_folder.StartsWith(Path.Combine(Path.GetTempPath(), "DropWheel Steps"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(_folder))
                    Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        for (int i = 0; i < _steps.Count; i++)
            _list.Children.Add(StepRow(i));
        FooterNote = L.P(_steps.Count, "{0} step", "{0} steps");
        PrimaryButton.IsEnabled = _steps.Count > 0;
    }

    private UIElement StepRow(int index)
    {
        var step = _steps[index];
        var number = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            Child = new TextBlock { Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        number.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        var text = new TextBox { Text = step.Text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        text.TextChanged += (_, _) => step.Text = text.Text;
        var words = new StackPanel { Children = { text } };
        if (step.Picture.Length > 0 && File.Exists(step.Picture))
        {
            var picture = new Image { Source = Load(step.Picture), MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            words.Children.Add(new Border { Child = picture, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left });
        }
        var tools = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var up = IconButton("chevronUp", "Move up");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => Move(index, -1);
        var down = IconButton("chevronDown", "Move down");
        down.IsEnabled = index < _steps.Count - 1;
        down.Click += (_, _) => Move(index, 1);
        var remove = IconButton("trash", "Remove this step");
        remove.Click += (_, _) =>
        {
            _steps.RemoveAt(index);
            Fill();
        };
        tools.Children.Add(up);
        tools.Children.Add(down);
        tools.Children.Add(remove);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(number, Dock.Left);
        DockPanel.SetDock(tools, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(tools);
        row.Children.Add(words);
        return row;
    }

    private void Move(int index, int by)
    {
        int to = index + by;
        if (to < 0 || to >= _steps.Count)
            return;
        (_steps[index], _steps[to]) = (_steps[to], _steps[index]);
        Fill();
    }

    private static BitmapImage Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 900;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>The guide as Markdown, its pictures named as they are in <paramref name="pictures"/> (copied beside it).</summary>
    internal static string Markdown(string title, IReadOnlyList<GuideStep> steps, Func<GuideStep, string?> pictures)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title.Trim().Length > 0 ? title.Trim() : L.T("Step-by-Step Guide"));
        sb.AppendLine();
        for (int i = 0; i < steps.Count; i++)
        {
            // Escape what Markdown would read as formatting in the step's words.
            var words = steps[i].Text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("#", "\\#");
            sb.Append("## ").Append(i + 1).Append(". ").AppendLine(words);
            sb.AppendLine();
            if (pictures(steps[i]) is { } picture)
            {
                sb.Append("![](").Append(Uri.EscapeDataString(picture)).AppendLine(")");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private async Task SaveAsync()
    {
        PrimaryButton.IsEnabled = false;
        FooterNote = "Saving the guide…";
        try
        {
            var title = _title.Text;
            var format = _format.Value;
            var steps = _steps.ToList();
            var saving = Task.Run(() => Save(title, steps, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DropWheel Guides"), format,
                App.Current.Controller.Jobs.Converters, Settings.SettingsStore.Current.Conversion.Clone()));
            _saving = saving;
            var path = await saving;
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Guide saved"), Path.GetFileName(path), () =>
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
            FooterNote = L.F("Saved in {0}", Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving a guide");
            FooterNote = "The guide couldn't be saved";
        }
        finally
        {
            _saving = null;
            PrimaryButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes the guide into <paramref name="folder"/>: Markdown with a folder of its pictures, or one PDF,
    /// Word file or web page with the pictures inside it. Returns the file's path.
    /// </summary>
    internal static async Task<string> Save(string title, IReadOnlyList<GuideStep> steps, string folder, string format, ConverterRegistry converters, ConversionSettings settings)
    {
        Directory.CreateDirectory(folder);
        var stem = Petal.Recipes.SmartNamer.Tidy(title) ?? L.T("Step-by-Step Guide");
        if (format == "md")
        {
            var md = OutputNaming.Unique(folder, stem, "md");
            var pictures = Path.Combine(folder, Path.GetFileNameWithoutExtension(md) + " pictures");
            Directory.CreateDirectory(pictures);
            int n = 0;
            var names = new Dictionary<GuideStep, string>();
            foreach (var step in steps.Where(s => File.Exists(s.Picture)))
            {
                var name = $"step-{++n:D3}.png";
                File.Copy(step.Picture, Path.Combine(pictures, name), overwrite: true);
                names[step] = Path.GetFileName(pictures) + "/" + name;
            }
            await File.WriteAllTextAsync(md, MarkdownWithFolder(title, steps, names), new UTF8Encoding(false));
            return md;
        }
        // The other formats: Markdown beside the pictures, converted by Petal's own document engine.
        var work = Path.Combine(Path.GetTempPath(), "Petal Guide " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var source = Path.Combine(work, "guide.md");
            await File.WriteAllTextAsync(source, Markdown(title, steps, s => File.Exists(s.Picture) ? CopyInto(work, s.Picture) : null), new UTF8Encoding(false));
            var target = Formats.FromId(format) ?? Formats.Pdf;
            var output = OutputNaming.Unique(folder, stem, target.WriteExtension);
            var converter = converters.Find(Formats.Md, target) ?? throw new InvalidOperationException($"No way from Markdown to {target.Label}");
            await converter.ConvertAsync(new ConversionJob(source, Formats.Md, target, output, settings), null, CancellationToken.None);
            return output;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static string MarkdownWithFolder(string title, IReadOnlyList<GuideStep> steps, Dictionary<GuideStep, string> names) =>
        Markdown(title, steps, s => names.GetValueOrDefault(s)).Replace("%2F", "/");

    private static string CopyInto(string folder, string picture)
    {
        var name = Path.GetFileName(picture);
        File.Copy(picture, Path.Combine(folder, name), overwrite: true);
        return name;
    }
}
