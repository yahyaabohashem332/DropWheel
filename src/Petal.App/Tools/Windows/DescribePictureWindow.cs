using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Tools;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Describe Picture: what a picture shows in a sentence (alt text for a website or a post) or a paragraph,
/// written by a small picture model on this PC. In Arabic it is translated on this PC too. The words can be
/// copied, or kept in the picture's own details where its format allows that without changing the picture.
/// </summary>
public sealed class DescribePictureWindow : ToolWindow
{
    private readonly string _file;
    private readonly Segmented<bool> _length;
    private readonly ModelPanel _model = new(PictureDescriber.Models);
    private readonly ModelPanel _translation = new(Petal.Recipes.Translation.Translator.Models) { Visibility = Visibility.Collapsed };
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status;
    private readonly Button _keep;
    private CancellationTokenSource? _running;

    public DescribePictureWindow(ToolInfo tool, string file) : base(tool.Name, 460)
    {
        _file = file;
        var picture = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
        Body.Children.Add(PreviewPanel(picture, 190));
        _ = LoadPreviewAsync(picture);
        _length = Segments([(false, "A sentence"), (true, "A paragraph")], false);
        Body.Children.Add(Row("Length", _length, 90, 12));
        _status = Text("", "Petal.Caption");
        _status.Margin = new Thickness(0, 12, 0, 6);
        Body.Children.Add(_status);
        Body.Children.Add(_text);
        Body.Children.Add(_model);
        Body.Children.Add(_translation);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _keep = new Button { Content = L.T("Save in the Picture"), Margin = new Thickness(0, 0, 8, 6), IsEnabled = false };
        _keep.ToolTip = L.T("Writes the description into the picture's details, where photo apps and websites can read it. The picture itself doesn't change.");
        _keep.Click += async (_, _) => await KeepAsync();
        actions.Children.Add(_keep);
        Body.Children.Add(actions);
        _text.TextChanged += (_, _) =>
        {
            _text.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(_text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            bool has = _text.Text.Trim().Length > 0;
            _keep.IsEnabled = has && MetadataCanBeKept(_file);
            PrimaryButton.IsEnabled = has;
        };
        _length.Changed += async _ => await DescribeAsync();
        _model.Ready += async () => await DescribeAsync();
        _translation.Ready += async () => await DescribeAsync();
        PrimaryButton.Content = L.T("Copy");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) => Copy();
        FooterNote = Petal.Recipes.Cloud.CloudAi.CanSee
            ? L.F("Written by {0}, on your own key; check it before you publish it.", Petal.Recipes.Cloud.CloudAi.Name(Petal.Recipes.Cloud.CloudAi.Seeing!.Provider))
            : "Written on this PC by a small AI model; check it before you publish it.";
        Loaded += async (_, _) => await DescribeAsync();
        Closed += (_, _) => _running?.Cancel();
    }

    private async Task LoadPreviewAsync(Image target)
    {
        try
        {
            var png = await ImagePreview.DecodePngAsync(_file, 600);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            target.Source = image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Info($"No preview for Describe Picture: {ex.Message}");
        }
    }

    private async Task DescribeAsync()
    {
        if (!PictureDescriber.ModelReady)
        {
            _status.Text = L.T("Describing pictures needs the picture model, downloaded once.");
            _model.Visibility = Visibility.Visible;
            _text.Visibility = _keep.Visibility = Visibility.Collapsed;
            return;
        }
        _model.Visibility = Visibility.Collapsed;
        _text.Visibility = _keep.Visibility = Visibility.Visible;
        _running?.Cancel();
        var running = _running = new CancellationTokenSource();
        _status.Text = L.T("Looking at the picture…");
        _text.Text = "";
        var work = Path.Combine(Path.GetTempPath(), "Petal", $"describe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(work);
            // Any format Petal opens, made small: the model sees little more, and larger only costs time.
            var small = Path.Combine(work, "picture.png");
            await File.WriteAllBytesAsync(small, await ImagePreview.DecodePngAsync(_file, PictureDescriber.Side, running.Token), running.Token);
            var english = await PictureDescriber.DescribeAsync(small, _length.Value, running.Token);
            if (running != _running)
                return;
            string text = english;
            // In any language but English the words are translated on this PC.
            if (L.Code != "en")
            {
                var missing = Words.Translation.Missing(english);
                if (missing.Count > 0)
                {
                    _translation.Models = missing;
                    _translation.Visibility = Visibility.Visible;
                    _status.Text = L.T("The description is in English. Download the model to have it in your language.");
                }
                else
                {
                    _translation.Visibility = Visibility.Collapsed;
                    _status.Text = Words.Translation.Working();
                    text = await Words.Translation.RunAsync(english, running.Token);
                }
            }
            if (running != _running)
                return;
            _text.Text = text;
            _status.Text = text.Length == 0 ? L.T("The picture couldn't be described.") : L.T("You can change the words before copying.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A picture that can't be opened, a model that stops: said, never left "looking".
            Log.Info($"Describe Picture: {ex.Message}");
            if (running == _running)
                _status.Text = L.T("The picture couldn't be described.");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Copy()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
            HudCard.ShowAt(HudCard.CornerPoint(), "", "").Succeed(L.T("Description copied"), text.Length > 60 ? text[..60] + "…" : text, null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    /// <summary>JPEG, PNG and WebP keep their pixels when their details change; other formats would be saved again.</summary>
    internal static bool MetadataCanBeKept(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";

    /// <summary>The description into the picture's details (EXIF ImageDescription), replacing the file only when that worked.</summary>
    private async Task KeepAsync()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0 || !MetadataCanBeKept(_file))
            return;
        var temporary = Path.Combine(Path.GetDirectoryName(_file)!, "." + Path.GetFileNameWithoutExtension(_file) + ".petal" + Path.GetExtension(_file));
        try
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            await Petal.Engines.Imaging.ImageMetadata.ApplyAsync(_file, temporary, [new MetadataChange.Set(MetadataIds.ImageDescription, text)]);
            File.Replace(temporary, _file, null);
            HudCard.ShowAt(HudCard.CornerPoint(), "", "").Succeed(L.T("Description saved in the picture"), Path.GetFileName(_file), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            HudCard.ShowAt(HudCard.CornerPoint(), "", "").Fail(L.T("The description couldn't be saved"), ex.Message);
        }
    }
}
