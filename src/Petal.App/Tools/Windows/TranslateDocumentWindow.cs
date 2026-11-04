using System.IO;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Translate;
using Petal.Recipes.Cloud;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Windows;

/// <summary>
/// A Word, PowerPoint, Excel or PDF file in another language, as a new file beside the original with its formatting kept.
/// It runs on this PC unless the user has chosen a cloud model of their own, and it says which.
/// </summary>
public sealed class TranslateDocumentWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly LanguageChoice _from = new("auto", [("auto", "Detect automatically")]);
    private readonly LanguageChoice _into = new(LanguageChoice.Interface);
    private readonly TextBlock _where = Text("", "Petal.Caption");
    private readonly ModelPanel _model;
    private readonly string _guessed;

    public TranslateDocumentWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 440)
    {
        _files = files;
        _jobs = jobs;
        _at = at;
        _guessed = Guess(files[0]);
        if (_guessed == _into.Value)
            _into.Value = _guessed == "en" ? "es" : "en";
        _model = new ModelPanel(Translator.ModelsFor(_guessed, _into.Value));
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(Text("A new file is saved beside the original, with its formatting kept and the language in its name.", "Petal.Caption").WithMarginT(6));
        Body.Children.Add(Row("From", _from, 80, 12));
        Body.Children.Add(Row("Into", _into, 80, 8));
        _where.Margin = new Thickness(0, 12, 0, 0);
        _where.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_where);
        Body.Children.Add(_model);
        _from.Changed += _ => Choose();
        _into.Changed += _ => Choose();
        _model.Ready += Sync;
        _model.Switched += Sync;
        PrimaryButton.Content = L.T("Translate");
        PrimaryButton.Click += (_, _) => Apply();
        Choose();
    }

    /// <summary>The language a file is in, from its first words; English when it has none to read (a scan: the job reads it).</summary>
    private static string Guess(string file) =>
        Petal.Core.Text.LanguageGuess.Of(DocumentTranslator.FirstText(file)) ?? "en";

    private string From => _from.Value == "auto" ? _guessed : _from.Value;

    private void Choose()
    {
        _model.Models = Translator.ModelsFor(From, _into.Value);
        Sync();
    }

    /// <summary>"report.docx" becomes "report.es.docx"; an ODT or RTF comes out as a DOCX.</summary>
    internal static string OutputFor(string file, string into) =>
        OutputNaming.Unique(OutputNaming.DirectoryFor(file), Path.GetFileNameWithoutExtension(file) + "." + into, DocumentTranslator.OutputExtension(file));

    private void Sync()
    {
        bool same = From == _into.Value;
        _where.Text = same ? L.T("The file is already in this language.")
            : CloudAi.CanTranslate ? L.F("The text goes to {0}, on your own API key.", CloudAi.Name(CloudAi.Translating!.Provider))
            : L.T("It runs on this PC: the file doesn't leave it.");
        PrimaryButton.IsEnabled = !same && _model.IsReady;
        FooterNote = _files.Count == 1 ? Path.GetFileName(OutputFor(_files[0], _into.Value)) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var (from, into) = (_from.Value, _into.Value);
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputFor(file, into);
            await DocumentTranslation.TranslateAsync(file, output, from, into, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Translating", "Translated", work, _at);
    }
}
