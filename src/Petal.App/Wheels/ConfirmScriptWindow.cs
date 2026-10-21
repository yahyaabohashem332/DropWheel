using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;

namespace Petal.App.Wheels;

/// <summary>
/// Shown before a script runs for the first time when it wasn't written in Petal on this PC (it came in
/// with imported settings): the script itself, what it is given, and Run or Cancel.
/// </summary>
internal sealed class ConfirmScriptWindow : ToolWindow
{
    private readonly TaskCompletionSource<bool> _answer;

    private ConfirmScriptWindow(WheelEntry entry, TaskCompletionSource<bool> answer) : base("Run This Script?", 520)
    {
        _answer = answer;
        Body.Children.Add(Text(entry.Name ?? L.T("Script"), "Petal.Section"));
        Body.Children.Add(Text("This script came from somewhere else, such as settings you imported. Run it only if you trust where it came from.", "Petal.Body").WithMarginT(8));
        Body.Children.Add(Row("Runs with", new TextBlock { Text = entry.Shell == ScriptShell.Cmd ? "Command Prompt" : "PowerShell", VerticalAlignment = VerticalAlignment.Center }, 90, 10));
        Body.Children.Add(Row("Gets", new TextBlock { Text = L.T(InputName(entry.Input)), VerticalAlignment = VerticalAlignment.Center }, 90, 6));
        Body.Children.Add(new TextBox
        {
            Text = entry.Script,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 220,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
        });
        var always = new CheckBox { Content = L.T("Don't ask again for this script"), Margin = new Thickness(0, 12, 0, 0) };
        Body.Children.Add(always);
        Body.Children.Add(Text("Petal asks again if the script changes.", "Petal.Caption").WithMarginT(4));
        PrimaryButton.Content = L.T("Run");
        PrimaryButton.Click += (_, _) =>
        {
            if (always.IsChecked == true)
            {
                ScriptRunner.Approve(entry);
                SettingsStore.Save();
            }
            _answer.TrySetResult(true);
            Close();
        };
        Closed += (_, _) => _answer.TrySetResult(false);
    }

    public static string InputName(ScriptInput input) => input switch
    {
        ScriptInput.Clipboard => "The text on the clipboard",
        ScriptInput.SelectedText => "The selected text",
        ScriptInput.SelectedFiles => "The files selected in File Explorer",
        _ => "Nothing",
    };

    public static void Ask(WheelEntry entry, ScreenPoint at, TaskCompletionSource<bool> answer) =>
        new ConfirmScriptWindow(entry, answer).ShowNear(at);
}
