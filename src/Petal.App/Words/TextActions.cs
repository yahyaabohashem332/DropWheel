using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Tools.Windows;
using Petal.App.Wheels;
using Petal.Core.Text;
using Petal.Recipes;

namespace Petal.App.Words;

/// <summary>
/// What the text petals do with the text selected in any app: put it right in place (layout, case,
/// diacritics, digits, spaces), or tell something about it (count, sum), or open a window with a
/// translation, a summary or a rewrite to put in its place.
/// </summary>
internal static class TextActions
{
    /// <summary>Changes made where the text is, by petal id.</summary>
    private static readonly Dictionary<string, Func<string, string>> InPlace = new()
    {
        ["text.fixlayout"] = KeyboardLayout.Fix,
        ["text.upper"] = TextTools.Upper,
        ["text.lower"] = TextTools.Lower,
        ["text.title"] = TextTools.Title,
        ["text.sentence"] = TextTools.Sentence,
        ["text.diacritics"] = TextTools.RemoveDiacritics,
        ["text.digits"] = TextTools.SwapDigits,
        ["text.tidy"] = TextTools.TidySpaces,
    };

    public static async Task RunAsync(string id, WheelActionContext c)
    {
        // Let the wheel leave and the app have the keyboard back.
        await Task.Delay(120);
        var text = await SelectedText.GetAsync(c.Target);
        if (string.IsNullOrWhiteSpace(text))
        {
            c.Host.Card(c.At, L.T("No text is selected"), L.T("Select some text in the app first."), false);
            return;
        }
        if (InPlace.TryGetValue(id, out var change))
        {
            var changed = change(text);
            if (changed == text)
            {
                c.Host.Card(c.At, L.T("Nothing to change"), "", true);
                return;
            }
            if (!await SelectedText.ReplaceAsync(c.Target, changed))
            {
                SelectedText.TrySet(changed);
                c.Host.Card(c.At, L.T("Copied instead"), L.T("The app in front changed, so the text is on the clipboard."), true);
                return;
            }
            if (id == "text.fixlayout")
                SwitchKeyboard(c.Target, KeyboardLayout.LooksArabic(changed));
            return;
        }
        switch (id)
        {
            case "text.count":
            {
                var count = TextTools.Count(text);
                c.Host.Card(c.At, L.P(count.Words, "{0} word", "{0} words"),
                    L.F("{0} characters · {1} without spaces · {2} lines · {3} sentences", count.Characters, count.CharactersWithoutSpaces, count.Lines, count.Sentences), true);
                break;
            }
            case "text.calculate":
            {
                if (Calculator.Evaluate(text) is not { } answer)
                {
                    c.Host.Card(c.At, L.T("That isn't a sum"), L.T("Select a sum like 12% of 340, or 5 km in miles."), false);
                    break;
                }
                SelectedText.TrySet(answer.Text);
                c.Host.Card(c.At, "= " + answer.Shown, L.T("Copied"), true);
                break;
            }
            case "text.translate":
                TextResultWindow.Open(TextJob.Translate, text, c.Target, c.At);
                break;
            case "text.summarize":
                TextResultWindow.Open(TextJob.Summarize, text, c.Target, c.At);
                break;
            case "text.rewrite":
                TextResultWindow.Open(TextJob.Rewrite, text, c.Target, c.At);
                break;
        }
    }

    /// <summary>After fixing the layout, the keyboard switches to the language that was meant, so typing goes on right.</summary>
    private static void SwitchKeyboard(IntPtr target, bool arabic)
    {
        int count = ScreenNative.GetKeyboardLayoutList(0, null);
        if (count <= 1)
            return;
        var layouts = new IntPtr[count];
        ScreenNative.GetKeyboardLayoutList(count, layouts);
        var wanted = layouts.FirstOrDefault(h => ((long)h & 0x3FF) == (arabic ? 0x01 : 0x09));
        if (wanted != IntPtr.Zero)
            ScreenNative.PostMessage(target, ScreenNative.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, wanted);
    }
}

public enum TextJob
{
    Translate,
    Summarize,
    Rewrite,
}

/// <summary>
/// A translation, summary or rewrite of the selected text, made on this PC, to read, change, copy, or put
/// in place of the selection.
/// </summary>
internal sealed class TextResultWindow : ToolWindow
{
    private readonly TextJob _job;
    private readonly string _text;
    private readonly IntPtr _target;
    private readonly TextBox _result = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status;
    private readonly Tools.Parts.LanguageChoice? _into;
    private readonly ModelPanel _model;
    private readonly Segmented<string>? _style;
    private CancellationTokenSource? _cts;

    public static void Open(TextJob job, string text, IntPtr target, ScreenPoint at)
    {
        var window = new TextResultWindow(job, text, target);
        window.ShowNear(at);
        window.Activate();
    }

    internal static TextResultWindow ForSnapshot(TextJob job, string text) => new(job, text, IntPtr.Zero);

