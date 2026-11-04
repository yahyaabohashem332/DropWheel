using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Text;
using Petal.Core.Tools;
using Petal.Engines.Documents;
using Petal.Engines.Imaging;
using Petal.Recipes.Cloud;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Windows;

/// <summary>
/// The text on a picture in another language, where it stands: a menu, a sign, a slide, a screenshot. One picture is shown
/// translated, each piece of text can be corrected or left as it was, and a copy is saved; several go straight to work.
/// </summary>
public sealed class TranslatePictureWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly LanguageChoice _from = new("auto", [("auto", "Detect automatically")]);
    private readonly LanguageChoice _into = new(LanguageChoice.Interface);
    private readonly TextBlock _where = Text("", "Petal.Caption");
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly ModelPanel _model;
    private readonly ModelPanel _eraser = new(ObjectEraser.Models);
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
    private readonly CheckBox _original = new() { Content = L.T("Show the original"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _list = new() { MaxHeight = 190, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<PictureBlock> _erased = [];
    private readonly CancellationTokenSource _closing = new();
    private PictureWork? _work;
    private byte[]? _done;
    private string _guessed = "en";
    private string _readHint = "";
    private bool _translated;
    private bool _busy;

    public TranslatePictureWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : this(tool, files, jobs, at, null, null)
    {
    }

    /// <param name="ready">A picture already translated into <paramref name="into"/>, to look at this window with.</param>
    internal TranslatePictureWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, PictureWork? ready, string? into) : base(tool.Name, files.Count == 1 ? 620 : 440)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _model = new ModelPanel(Translator.ModelsFor("en", _into.Value == "en" ? "es" : _into.Value));
        _list.Content = _lines;
        if (files.Count == 1)
        {
            Body.Children.Add(PreviewPanel(_picture, 300));
            Body.Children.Add(_original);
        }
        else
        {
            Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
            Body.Children.Add(Text("A new picture is saved beside each original, with the language in its name.", "Petal.Caption").WithMarginT(6));
        }
        Body.Children.Add(Row("From", _from, 80, 12));
        Body.Children.Add(Row("Into", _into, 80, 8));
        _where.Margin = new Thickness(0, 10, 0, 0);
        _where.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_where);
        Body.Children.Add(_model);
        Body.Children.Add(_eraser);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        Body.Children.Add(_list);

        _from.Changed += _ => Choose();
        _into.Changed += _ => Choose();
        _model.Ready += Sync;
        _model.Switched += Sync;
        _eraser.Ready += Sync;
        _original.Checked += (_, _) => ShowCurrent();
        _original.Unchecked += (_, _) => ShowCurrent();
        _redraw.Tick += (_, _) =>
        {
            _redraw.Stop();
            _ = RedrawAsync();
        };
        PrimaryButton.Content = L.T("Translate");
        PrimaryButton.Click += async (_, _) =>
        {
            if (_files.Count > 1)
                Batch();
            else if (_translated)
                Save();
            else
                await TranslateAsync();
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _redraw.Stop();
        };
        _eraser.Wanted = files.Count > 1;
        if (ready is not null)
        {
            _work = ready;
            _into.Value = into ?? _into.Value;
            Loaded += async (_, _) =>
            {
                await PictureTranslation.EraseAsync(ready);
                _erased.UnionWith(ready.Blocks.Where(b => !b.Keep));
                _translated = true;
                Finish();
            };
        }
        else if (files.Count == 1)
            Loaded += async (_, _) => await ReadAsync();
        Choose();
    }

    private string From => _from.Value == "auto" ? _guessed : _from.Value;

    private bool RightToLeft => _into.Value is "ar" or "fa" or "ur";

    /// <summary>"menu.jpg" becomes "menu.ar.jpg"; a picture that can't be written comes out as a PNG.</summary>
    internal static string OutputFor(string file, string into) =>
        OutputNaming.Unique(OutputNaming.DirectoryFor(file), Path.GetFileNameWithoutExtension(file) + "." + into,
            Formats.FromPath(file) is { CanWrite: true } ? Path.GetExtension(file).TrimStart('.') : "png");

    private async Task ReadAsync()
    {
        _busy = true;
        _status.Text = L.T("Reading the picture…");
        Sync();
        try
        {
            var work = await PictureTranslation.ReadAsync(_files[0], _closing.Token);
            _work = work;
            _picture.Source = PictureTranslation.Show(work.Original, work.Width, work.Height);
            var text = string.Join(' ', work.Blocks.Select(b => b.Source));
            _guessed = LanguageGuess.Of(text) ?? "en";
            if (_from.Value == "auto" && _guessed == _into.Value)
                _into.Value = _guessed == "en" ? "es" : "en";
            _readHint = ReadingHint(_guessed);
            _status.Text = work.Blocks.Count == 0
                ? L.T("No text was found. Try a sharper scan, or add its language under Windows Settings › Time & language.")
                : _readHint;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Reading a picture to translate");
            _status.Text = L.T("Couldn't open this image.");
        }
        finally
        {
            _busy = false;
            Choose();
        }
    }

    /// <summary>Windows reads best in the languages it has been given: says so when this picture's isn't one.</summary>
    private static string ReadingHint(string language)
    {
        bool known = Translator.Languages.Any(l => l.Code == language);
        bool readable = DocumentEngine.OcrLanguages.Any(tag => tag.Split('-')[0] == language);
        return !known || readable ? "" : L.F("Windows has no reader for {0} on this PC, so some letters may be misread. Add the language under Settings › Time & language › Language.", LanguageChoice.NameOf(language));
    }

    /// <summary>A language chosen anew: what was translated for another one is gone.</summary>
    private void Choose()
    {
        if (_translated)
        {
            _translated = false;
            _done = null;
            _erased.Clear();
            _lines.Children.Clear();
            _list.Visibility = Visibility.Collapsed;
            _original.Visibility = Visibility.Collapsed;
            _original.IsChecked = false;
            _status.Text = _readHint;
            if (_work is { } work)
            {
                foreach (var block in work.Blocks)
                {
                    block.Translation = "";
                    block.Keep = !PictureTranslation.Trusted(block.Source);
                }
                _picture.Source = PictureTranslation.Show(work.Original, work.Width, work.Height);
            }
            PrimaryButton.Content = L.T("Translate");
        }
        _model.Models = Translator.ModelsFor(From, _into.Value);
        if (_files.Count == 1)
            _eraser.Wanted = _work?.NeedsEraser == true;
        Sync();
    }

    private void Sync()
    {
        bool same = _from.Value == _into.Value;
        _where.Text = same ? L.T("The file is already in this language.")
            : CloudAi.CanTranslate ? L.F("The text goes to {0}, on your own API key.", CloudAi.Name(CloudAi.Translating!.Provider))
            : L.T("It runs on this PC: the file doesn't leave it.");
        bool can = !_busy && !same && (_translated || (_model.IsReady && (!_eraser.Wanted || _eraser.IsReady)));
        PrimaryButton.IsEnabled = can && (_files.Count > 1 || _work is { Blocks.Count: > 0 });
        FooterNote = _files.Count == 1 ? Path.GetFileName(OutputFor(_files[0], _into.Value)) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private async Task TranslateAsync()
    {
        if (_work is not { } work)
            return;
        _busy = true;
        Sync();
        try
        {
            var progress = new Progress<double>(f => _status.Text = L.T("Translating…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
            _status.Text = L.T("Translating…");
            await PictureTranslation.TranslateAsync(work, _from.Value, _into.Value, progress, _closing.Token);
            _status.Text = L.T("Taking the text off…");
            await PictureTranslation.EraseAsync(work, null, _closing.Token);
            _erased.Clear();
            _erased.UnionWith(work.Blocks.Where(b => !b.Keep));
            _translated = true;
            Finish();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Translating a picture");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    /// <summary>The picture translated, and its lines to correct.</summary>
    private void Finish()
    {
        if (_work is not { } work)
            return;
        Compose(work);
        Rows(work);
        PrimaryButton.Content = L.T("Save Copy");
        _original.Visibility = Visibility.Visible;
        _list.Visibility = _lines.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Report(work);
        Sync();
    }

    private void Report(PictureWork work)
    {
        _status.Text = work.Blocks.All(b => b.Keep) ? L.T("Nothing here needed translating.")
            : work.Blocks.Any(b => !b.Keep && b.Tight) ? L.T("Some of the text had to go smaller to fit: shorten it here if you like.")
            : L.T("Correct any line here and the picture follows.");
    }

    private void Compose(PictureWork work)
    {
        _done = PictureTranslation.Compose(work, _into.Value);
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_work is not { } work)
            return;
        _picture.Source = PictureTranslation.Show(_original.IsChecked == true || _done is null ? work.Original : _done, work.Width, work.Height);
    }

    private void Rows(PictureWork work)
    {
        _lines.Children.Clear();
        foreach (var block in work.Blocks.Where(b => b.Source.Any(char.IsLetter)))
        {
            var source = Text(block.Source, "Petal.Caption");
            source.TextWrapping = TextWrapping.Wrap;
            source.FlowDirection = block.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            var box = new TextBox
            {
                Text = block.Translation,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                FlowDirection = RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var keep = new CheckBox { Content = L.T("Keep as it is"), IsChecked = block.Keep, Margin = new Thickness(0, 4, 0, 0) };
            box.TextChanged += (_, _) =>
            {
                block.Translation = box.Text;
                if (box.IsKeyboardFocused && box.Text.Trim().Length > 0)
                    keep.IsChecked = false;
                Later();
            };
            keep.Checked += (_, _) =>
            {
                block.Keep = true;
                Later();
            };
            keep.Unchecked += (_, _) =>
            {
                block.Keep = false;
                Later();
            };
            _lines.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 8, 10), Children = { source, box, keep } });
        }
    }

    private void Later()
    {
        _redraw.Stop();
        _redraw.Start();
    }

    /// <summary>After a correction. Text that was kept until now has to come off the picture first.</summary>
    private async Task RedrawAsync()
    {
        if (_work is not { } work || _busy || !_translated)
            return;
        var newly = work.Blocks.Where(b => !b.Keep && !_erased.Contains(b)).ToList();
        if (newly.Any(b => !b.Plain) && !_eraser.IsReady)
        {
            _eraser.Wanted = true;
            Sync();
            _status.Text = L.T("Some of this text sits on a photo: the eraser model rebuilds what is behind it.");
            return;
        }
        if (newly.Count > 0)
        {
            _busy = true;
            Sync();
            _status.Text = L.T("Taking the text off…");
            try
            {
                await PictureTranslation.EraseAsync(work, null, _closing.Token);
                _erased.UnionWith(newly);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Taking text off a picture");
                _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
                return;
            }
            finally
            {
                _busy = false;
                Sync();
            }
        }
        Compose(work);
        Report(work);
    }

    private void Save()
    {
        if (_work is not { } work)
            return;
        var pixels = PictureTranslation.Compose(work, _into.Value);
        var output = OutputFor(_files[0], _into.Value);
        Close();
        _ = _jobs.RunAsync("Saving", "Translated", [new WorkItem(_files[0], Path.GetFileName(_files[0]), async (_, ct) =>
        {
            await PictureTranslation.SaveAsync(work, pixels, output, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }

    private void Batch()
    {
        var (from, into) = (_from.Value, _into.Value);
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputFor(file, into);
            await PictureTranslation.TranslateFileAsync(file, output, from, into, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Translating", "Translated", work, _at);
    }
}
