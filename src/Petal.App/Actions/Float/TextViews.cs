using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Visuals;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using WpfCell = System.Windows.Documents.TableCell;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace Petal.App.Actions.Float;

/// <summary>
/// A text as it is: code with its lines numbered and not wrapped, prose wrapped; Ctrl and the wheel make its letters
/// larger or smaller. A document (an EPUB, a web page…) shows its words. Words are found and chosen.
/// </summary>
internal sealed class TextFloatView : FloatView
{
    /// <summary>The most of a text read: two megabytes.</summary>
    private const int Most = 2 * 1024 * 1024;

    private readonly bool _document;
    private readonly TextBox _text = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(14, 12, 18, 12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
    private readonly TextBox _numbers = new() { IsReadOnly = true, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 12, 8, 12), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, TextAlignment = TextAlignment.Right, IsTabStop = false, Focusable = false, FlowDirection = FlowDirection.LeftToRight };
    private bool _code;
    private bool _wrap;
    private int _foundAt = -1;

    public TextFloatView(string path, bool document) : base(path)
    {
        _document = document;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        _text.SetResourceReference(Control.ForegroundProperty, "Petal.Text");
        _numbers.SetResourceReference(Control.ForegroundProperty, "Petal.TextSecondary");
        _numbers.SetResourceReference(Control.BackgroundProperty, "Petal.Control");
        _numbers.SetResourceReference(Control.BorderBrushProperty, "Petal.Divider");
        _text.VerticalContentAlignment = VerticalAlignment.Top;
        _numbers.VerticalContentAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_text, 1);
        Children.Add(_numbers);
        Children.Add(_text);
        _text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => _numbers.ScrollToVerticalOffset(e.VerticalOffset)));
        _text.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;
            e.Handled = true;
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        };
        _ = ReadAsync();
    }

    /// <summary>Why the words are shown and not the pages (a document Office couldn't lay out); said under its facts.</summary>
    public string? Explained { get; init; }

    public override FloatKind Kind => FloatKind.Text;

    public override string? ZoomText => FloatMath.Percent(_text.FontSize / Base);

    public override bool CanFind => true;

    private double Base => _code ? 13 : 15;

    private async Task ReadAsync()
    {
        string text;
        try
        {
            if (_document)
            {
                var converters = App.Current.Controller.Jobs.Converters;
                text = await Petal.Recipes.DocumentText.ReadAsync(Path, converters, SettingsStore.Current.Conversion, null, CancellationToken.None) ?? "";
            }
            else
                text = await ReadAsync(Path, Most);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(Path)} not read ({ex.Message})");
            Children.Clear();
            Children.Add(QuickLookWindow.Fallback(Path));
            Tell(QuickLookWindow.Facts(Path, null));
            return;
        }
        var ext = System.IO.Path.GetExtension(Path).ToLowerInvariant();
        _code = !_document && ext is not (".txt" or ".md" or ".markdown" or ".srt" or ".vtt" or ".log" or ".csv" or ".tsv");
        _wrap = !_code;
        _text.FontFamily = _code ? UiFonts.Mono : UiFonts.Text;
        _text.FontSize = Base;
        _numbers.FontFamily = _text.FontFamily;
        _numbers.FontSize = _text.FontSize;
        _text.Text = text;
        // Code reads left to right whatever the interface; prose in the direction of its own letters.
        FlowDirection = _code ? FlowDirection.LeftToRight : Petal.Core.Text.KeyboardLayout.LooksArabic(text.Length > 4000 ? text[..4000] : text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        int lines = text.Count(c => c == '\n') + 1;
        _numbers.Text = string.Join("\n", Enumerable.Range(1, lines));
        Wrap(_wrap);
        var facts = _document ? L.P(Petal.Core.Text.TextTools.Count(text).Words, "{0} word", "{0} words") : L.P(lines, "{0} line", "{0} lines");
        Tell(QuickLookWindow.Facts(Path, Explained is null ? facts : facts + " · " + Explained));
    }

    /// <summary>A text file's beginning (up to <paramref name="most"/> bytes), in its own encoding, never locking the file.</summary>
    internal static async Task<string> ReadAsync(string path, int most)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(stream.Length, most)];
        int read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        return stream.Length > read ? text + Environment.NewLine + "…" : text;
    }

    /// <summary>Lines wrapped at the window's edge, or each on one line; numbered only when they aren't wrapped.</summary>
    private void Wrap(bool wrap)
    {
        _wrap = wrap;
        _text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _numbers.Visibility = wrap || !_code ? Visibility.Collapsed : Visibility.Visible;
    }

    public override void ZoomBy(double factor)
    {
        _text.FontSize = Math.Clamp(_text.FontSize * factor, 8, 48);
        _numbers.FontSize = _text.FontSize;
        Tell();
    }

    public override void FitAll()
    {
        _text.FontSize = _numbers.FontSize = Base;
        Tell();
    }

    public override Task<(int At, int Of)> FindAsync(string text, bool forward, CancellationToken cancellationToken)
    {
        text = text.Trim();
        var all = _text.Text;
        if (text.Length == 0)
            return Task.FromResult((0, 0));
        var places = new List<int>();
        for (int i = all.IndexOf(text, StringComparison.CurrentCultureIgnoreCase); i >= 0; i = all.IndexOf(text, i + 1, StringComparison.CurrentCultureIgnoreCase))
            places.Add(i);
        if (places.Count == 0)
            return Task.FromResult((0, 0));
        int caret = _text.SelectionStart + (forward ? _text.SelectionLength : 0);
        int at = forward ? places.FindIndex(p => p >= caret && p != _foundAt) : places.FindLastIndex(p => p < caret && p != _foundAt);
        if (at < 0)
            at = forward ? 0 : places.Count - 1;
        _foundAt = places[at];
        _text.Focus();
        _text.Select(_foundAt, text.Length);
        _text.ScrollToLine(_text.GetLineIndexFromCharacterIndex(_foundAt));
        return Task.FromResult((at + 1, places.Count));
    }

    public override IEnumerable<Control> MenuItems() =>
    [
        Item("Wrap Long Lines", () => Wrap(!_wrap), _wrap),
        Item("Larger Text", () => ZoomBy(1.1)),
        Item("Smaller Text", () => ZoomBy(1 / 1.1)),
    ];

    public override bool Copy()
    {
        if (_text.SelectionLength == 0)
            return false;
        _text.Copy();
        return true;
    }
}