    private TextResultWindow(TextJob job, string text, IntPtr target) : base(job switch
    {
        TextJob.Translate => "Translate",
        TextJob.Summarize => "Summarize",
        _ => "Rewrite",
    }, 480)
    {
        _job = job;
        _text = text;
        _target = target;
        var original = Text(text.Length > 300 ? text[..300] + "…" : text, "Petal.Caption");
        original.TextTrimming = TextTrimming.CharacterEllipsis;
        original.MaxHeight = 54;
        original.TextWrapping = TextWrapping.Wrap;
        original.FlowDirection = KeyboardLayout.LooksArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        original.TextAlignment = original.FlowDirection == L.Direction ? TextAlignment.Left : TextAlignment.Right;
        Body.Children.Add(original);
        if (job == TextJob.Rewrite)
        {
            _style = Segments([("clear", "Clearer"), ("short", "Shorter"), ("formal", "Formal"), ("friendly", "Friendly"), ("fix", "Fix mistakes")], "clear");
            _style.Changed += value => _ = RunAsync();
            Body.Children.Add(Row("Make it", _style, 80, 12));
        }
        else if (job == TextJob.Summarize)
        {
            _style = Segments([("short", "A few lines"), ("points", "Key points")], "short");
            _style.Changed += value => _ = RunAsync();
            Body.Children.Add(Row("As", _style, 80, 12));
        }
        _status = Text("", "Petal.Caption");
        _status.Margin = new Thickness(0, 12, 0, 6);
        Body.Children.Add(_status);
        Body.Children.Add(_result);
        if (job == TextJob.Translate)
        {
            _into = new Tools.Parts.LanguageChoice(Translation.Into(text));
            _into.Changed += code =>
            {
                Settings.SettingsStore.Current.TranslateInto = code;
                Settings.SettingsStore.Save();
                var missing = Translation.Missing(_text, code);
                if (missing.Count > 0)
                {
                    _model!.Models = missing;
                    _model.Visibility = Visibility.Visible;
                }
                else
                {
                    _model!.Visibility = Visibility.Collapsed;
                    _ = RunAsync();
                }
            };
            Body.Children.Insert(Body.Children.IndexOf(_status), Row("Into", _into, 80, 12));
        }
        var models = job == TextJob.Translate ? Translation.Missing(text, _into!.Value) : AskPetal.ModelReady ? [] : AskPetal.Models;
        _model = new ModelPanel(models.Count > 0 ? models : AskPetal.Models) { Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        var where = Text("", "Petal.Caption").WithMarginT(8);
        void Where()
        {
            var cloud = job == TextJob.Translate ? (Petal.Recipes.Cloud.CloudAi.CanTranslate ? Petal.Recipes.Cloud.CloudAi.Translating : null) : Petal.Recipes.Cloud.CloudAi.CanWrite ? Petal.Recipes.Cloud.CloudAi.Writing : null;
            where.Text = cloud is null ? L.T("Runs on this PC; the text isn't sent anywhere.") : L.F("The text goes to {0}, on your own API key.", Petal.Recipes.Cloud.CloudAi.Name(cloud.Provider));
        }
        Where();
        _model.Ready += () =>
        {
            _model.Visibility = Visibility.Collapsed;
            Where();
            _ = RunAsync();
        };
        Body.Children.Add(_model);
        Body.Children.Add(where);
        _result.TextChanged += (_, _) =>
        {
            _result.FlowDirection = KeyboardLayout.LooksArabic(_result.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            PrimaryButton.IsEnabled = ResetButton.IsEnabled = _result.Text.Trim().Length > 0;
        };
        PrimaryButton.Content = L.T("Replace the Selection");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += async (_, _) =>
        {
            var result = _result.Text;
            Close();
            if (!await SelectedText.ReplaceAsync(_target, result))
            {
                SelectedText.TrySet(result);
                Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Copied instead"), L.T("The app in front changed, so the text is on the clipboard."), null);
            }
        };
        ResetButton.Content = L.T("Copy");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.IsEnabled = false;
        ResetButton.Click += (_, _) =>
        {
            if (SelectedText.TrySet(_result.Text))
                FooterNote = "Copied";
        };
        Loaded += (_, _) =>
        {
            if (models.Count == 0)
                _ = RunAsync();
            else
                _status.Text = L.T("This needs a model on this PC first.");
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _status.Text = _job switch
        {
            TextJob.Translate => Translation.Working(),
            TextJob.Summarize => Petal.App.Settings.AiSetup.Writing("Summarizing on this PC…"),
            _ => Petal.App.Settings.AiSetup.Writing("Rewriting on this PC…"),
        };
        _result.IsReadOnly = true;
        try
        {
            string answer = _job switch
            {
                TextJob.Translate => await Translation.RunAsync(_text, cts.Token, _into!.Value),
                TextJob.Summarize => await AskPetal.WriteAsync(AskPetal.Instructions.Summary(_style!.Value == "points", KeyboardLayout.LooksArabicLanguage(_text)), _text, 500, cts.Token),
                _ => await AskPetal.WriteAsync(AskPetal.Instructions.Rewrite(_style!.Value, KeyboardLayout.LooksArabicLanguage(_text)), _text, Math.Clamp(_text.Length, 200, 1200), cts.Token),
            };
            if (cts.IsCancellationRequested)
                return;
            _result.Text = answer;
            _status.Text = answer.Length == 0 ? L.T("Nothing came back. Try again.") : L.T("Change it if you like, then put it in place or copy it.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            Log.Error(ex, $"Text {_job}");
            _status.Text = L.T("It didn't work. The activity log has the details.");
        }
        finally
        {
            _result.IsReadOnly = false;
        }
    }
}
