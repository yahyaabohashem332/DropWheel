using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.Core.Text;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;

namespace Petal.App.Screen;

/// <summary>What a circle took in, as far as what can be done with it goes.</summary>
internal enum CircleKind
{
    /// <summary>Words: a sentence, a paragraph, a number, a label.</summary>
    Text,

    /// <summary>A picture, an icon or a chart, with no words to speak of.</summary>
    Picture,

    /// <summary>A picture with some words in it (a poster, a slide, a post).</summary>
    Mixed,
}

/// <summary>Everything read from a circled area: its picture, its words in reading order, and what the words hold.</summary>
internal sealed class CircleContent
{
    public required BitmapSource Picture { get; init; }

    /// <summary>The picture's BGRA pixels.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Where the area is on the still of the screen, in its pixels.</summary>
    public required Int32Rect Area { get; init; }

    /// <summary>The words, in the still's pixels.</summary>
    public required IReadOnlyList<PictureWord> Words { get; init; }

    public required IReadOnlyList<ScreenLine> Lines { get; init; }

    public required IReadOnlyList<ScreenBlock> Blocks { get; init; }

    /// <summary>The words as they read, a line of the screen per line.</summary>
    public required string Text { get; init; }

    public required IReadOnlyList<Found> Finds { get; init; }

    public required IReadOnlyList<BarcodeResult> Codes { get; init; }

    /// <summary>The words as a table, when they stand in rows and columns.</summary>
    public PictureTable? Table { get; init; }

    /// <summary>How much of the area the words cover, from 0 to 1.</summary>
    public double TextCover { get; init; }

    public CircleKind Kind =>
        Words.Count == 0 ? CircleKind.Picture
        : TextCover < 0.08 && Area.Width * Area.Height > 160 * 160 ? CircleKind.Mixed
        : CircleKind.Text;

    public bool HasText => Text.Trim().Length > 0;

    public int WordCount => TextTools.Count(Text).Words;

    /// <summary>The text joined where the screen broke its lines, for reading, translating and asking about.</summary>
    public string Prose => Blocks.Count > 0 ? string.Join("\n\n", Blocks.Select(b => b.Text)) : ScreenTextWindow.JoinLines(Text);
}

/// <summary>Reads a circled area: its words (read again, larger, for the small print), its codes and what its text holds.</summary>
internal static class CircleReader
{
    /// <param name="stroke">The loop or stroke drawn, to keep only the words it took in; null for a click or a box.</param>
    public static async Task<CircleContent> ReadAsync(byte[] still, int stillWidth, CircleArea selection, IReadOnlyList<Point>? stroke, CancellationToken cancellationToken)
    {
        var area = selection.Area;
        var pixels = Crop(still, stillWidth, area);
        var picture = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32, null, pixels, area.Width * 4);
        picture.Freeze();
        var codes = Task.Run(() =>
        {
            try { return BarcodeScanner.Scan(new BgraBitmap(area.Width, area.Height, pixels)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return (IReadOnlyList<BarcodeResult>)[]; }
        }, cancellationToken);
        IReadOnlyList<PictureWord> words = selection.Words;
        if (DocumentEngine.IsOcrAvailable)
        {
            try
            {
                var read = await DocumentEngine.ReadWordsAsync(pixels, area.Width, area.Height, cancellationToken, everyLanguage: true).ConfigureAwait(false);
                var placed = read.Select(w => w with { X = w.X + area.X, Y = w.Y + area.Y }).ToList();
                if (stroke is not null)
                    placed = placed.Where(w => Takes(selection, stroke, w)).ToList();
                // The larger reading wins unless it found nothing the first one did.
                if (placed.Count > 0 || selection.Words.Count == 0)
                    words = placed;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log.Info($"Circle: reading the area: {ex.Message}");
            }
        }
        var lines = CircleText.Lines(words);
        var blocks = CircleText.Blocks(lines);
        var text = string.Join("\n", lines.Select(l => l.Text));
        var table = words.Count >= 4 ? PictureTables.FromWords(words) : null;
        double cover = words.Sum(w => w.Width * w.Height) / Math.Max(1.0, area.Width * (double)area.Height);
        return new CircleContent
        {
            Picture = picture,
            Pixels = pixels,
            Area = area,
            Words = words,
            Lines = lines,
            Blocks = blocks,
            Text = text,
            Finds = Finder.Find(ScreenTextWindow.JoinLines(text)),
            Codes = await codes.ConfigureAwait(false),
            Table = table is { Columns: >= 2 } && table.Rows.Count >= 2 && table.Rows.Sum(r => r.Count(c => c.Length > 0)) >= 4 ? table : null,
            TextCover = Math.Min(1, cover),
        };
    }