/// <summary>
/// A Markdown file as it reads: headings, paragraphs in bold and italics, lists and task lists, quotes, code, tables,
/// links that open in the browser. Its source is a menu line away.
/// </summary>
internal sealed class MarkdownView : FloatView
{
    private readonly FlowDocumentScrollViewer _reader = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false };
    private TextFloatView? _source;
    private double _size = 15;

    public MarkdownView(string path) : base(path)
    {
        Children.Add(_reader);
        _reader.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;
            e.Handled = true;
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        };
        _ = ReadAsync();
    }

    public override FloatKind Kind => FloatKind.Text;

    public override string? ZoomText => FloatMath.Percent(_size / 15);

    private async Task ReadAsync()
    {
        try
        {
            var text = await TextFloatView.ReadAsync(Path, 2 * 1024 * 1024);
            var document = MarkdownDocument.Build(text);
            document.FontSize = _size;
            _reader.Document = document;
            int words = Petal.Core.Text.TextTools.Count(text).Words;
            Tell(QuickLookWindow.Facts(Path, L.P(words, "{0} word", "{0} words")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Children.Clear();
            Children.Add(QuickLookWindow.Fallback(Path));
            Tell(QuickLookWindow.Facts(Path, null));
        }
    }

    public override void ZoomBy(double factor)
    {
        _size = Math.Clamp(_size * factor, 9, 40);
        if (_reader.Document is { } document)
            document.FontSize = _size;
        Tell();
    }

    public override void FitAll() => ZoomBy(15 / _size);

    public override IEnumerable<Control> MenuItems() =>
    [
        Item("Show the Source", () =>
        {
            if (_source is null)
            {
                _source = new TextFloatView(Path, document: false);
                Children.Add(_source);
                _reader.Visibility = Visibility.Collapsed;
            }
            else
            {
                Children.Remove(_source);
                _source.Dispose();
                _source = null;
                _reader.Visibility = Visibility.Visible;
            }
        }, _source is not null),
    ];

    public override bool Copy()
    {
        if (_source is not null)
            return _source.Copy();
        if (_reader.Selection is { IsEmpty: false } selection)
        {
            Clipboard.SetText(selection.Text);
            return true;
        }
        return false;
    }
}

/// <summary>Markdown made into a WPF document, with the app's colours and the text's own direction.</summary>
internal static class MarkdownDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().UseTaskLists().UseAutoLinks().Build();

    public static FlowDocument Build(string markdown)
    {
        var document = new FlowDocument { FontFamily = UiFonts.Text, PagePadding = new Thickness(24, 16, 24, 24), LineHeight = double.NaN };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Petal.Text");
        document.SetResourceReference(FlowDocument.BackgroundProperty, "Petal.Popup");
        document.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(markdown.Length > 4000 ? markdown[..4000] : markdown) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var block in Markdown.Parse(markdown, Pipeline))
            if (Convert(block) is { } made)
                document.Blocks.Add(made);
        return document;
    }

    /// <summary>The kinds of block Markdown has, each as its WPF block; null for what has none (a link's definition).</summary>
    internal static WpfBlock? Convert(MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Inlines(new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Level switch { 1 => 26, 2 => 21, 3 => 18, _ => 16 }, Margin = new Thickness(0, heading.Level <= 2 ? 18 : 12, 0, 6) }, heading.Inline);
                return title;
            case ParagraphBlock paragraph:
                return Inlines(new Paragraph { Margin = new Thickness(0, 0, 0, 10) }, paragraph.Inline);
            case ListBlock list:
                var made = new List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0) };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var entry = new ListItem();
                    foreach (var inner in item)
                        if (Convert(inner) is { } part)
                        {
                            part.Margin = new Thickness(0, 0, 0, 3);
                            entry.Blocks.Add(part);
                        }
                    made.ListItems.Add(entry);
                }
                return made;
            case QuoteBlock quote:
                var section = new Section { Padding = new Thickness(12, 2, 0, 2), BorderThickness = new Thickness(3, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                section.SetResourceReference(WpfBlock.BorderBrushProperty, "Petal.Accent");
                section.SetResourceReference(TextElement.ForegroundProperty, "Petal.TextSecondary");
                foreach (var inner in quote)
                    if (Convert(inner) is { } part)
                        section.Blocks.Add(part);
                return section;
            case FencedCodeBlock or CodeBlock:
                var code = new Paragraph(new Run(((LeafBlock)block).Lines.ToString())) { FontFamily = UiFonts.Mono, FontSize = 13, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), FlowDirection = FlowDirection.LeftToRight };
                code.SetResourceReference(WpfBlock.BackgroundProperty, "Petal.Control");
                return code;
            case ThematicBreakBlock:
                var line = new Paragraph { Margin = new Thickness(0, 6, 0, 12), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 };
                line.SetResourceReference(WpfBlock.BorderBrushProperty, "Petal.Divider");
                return line;
            case MdTable table:
                return Table(table);
            case LeafBlock leaf when leaf.Inline is not null:
                return Inlines(new Paragraph { Margin = new Thickness(0, 0, 0, 10) }, leaf.Inline);
            case LeafBlock leaf:
                return new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 10) };
            case ContainerBlock container:
                var all = new Section();
                foreach (var inner in container)
                    if (Convert(inner) is { } part)
                        all.Blocks.Add(part);
                return all;
            default:
                return null;
        }
    }

    private static WpfTable Table(MdTable table)
    {
        var made = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
        made.SetResourceReference(WpfBlock.BorderBrushProperty, "Petal.Divider");
        int columns = table.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (int i = 0; i < columns; i++)
            made.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdRow>())
        {
            var line = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<MdCell>())
            {
                var content = new WpfCell { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                content.SetResourceReference(WpfCell.BorderBrushProperty, "Petal.Divider");
                foreach (var inner in cell)
                    if (Convert(inner) is { } part)
                    {
                        part.Margin = new Thickness(0);
                        if (row.IsHeader)
                            part.FontWeight = FontWeights.SemiBold;
                        content.Blocks.Add(part);
                    }
                line.Cells.Add(content);
            }
            if (row.IsHeader)
                line.SetResourceReference(TextElement.BackgroundProperty, "Petal.Control");
            group.Rows.Add(line);
        }
        made.RowGroups.Add(group);
        return made;
    }

    private static Paragraph Inlines(Paragraph paragraph, ContainerInline? inlines)
    {
        if (inlines is not null)
            foreach (var inline in inlines)
                if (Convert(inline) is { } made)
                    paragraph.Inlines.Add(made);
        return paragraph;
    }

    private static WpfInline? Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = UiFonts.Mono };
                run.SetResourceReference(TextElement.BackgroundProperty, "Petal.Control");
                return run;
            case LineBreakInline brk:
                return brk.IsHard ? new LineBreak() : new Run(" ");
            case TaskList task:
                return new Run((task.Checked ? "☑" : "☐") + (char)0xFE0E + " ");
            case LinkInline { IsImage: true } image:
                return new Run("[" + string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString())) + "]");
            case LinkInline link:
                var hyperlink = new Hyperlink();
                foreach (var child in link)
                    if (Convert(child) is { } made)
                        hyperlink.Inlines.Add(made);
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" or "mailto")
                {
                    hyperlink.NavigateUri = address;
                    hyperlink.ToolTip = address.ToString();
                    hyperlink.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                        }
                    };
                }
                return hyperlink;
            case AutolinkInline auto:
                var auto2 = new LinkInline(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, "");
                auto2.AppendChild(new LiteralInline(auto.Url));
                return Convert(auto2);
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis)
                    if (Convert(child) is { } made)
                        span.Inlines.Add(made);
                return span;
            case HtmlInline html:
                return new Run(html.Tag);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case ContainerInline container:
                var all = new Span();
                foreach (var child in container)
                    if (Convert(child) is { } made)
                        all.Inlines.Add(made);
                return all;
            default:
                return null;
        }
    }
}
