using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Ask Petal: say what to do with the selected files, in English or Arabic. Petal shows the steps
/// it understood before anything runs; they can be run once or saved as a recipe.
/// </summary>
public sealed class AskWindow : ToolWindow
{
    private static AskWindow? _open;

    private readonly IReadOnlyList<string> _files;
    private readonly IReadOnlyList<FileFormat> _formats;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly TextBox _request = new() { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLength = 400 };
    private readonly Button _ask = new() { Content = L.T("Ask"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 };
    private readonly TextBlock _thinking;
    private readonly StackPanel _plan = new() { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ModelPanel _model = new(AskPetal.Models);
    private readonly TextBlock _modelHint;
    private CancellationTokenSource? _cts;
    private Recipe? _recipe;
    private readonly Button _mic;
    private Words.Listener? _listener;

    public static void Show(IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, string? request = null)
    {
        _open?.Close();
        var window = _open = new AskWindow(files, jobs, at);
        window.Closed += (_, _) => _open = null;
        window.ShowNear(at);
        // From the command palette: the request is already typed, so ask straight away.
        if (!string.IsNullOrWhiteSpace(request))
        {
            window._request.Text = request;
            window.Loaded += async (_, _) => await window.AskAsync();
        }
    }

    /// <summary>From the tray: files chosen first.</summary>
    public static void ChooseAndShow(JobRunner jobs)
    {
        var dialog = new OpenFileDialog { Title = L.T("Ask Petal about"), Multiselect = true };
        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            _open?.Close();
            _open = new AskWindow(dialog.FileNames, jobs, new ScreenPoint(0, 0));
            _open.Closed += (_, _) => _open = null;
            _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Show();
            _open.Activate();
        }
    }

    private AskWindow(IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base("Ask Petal", 460)
    {
        _files = files;
        _jobs = jobs;
        _at = at;
        _formats = files.Select(Formats.FromPath).OfType<FileFormat>().ToList();
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(Text("Say what to do with them, in your own words.", "Petal.Caption").WithMarginT(4));

        _request.FlowDirection = L.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _request.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = AskAsync();
            }
        };
        _request.TextChanged += (_, _) => _ask.IsEnabled = _request.Text.Trim().Length > 0;
        _ask.IsEnabled = false;
        _ask.Click += (_, _) => _ = AskAsync();
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_ask, Dock.Right);
        row.Children.Add(_ask);
        _mic = IconButton("mic", "Say it (press again when done)", 16);
        _mic.Margin = new Thickness(6, 0, 0, 0);
        _mic.Click += async (_, _) => await ListenAsync();
        DockPanel.SetDock(_mic, Dock.Right);
        row.Children.Add(_mic);
        row.Children.Add(_request);
        Body.Children.Add(row);
        Body.Children.Add(Text(L.T("For example: “make them 1080 px WEBP and zip them”, “اضغطها للواتساب”, “put them in one searchable PDF”."), "Petal.Caption").WithMarginT(6));

        _thinking = Text("", "Petal.Caption");
        _thinking.Margin = new Thickness(0, 10, 0, 0);
        _thinking.Visibility = Visibility.Collapsed;
        Body.Children.Add(_thinking);
        Body.Children.Add(_plan);

        _modelHint = Text("With the AI model, Petal also understands requests worded freely. It runs on this PC.", "Petal.Caption");
        _modelHint.Margin = new Thickness(0, 14, 0, 0);
        _modelHint.Visibility = AskPetal.ModelReady ? Visibility.Collapsed : Visibility.Visible;
        Body.Children.Add(_modelHint);
        Body.Children.Add(_model);
        _model.Ready += () => _modelHint.Visibility = Visibility.Collapsed;

