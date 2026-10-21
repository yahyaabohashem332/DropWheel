using System.IO;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.Recipes;

namespace Petal.App.Actions;

/// <summary>
/// Runs the user's own petals. The first time a petal runs (and again whenever its file changes),
/// Petal shows the exact command and asks; nothing runs unseen.
/// </summary>
internal static class CustomPetalRunner
{
    public static void Run(CustomPetal petal, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at)
    {
        IReadOnlyList<CustomPetal.Run> runs;
        try
        {
            runs = petal.Plan(files);
        }
        catch (InvalidOperationException ex)
        {
            Hud.HudCard.ShowAt(at, petal.DisplayName(L.IsArabic), "").Fail(petal.DisplayName(L.IsArabic), L.T(ex.Message));
            return;
        }
        if (SettingsStore.Current.ApprovedPetals.Contains(petal.Fingerprint))
        {
            Start(petal, runs, jobs, at);
            return;
        }
        var ask = new ConfirmPetalWindow(petal, runs, () => Start(petal, runs, jobs, at));
        ask.ShowNear(at);
    }

    private static void Start(CustomPetal petal, IReadOnlyList<CustomPetal.Run> runs, JobRunner jobs, ScreenPoint at)
    {
        var name = petal.DisplayName(L.IsArabic);
        var work = runs.Select(run => new WorkItem(run.Inputs[0], run.Inputs.Count == 1 ? Path.GetFileName(run.Inputs[0]) : L.P(run.Inputs.Count, "{0} file", "{0} files"),
            (_, ct) => CustomPetal.RunAsync(run, ct))).ToList();
        _ = jobs.RunAsync(L.F("Running {0}", name), L.F("{0}: done", name), work, at);
    }

    /// <summary>For design snapshots: the question for the example petal, from a scratch folder.</summary>
    internal static Window ConfirmForSnapshot(string file)
    {
        var saved = CustomPetal.Folder;
        CustomPetal.Folder = Path.Combine(Path.GetTempPath(), "petal-snapshot-petals");
        try
        {
            Directory.CreateDirectory(CustomPetal.Folder);
            foreach (var old in Directory.GetFiles(CustomPetal.Folder))
                File.Delete(old);
            var petal = CustomPetal.Load(CustomPetal.WriteExample());
            return new ConfirmPetalWindow(petal, petal.Plan([file]), () => { });
        }
        finally
        {
            CustomPetal.Folder = saved;
        }
    }

    /// <summary>The first-run question: the program, the command, and where the petal came from.</summary>
    private sealed class ConfirmPetalWindow : ToolWindow
    {
        public ConfirmPetalWindow(CustomPetal petal, IReadOnlyList<CustomPetal.Run> runs, Action run) : base("Run Your Petal?", 520)
        {
            Body.Children.Add(Text(petal.DisplayName(L.IsArabic), "Petal.Section"));
            if (petal.DisplayDescription(L.IsArabic) is { Length: > 0 } description)
            {
                // A petal's own words run in their own direction, whatever the interface language.
                var words = Text(description, "Petal.Caption").WithMarginT(2);
                words.FlowDirection = description.Any(c => c is >= '\u0600' and <= '\u06FF') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                words.HorizontalAlignment = HorizontalAlignment.Left;
                Body.Children.Add(words);
            }
            Body.Children.Add(Text("This petal runs a program on your files. Run it only if you trust where it came from.", "Petal.Body").WithMarginT(12));
            Body.Children.Add(Row("Program", Path(runs[0].Program), 90, 10));
            Body.Children.Add(Row("Command", Path(runs[0].CommandLine + (runs.Count > 1 ? " " + L.F("(and {0} more)", runs.Count - 1) : "")), 90, 6));
            Body.Children.Add(Row("Defined in", Path(petal.File), 90, 6));
            var always = new CheckBox { Content = L.T("Don't ask again for this petal"), Margin = new Thickness(90, 12, 0, 0) };
            Body.Children.Add(always);
            Body.Children.Add(Text("Petal asks again if the petal's file changes.", "Petal.Caption").WithMargin(90, 4, 0, 0));
            PrimaryButton.Content = L.T("Run");
            PrimaryButton.Click += (_, _) =>
            {
                if (always.IsChecked == true)
                {
                    SettingsStore.Current.ApprovedPetals.Add(petal.Fingerprint);
                    SettingsStore.Save();
                }
                Close();
                run();
            };
        }

        private static TextBox Path(string text) => new()
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
        };
    }
}
