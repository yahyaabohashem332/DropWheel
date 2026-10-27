using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Overlay;

namespace Petal.App.Screen;

/// <summary>What the design snapshots need to show Circle Anything in each of its states, off the screen.</summary>
internal sealed partial class CircleWindow
{
    /// <summary>The window as it looks now, as a picture the size of the still.</summary>
    internal BitmapSource SnapshotImage()
    {
        UpdateLayout();
        Layout();
        _root.Measure(new Size(_width, _height));
        _root.Arrange(new Rect(0, 0, _width, _height));
        _root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Waits for the whole screen to be read, so the pointer can light paragraphs.</summary>
    internal async Task SnapshotReadyAsync()
    {
        var words = await _screenWords;
        _lines = CircleText.Lines(words);
        _blocks = CircleText.Blocks(_lines);
    }

    internal void SnapshotHover(Point p) => Hover(p);

    /// <summary>Whether the last stroke or click took something to show the wheel on.</summary>
    internal bool HasContent => _content is not null;

    /// <summary>The whole screen's words between two heights, as Windows read them, for checking.</summary>
    internal string SnapshotScreenWords(double top, double bottom) => !_screenWords.IsCompletedSuccessfully ? "(not read)"
        : string.Join(Environment.NewLine, _screenWords.Result.Where(w => w.CentreY >= top && w.CentreY <= bottom).Select(w => $"  {w.Line} x={w.X:0}-{w.Right:0} y={w.Y:0} h={w.Height:0} '{w.Text}'"));

    /// <summary>What was read, for checking: the text, then each word with where it is and its line.</summary>
    internal string SnapshotWords() => _content is not { } c ? "(nothing)"
        : c.Text + Environment.NewLine + string.Join(Environment.NewLine, c.Words.Select(w => $"  {w.Line} x={w.X:0} '{w.Text}'"));

    /// <summary>A stroke drawn as the pointer would: the area shows at once and its reading runs (await the task for the rest).</summary>
    internal Task SnapshotStrokeAsync(IReadOnlyList<Point> stroke)
    {
        Reset();
        _hover.Visibility = Visibility.Collapsed;
        _points.Clear();
        _points.AddRange(stroke);
        return FinishAsync(stroke[^1]);
    }

    /// <summary>The wheel's page for what was circled, to draw the wheel over the snapshot.</summary>
    internal RingPage SnapshotPage() => Page(_content!, Items(_content!));

    internal Task SnapshotRunAsync(string id) => RunAsync(id);

    /// <summary>A translation laid over the circled paragraphs, with its card, without the translation model.</summary>
    internal void SnapshotTranslation(IReadOnlyList<string> lines)
    {
        var card = ShowCard("languages", "Translation");
        CircleTranslation.Render(_translation, _content!.Blocks, lines, _pixels, _width, _height);
        _translation.Visibility = Visibility.Visible;
        card.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        card.Done(Localization.L.T("Translated on this PC."));
        card.CopyChip("Copy", () => card.Text);
        card.Chip("eye", "Original", () => { });
        card.Chip("speech", "Read Aloud", () => { });
    }

    /// <summary>An answer in a card, the way Explain and Ask show theirs once the model has written.</summary>
    internal void SnapshotAnswer(string icon, string title, string text, bool ask)
    {
        var card = ShowCard(icon, title);
        card.Text = text;
        card.Done(Localization.L.T("Worked out on this PC; it can be wrong."));
        if (ask)
            card.AskBox("What do you want to know about it?");
        else
        {
            card.CopyChip("Copy", () => card.Text);
            card.Chip("ask", "Ask More", () => { });
        }
    }
}