    /// <summary>A word read again belongs to the circle when the loop holds its middle or the stroke passes over it.</summary>
    private static bool Takes(CircleArea selection, IReadOnlyList<Point> stroke, PictureWord w) => CircleText.Takes(stroke, selection.Closed, w);

    internal static byte[] Crop(byte[] still, int stillWidth, Int32Rect area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        for (int y = 0; y < area.Height; y++)
            Buffer.BlockCopy(still, ((area.Y + y) * stillWidth + area.X) * 4, pixels, y * area.Width * 4, area.Width * 4);
        return pixels;
    }
}

/// <summary>A petal of the circle's wheel: what it does with what was circled.</summary>
internal sealed record CircleChoice(string Id, Func<Task> Run);

/// <summary>The petals the circle's wheel can show, and which of them suit what was circled, most useful first.</summary>
internal static class CircleChoices
{
    /// <summary>Every petal: its id, its label on the wheel, its name in the centre, and its icon.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, string Name, string Icon)> All =
    [
        ("link", "OPEN LINK", "Open the Link", "externalLink"),
        ("email", "EMAIL", "Write an E-mail", "mail"),
        ("call", "CALL", "Call the Number", "phone"),
        ("event", "CALENDAR", "Add to Calendar", "calendar"),
        ("time", "YOUR TIME", "In Your Own Time", "history"),
        ("convert", "CONVERT", "Convert the Units", "ruler"),
        ("money", "CURRENCY", "Convert the Currency", "coins"),
        ("answer", "RESULT", "Copy the Answer", "calculator"),
        ("colour", "COLOUR", "Copy the Colour", "dropper"),
        ("code", "QR CODE", "Open What the Code Says", "qr"),
        ("copy", "COPY TEXT", "Copy the Text", "copy"),
        ("translate", "TRANSLATE", "Translate on the Screen", "languages"),
        ("explain", "EXPLAIN", "Explain It", "sparkles"),
        ("ask", "ASK", "Ask About It", "ask"),
        ("summary", "SUMMARY", "Summarize It", "summary"),
        ("search", "SEARCH", "Search the Web", "globe"),
        ("read", "READ ALOUD", "Read It Aloud", "speech"),
        ("files", "MY FILES", "Find in My Files", "fileSearch"),
        ("table", "TABLE", "Copy as a Table", "sheet"),
        ("excel", "EXCEL", "Save as Excel", "fileSpreadsheet"),
        ("describe", "DESCRIBE", "Describe the Picture", "eye"),
        ("picture", "COPY PICTURE", "Copy the Picture", "image"),
        ("save", "SAVE", "Save the Picture", "download"),
        ("cutout", "CUT OUT", "Cut Out the Subject", "scissors"),
        ("pin", "PIN", "Pin It on Top", "pinPicture"),
        ("colours", "COLOURS", "Its Colours", "palette"),
        ("tools", "TOOLS", "All Tools for the Picture", "wand"),
        ("picturegroup", "PICTURE", "The Picture", "image"),
        ("more", "MORE", "More", "layoutGrid"),
    ];

    public static IReadOnlyList<string> Labels => All.Select(c => c.Label).ToList();

    public static (string Id, string Label, string Name, string Icon) Get(string id) => All.First(c => c.Id == id);

    /// <summary>The most petals the wheel shows at once; the rest go under MORE.</summary>
    public const int MostPetals = 12;

    /// <summary>
    /// The petals for what was circled, in order: first what its text holds (a link to open, a number to call, a date for
    /// the calendar, units or money to convert, a sum's answer, a code), then what suits words or a picture.
    /// </summary>
    /// <param name="searchOn">Search by Meaning has read the user's files, so "Find in My Files" can work.</param>
    public static IReadOnlyList<string> For(CircleContent content, bool searchOn)
    {
        var ids = new List<string>();
        void Add(string id)
        {
            if (!ids.Contains(id))
                ids.Add(id);
        }
        foreach (var f in content.Finds)
            switch (f.Kind)
            {
                case FoundKind.Link: Add("link"); break;
                case FoundKind.Email: Add("email"); break;
                case FoundKind.Phone: Add("call"); break;
                case FoundKind.When when f.HasDate: Add("event"); break;
                case FoundKind.When when f.Offset is not null || f.Zone is not null: Add("time"); break;
                case FoundKind.Measure: Add("convert"); break;
                case FoundKind.Money: Add("money"); break;
                case FoundKind.Sum: Add("answer"); break;
                case FoundKind.Colour: Add("colour"); break;
            }
        // A date with its zone is also worth seeing in the reader's own time.
        if (content.Finds.Any(f => f is { Kind: FoundKind.When, HasDate: true, HasTime: true } && (f.Offset is not null || f.Zone is not null)))
            Add("time");
        if (content.Codes.Count > 0)
            Add("code");
        var text = new List<string> { "copy", "translate", "explain", "ask", "search", "read" };
        if (content.WordCount >= 60)
            text.Insert(4, "summary");
        if (content.Table is not null)
            text.Add("table");
        if (searchOn)
            text.Add("files");
        var picture = new List<string> { "describe", "ask", "picture", "save", "cutout", "pin", "colours", "tools" };
        switch (content.Kind)
        {
            case CircleKind.Picture:
                picture.ForEach(Add);
                break;
            case CircleKind.Mixed:
                picture.ForEach(Add);
                text.ForEach(Add);
                break;
            default:
                text.ForEach(Add);
                Add("picturegroup");
                break;
        }
        return ids;
    }

