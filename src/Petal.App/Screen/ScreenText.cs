using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.Core.Text;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Text from the screen: drag over anything (a video, a picture, an app that won't let text be copied)
/// and its words come out, ready to copy, fix, translate or search. QR codes and barcodes in the area are
/// read too. All on this PC, with the text reading built into Windows.
/// </summary>
internal sealed class ScreenTextWindow : ToolWindow
{
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _translated = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status;
    private readonly StackPanel _codes = new();
    private readonly ModelPanel _model = new(Petal.Recipes.Translation.Translator.Models) { Wanted = false };
    private readonly Button _translate, _search;
    // The picture read, kept for reading it again as a table.
    private byte[]? _pixels;
    private int _width, _height;

    /// <summary>Drag over an area; its text opens in the window.</summary>
    public static void Start(ScreenPoint at)
    {
        Wheels.Screenshots.ChooseArea("Drag over the text to read · Esc cancels", false, (picture, area) =>
        {
            var window = new ScreenTextWindow(picture);
            window.ShowNear(new ScreenPoint(area.Left + area.Width / 2, area.Bottom));
            window.Activate();
        });
    }

    internal static ScreenTextWindow ForSnapshot(BitmapSource picture) => new(picture);

    private ScreenTextWindow(BitmapSource picture) : base("Text from the Screen", 480)
    {
        var preview = new Image { Source = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
        Body.Children.Add(PreviewPanel(preview, Math.Clamp(picture.PixelHeight / 2.0 + 16, 60, 130)));
        _status = Text("Reading the text…", "Petal.Caption");
        _status.Margin = new Thickness(0, 10, 0, 6);
        Body.Children.Add(_status);
        Body.Children.Add(_text);
        Body.Children.Add(_translated);
        Body.Children.Add(_model);
        Body.Children.Add(_codes);
        _text.TextChanged += (_, _) =>
        {
            _text.FlowDirection = KeyboardLayout.LooksArabic(_text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            var count = TextTools.Count(_text.Text);
            FooterNote = count.Words == 0 ? "" : L.P(count.Words, "{0} word", "{0} words");
            _translate!.IsEnabled = _search!.IsEnabled = _text.Text.Trim().Length > 0;
        };

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _translate = new Button { Content = L.T("Translate"), Margin = new Thickness(0, 0, 8, 6), IsEnabled = false };
        _translate.Click += async (_, _) => await TranslateAsync();
        var fix = new Button { Content = L.T("Fix Keyboard Layout"), Margin = new Thickness(0, 0, 8, 6) };
        fix.ToolTip = L.T("For text typed with the wrong keyboard language: ؤشف becomes cat.");
        fix.Click += (_, _) => _text.Text = KeyboardLayout.Fix(_text.Text);
        var tidy = new Button { Content = L.T("Join Lines"), Margin = new Thickness(0, 0, 8, 6) };
        tidy.ToolTip = L.T("Lines broken by the screen joined into paragraphs.");
        tidy.Click += (_, _) => _text.Text = JoinLines(_text.Text);
        _search = new Button { Content = L.T("Search the Web"), Margin = new Thickness(0, 0, 8, 6), IsEnabled = false };
        _search.Click += (_, _) => Search();
        var table = new Button { Content = L.T("Copy as Table"), Margin = new Thickness(0, 0, 8, 6) };
        table.ToolTip = L.T("The text in rows and columns: it pastes into Excel or Google Sheets cell by cell.");
        table.Click += async (_, _) => await TableAsync(save: false);
        var excel = new Button { Content = L.T("Save as Excel"), Margin = new Thickness(0, 0, 8, 6) };
        excel.ToolTip = L.T("The table in the area as an Excel sheet, with numbers as numbers.");
        excel.Click += async (_, _) => await TableAsync(save: true);
        actions.Children.Add(_translate);
        actions.Children.Add(fix);
        actions.Children.Add(tidy);
        actions.Children.Add(_search);
        actions.Children.Add(table);
        actions.Children.Add(excel);
        Body.Children.Add(actions);
        _model.Ready += async () => await TranslateAsync();

        PrimaryButton.Content = L.T("Copy Text");
        PrimaryButton.Click += (_, _) => Copy(_translated.Visibility == Visibility.Visible && _translated.Text.Length > 0 && _translated.IsFocused ? _translated.Text : _text.Text);
        Loaded += async (_, _) => await ReadAsync(picture);
    }

    private async Task ReadAsync(BitmapSource picture)
    {
        int w = picture.PixelWidth, h = picture.PixelHeight;
        var pixels = new byte[w * h * 4];
        var bgra = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        bgra.CopyPixels(pixels, w * 4, 0);
        (_pixels, _width, _height) = (pixels, w, h);
        var codes = await Task.Run(() =>
        {
            try { return BarcodeScanner.Scan(new BgraBitmap(w, h, pixels)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return (IReadOnlyList<BarcodeResult>)[]; }
        });
        foreach (var code in codes)
            AddCode(code);
        if (!DocumentEngine.IsOcrAvailable)
        {
            _status.Text = L.T("Windows has no language for reading text. Add one under Settings › Time & language › Language.");
            return;
        }
        try
        {
            var text = await DocumentEngine.ReadTextAsync(pixels, w, h);
            _text.Text = text;
            _status.Text = text.Length == 0
                ? L.T(codes.Count > 0 ? "No text, but a code was found." : "No text was found in the area.")
                : L.T("The text found. You can change it before copying.");
            _text.Focus();
            _text.SelectAll();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = L.T("The text couldn't be read.");
        }
    }

    private void AddCode(BarcodeResult code)
    {
        bool link = Uri.TryCreate(code.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = L.T("Copy"), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => Copy(code.Text);
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        if (link)
        {
            var open = new Button { Content = L.T("Open Link"), Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(code.Text) { UseShellExecute = true });
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
        }
        var label = Text((code.Format == "QR_CODE" ? L.T("QR code") : L.T("Barcode")) + ": " + code.Text, "Petal.Body");
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.ToolTip = code.Text;
        row.Children.Add(label);
        _codes.Children.Add(row);
    }

    private async Task TranslateAsync()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0)
            return;
        _model.PointAt(Petal.Recipes.Translation.Translator.ModelsFor(Words.Translation.From(text), Words.Translation.Into(text)));
        _model.Wanted = true;
        if (Words.Translation.Missing(text).Count > 0)
            return;
        _translate.IsEnabled = false;
        _status.Text = Words.Translation.Working();
        try
        {
            var result = await Words.Translation.RunAsync(text);
            _translated.Text = result;
            _translated.FlowDirection = KeyboardLayout.LooksArabic(result) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _translated.Visibility = Visibility.Visible;
            _status.Text = L.T("Translated. Copy Text copies the box you clicked last.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            _status.Text = L.T("The translation didn't work.");
        }
        finally
        {
            _translate.IsEnabled = true;
        }
    }

    /// <summary>The area read again as a table: copied as cells, or saved as an Excel sheet and opened.</summary>
    private async Task TableAsync(bool save)
    {
        if (_pixels is null || !DocumentEngine.IsOcrAvailable)
            return;
        _status.Text = L.T("Reading the table…");
        try
        {
            var table = await PictureTables.ReadAsync(_pixels, _width, _height);
            if (table.IsEmpty)
            {
                _status.Text = L.T("No table was found in the area.");
                return;
            }
            var size = L.P(table.Rows.Count, "{0} row", "{0} rows") + " · " + L.P(table.Columns, "{0} column", "{0} columns");
            if (!save)
            {
                Clipboard.SetText(PictureTables.ToTabs(table));
                _status.Text = L.T("The table is copied. Paste it into a spreadsheet.");
                HudCard.ShowAt(HudCard.CornerPoint(), "", "").Succeed(L.T("Table copied"), size, null);
                return;
            }
            var folder = Settings.SavedPlaces.Tables;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
            var output = Petal.Core.Jobs.OutputNaming.Unique(folder, L.T("Table") + " " + stamp, "xlsx");
            await Task.Run(() => PictureTables.SaveXlsx(table, output, L.T("Table")));
            _status.Text = L.T("The table is saved in Documents.");
            HudCard.ShowAt(HudCard.CornerPoint(), "", "").Succeed(L.T("Table saved"), size, () => Open(output));
            Open(output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.IO.IOException or UnauthorizedAccessException)
        {
            _status.Text = L.T("The table couldn't be read.");
            Log.Info($"Table from the screen: {ex.Message}");
        }
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void Search()
    {
        var query = string.Join(' ', _text.Text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (query.Length > 300)
            query = query[..300];
        Process.Start(new ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true });
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
            HudCard.ShowAt(HudCard.CornerPoint(), "", "").Succeed(L.T("Text copied"), "", null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    /// <summary>Lines that end mid-sentence joined with the next; paragraphs kept.</summary>
    internal static string JoinLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                result.Append(Environment.NewLine).Append(Environment.NewLine);
                continue;
            }
            if (result.Length > 0 && !result.ToString().EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                char last = result[^1];
                if (last is '.' or '!' or '?' or ':' or '؟' or '۔')
                    result.Append(Environment.NewLine);
                else if (last == '-')
                    result.Length--;
                else
                    result.Append(' ');
            }
            result.Append(line);
        }
        return TextTools.TidySpaces(result.ToString());
    }
}
