using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.Engines.Files.Metadata;

namespace Petal.App.Actions;

/// <summary>
/// Checks a downloaded file: its SHA-256, and what the publisher says it should be, found by itself where it can be (copied
/// from the web page, or in a checksum file beside it) or pasted here. Says plainly whether they match.
/// </summary>
public sealed class ChecksumWindow : ToolWindow
{
    private readonly string _file;
    private readonly TextBox _yours = new() { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _published = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _kind = Text("", "Petal.Label");
    private readonly TextBlock _verdict = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _note = Text("", "Petal.Caption");
    private readonly Button _copy = new() { Content = L.T("Copy"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CancellationTokenSource _closing = new();
    private string _algorithm = FileChecksum.Sha256;
    private string _value = "";
    private int _run;

    public ChecksumWindow(string file, string? clipboard = null) : base(L.T("Check File"), 520)
    {
        _file = file;
        var name = Text(Path.GetFileName(file), "Petal.Section");
        name.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(name);
        _kind.Margin = new Thickness(0, 12, 0, 4);
        Body.Children.Add(_kind);
        Body.Children.Add(_yours);
        Body.Children.Add(_copy);
        var asked = Text("What the publisher says it should be", "Petal.Label");
        asked.Margin = new Thickness(0, 14, 0, 4);
        Body.Children.Add(asked);
        Body.Children.Add(_published);
        Body.Children.Add(_verdict);
        _note.Margin = new Thickness(0, 6, 0, 0);
        _note.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_note);
        _copy.Click += (_, _) => Clip(_value);
        _published.TextChanged += async (_, _) => await UpdateAsync();
        PrimaryButton.Content = L.T("Close");
        PrimaryButton.Click += (_, _) => Close();
        Closed += (_, _) => _closing.Cancel();
        var found = FileChecksum.Find(clipboard ?? ReadClipboard(), Path.GetFileName(file)) is { } copied
            ? copied
            : FileChecksum.FindBeside(file);
        if (found is not null)
        {
            _published.Text = found.Hex;
            _note.Text = found.Source is { } source ? L.F("Found in {0}, beside the file.", source) : L.T("Taken from what you copied.");
        }
        Loaded += async (_, _) => await UpdateAsync();
    }

    private static string ReadClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return "";
        }
    }

    private static void Clip(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Error(ex, "Copying a checksum");
        }
    }

    /// <summary>What was pasted decides the kind of checksum: its length says which one the publisher used.</summary>
    private async Task UpdateAsync()
    {
        int run = ++_run;
        var published = FileChecksum.Find(_published.Text, Path.GetFileName(_file));
        var algorithm = published?.Algorithm ?? FileChecksum.Sha256;
        if (algorithm != _algorithm || _value.Length == 0)
        {
            _algorithm = algorithm;
            _value = "";
            _copy.IsEnabled = false;
            _kind.Text = _algorithm;
            _yours.Text = L.T("Working it out…");
            Show(null, null);
            try
            {
                var progress = new Progress<double>(f => { if (run == _run) _yours.Text = L.T("Working it out…") + " " + (int)(f * 100) + "%"; });
                var value = await FileChecksum.ComputeAsync(_file, algorithm, progress, _closing.Token);
                if (run != _run)
                    return;
                _value = value;
                _yours.Text = value;
                _copy.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Working out a checksum");
                _yours.Text = L.T("This file couldn't be read.");
                return;
            }
        }
        Show(published, _published.Text);
    }

    private void Show(PublishedChecksum? published, string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            _verdict.Text = "";
            _verdict.ClearValue(TextBlock.ForegroundProperty);
            if (_note.Text.Length == 0 || published is null)
                _note.Text = _value.Length == 0 ? "" : L.T("Paste the checksum from the publisher's page to compare.");
            return;
        }
        if (published is null)
        {
            _verdict.Text = L.T("That isn't a checksum, or it is for another file.");
            _verdict.Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0x7C, 0x0E));
            return;
        }
        bool same = string.Equals(published.Hex, _value, StringComparison.OrdinalIgnoreCase);
        _verdict.Text = same ? L.T("✓ They match: this is the publisher's file.") : L.T("✗ They don't match: this isn't the same file. Don't open it. Download it again from the publisher.");
        _verdict.Foreground = new SolidColorBrush(same ? Color.FromRgb(0x1E, 0x8E, 0x4E) : Color.FromRgb(0xD0, 0x2B, 0x2B));
        if (same && FileChecksum.IsWeak(published.Algorithm))
            _note.Text = L.F("{0} shows the download isn't damaged, but someone could make a different file with the same value. Trust SHA-256 where it is offered.", published.Algorithm);
    }
}