    /// <summary>The petals of the PICTURE group for circled words: their picture, to copy, keep or take further.</summary>
    public static IReadOnlyList<string> PictureGroup { get; } = ["picture", "save", "pin", "tools"];

    /// <summary>The title and line in the wheel's centre: the answer when there is one, otherwise what was circled.</summary>
    public static (string Title, string Subtitle) Hub(CircleContent content, ExchangeRates? rates, string? home)
    {
        var first = content.Finds.FirstOrDefault(f => f.Kind == FoundKind.Sum) ?? content.Finds.FirstOrDefault();
        switch (first?.Kind)
        {
            case FoundKind.Sum:
                return ("= " + first.Value, first.Text);
            case FoundKind.Measure when Calculator.ForReader(first.Amount, first.Unit) is { Count: > 0 } answers:
                return (answers[0].Text, first.Text);
            case FoundKind.Money:
                if (rates is not null && Target(first.Unit, home) is { } to && rates.Convert(first.Amount, first.Unit, to) is { } converted)
                    return ("≈ " + Money.Format(converted, to), Money.Format(first.Amount, first.Unit));
                return (Money.Format(first.Amount, first.Unit), L.T("Amount of money"));
            case FoundKind.When when first.Utc is { } utc:
                return (Moment(utc.ToLocalTime(), first.HasDate), L.T("In your time"));
            case FoundKind.When when first.When is { } when:
                return (Moment(when, first.HasTime), L.T(first.HasTime ? "Date and time" : "Date"));
            case FoundKind.Colour:
                return (first.Value, L.T("Colour"));
            case FoundKind.Link:
                return (Uri.TryCreate(first.Value, UriKind.Absolute, out var uri) ? uri.Host : first.Text, L.T("Link"));
            case FoundKind.Email:
                return (L.T("E-mail address"), ForHub(first.Value));
            case FoundKind.Phone:
                return (first.Text, L.T("Phone number"));
        }
        if (content.Codes.Count > 0)
            return (L.T(content.Codes[0].Format == "QR_CODE" ? "QR code" : "Barcode"), ForHub(content.Codes[0].Text));
        if (content.Kind == CircleKind.Picture)
            return (L.T("Picture"), $"{content.Area.Width} × {content.Area.Height}");
        var words = content.WordCount;
        var preview = string.Join(' ', content.Text.Split((char[])['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries).Take(6));
        return (L.P(words, "{0} word", "{0} words"), preview);
    }

    /// <summary>
    /// A long thing as the wheel's centre can show it: a web address by its site, and long words broken where they can be
    /// (after @ / . - _), all in forty letters at most.
    /// </summary>
    internal static string ForHub(string text)
    {
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            trimmed = uri.Host.Length > 4 && uri.Host[3] == '.' && uri.Host.StartsWith("www", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var words = trimmed.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            if (w.Length <= 14)
                return w;
            var broken = new System.Text.StringBuilder();
            foreach (var c in w)
            {
                broken.Append(c);
                if (c is '@' or '/' or '.' or '-' or '_')
                    broken.Append(' ');
            }
            return broken.ToString().Trim();
        });
        var result = string.Join(' ', words);
        return result.Length > 40 ? result[..39].TrimEnd() + "…" : result;
    }

    /// <summary>The currency an amount goes into: the reader's own, or the dollar when it is already in theirs.</summary>
    public static string? Target(string from, string? home) =>
        home is null || home == from ? (from == "USD" ? "EUR" : "USD") : home;

    /// <summary>"Thu 5 Nov, 3:00 PM": a moment as the reader's language writes it.</summary>
    public static string Moment(DateTime when, bool withTime)
    {
        var culture = L.Culture;
        var date = when.ToString(when.Year == DateTime.Now.Year ? "ddd d MMM" : "ddd d MMM yyyy", culture);
        return withTime ? date + ", " + when.ToString("t", culture) : date;
    }
}