        PrimaryButton.Content = L.T("Run");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) => Run();
        Loaded += (_, _) => _request.Focus();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _listener?.Dispose();
        };
    }

    /// <summary>The request said out loud: the first press listens, the second writes it down and asks.</summary>
    private async Task ListenAsync()
    {
        if (_listener is null)
        {
            if (Words.Listener.ReadyModel() is null)
            {
                Words.DictationSetupWindow.ShowSingle();
                return;
            }
            _listener = new Words.Listener();
            try
            {
                _listener.Start();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                _listener.Dispose();
                _listener = null;
                _thinking.Text = L.T("The microphone couldn't be opened.");
                _thinking.Visibility = Visibility.Visible;
                return;
            }
            _mic.Content = ThemedIcon("square", 14, "Petal.Danger");
            _thinking.Text = L.T("Listening… press the button again when done.");
            _thinking.Visibility = Visibility.Visible;
            return;
        }
        var listener = _listener;
        _listener = null;
        _mic.IsEnabled = false;
        _thinking.Text = L.T("Writing it down…");
        try
        {
            var said = await listener.FinishAsync();
            if (said.Length > 0)
            {
                _request.Text = said;
                await AskAsync();
            }
            else
            {
                _thinking.Text = L.T("Nothing was heard.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Asking out loud");
            _thinking.Text = L.T("It didn't work. The activity log has the details.");
        }
        finally
        {
            listener.Dispose();
            _mic.IsEnabled = true;
            _mic.Content = ThemedIcon("mic", 16);
        }
    }

    private async Task AskAsync()
    {
        var request = _request.Text.Trim();
        if (request.Length == 0)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _ask.IsEnabled = false;
        _plan.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = false;
        _thinking.Text = L.T(AskPetal.ModelReady ? "Thinking…" : "Reading…");
        _thinking.Visibility = Visibility.Visible;
        try
        {
            var answer = await Task.Run(() => AskPetal.AskAsync(request, _formats, cts.Token));
            if (cts != _cts)
                return;
            ShowPlan(answer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Asking Petal");
            // The words alone, when the model can't run.
            ShowPlan(new AskAnswer(AskPetal.Understand(request, _formats), false));
        }
        finally
        {
            if (cts == _cts)
            {
                _thinking.Visibility = Visibility.Collapsed;
                _ask.IsEnabled = _request.Text.Trim().Length > 0;
            }
        }
    }

    private void ShowPlan(AskAnswer answer)
    {
        _plan.Children.Clear();
        _plan.Visibility = Visibility.Visible;
        _recipe = answer.Recipe;
        if (_recipe is null)
        {
            _plan.Children.Add(Text(L.T(AskPetal.ModelReady
                ? "Petal didn't find something it can do in that. Try naming the result: a format, a size, an app to send to, one PDF or a ZIP."
                : "Petal didn't understand that yet. Try simpler words, or download the AI model below to ask any way you like."), "Petal.Body"));
            Sync();
            return;
        }
        var heading = Text("Petal will:", "Petal.Label");
        _plan.Children.Add(heading);
        int n = 1;
        foreach (var step in _recipe.Steps)
        {
            // On its own a convert step is just a format ("WEBP"); say what it does.
            var words = step.Kind == RecipeStepKind.Convert ? L.F("Convert to {0}", RecipeText.Describe(step)) : RecipeText.Describe(step);
            var line = Text($"{n++}. {words}", "Petal.Body");
            line.Margin = new Thickness(8, 4, 0, 0);
            _plan.Children.Add(line);
        }
        if (!_recipe.AppliesTo(_formats, _jobs.Converters))
        {
            var mismatch = Text(L.T("These steps don't fit the selected files.") + " " + RecipeText.Accepts(_recipe), "Petal.Caption");
            mismatch.Margin = new Thickness(0, 8, 0, 0);
            mismatch.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            _plan.Children.Add(mismatch);
        }
        var save = new Button { Content = L.T("Save as Recipe…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) => SaveAsRecipe();
        _plan.Children.Add(save);
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = _recipe is not null && _recipe.AppliesTo(_formats, _jobs.Converters);
        FooterNote = _recipe is null ? "" : L.F("Results are saved beside the originals as “name {0}”.", _recipe.Name);
    }

    private void SaveAsRecipe()
    {
        if (_recipe is null)
            return;
        var draft = _recipe.Copy();
        draft.Id = Guid.NewGuid();
        draft.OnWheel = true;
        var editor = new Settings.RecipeWindow(draft, saved =>
        {
            SettingsStore.Current.Recipes.Add(saved);
            SettingsStore.Save();
        });
        editor.Owner = this;
        editor.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        editor.Show();
    }

    /// <summary>For design snapshots: a request already answered by the word clues.</summary>
    internal static AskWindow ForSnapshot(IReadOnlyList<string> files, JobRunner jobs)
    {
        var window = new AskWindow(files, jobs, new ScreenPoint(0, 0));
        window._request.Text = L.IsArabic ? "صغّرها إلى 1080 بكسل وحوّلها إلى WEBP ثم ضعها في ملف مضغوط" : "make them 1080 px WEBP and zip them";
        window.ShowPlan(new AskAnswer(AskPetal.Understand(window._request.Text, window._formats), false));
        return window;
    }

    private void Run()
    {
        if (_recipe is null)
            return;
        var recipe = _recipe;
        var files = _files;
        Close();
        _ = _jobs.RunRecipeAsync(files, recipe, _at);
    }
}
