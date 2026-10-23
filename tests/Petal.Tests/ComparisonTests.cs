using Petal.Recipes;
using SkiaSharp;

namespace Petal.Tests;

/// <summary>Comparing two versions: words added and removed, paragraphs moved in and out, pictures and a report.</summary>
public sealed class ComparisonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-compare-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Show(IEnumerable<DiffPart> parts) =>
        string.Concat(parts.Select(p => p.Kind switch { DiffKind.Added => "[+" + p.Text + "]", DiffKind.Removed => "[-" + p.Text + "]", _ => p.Text }));

    [Fact]
    public void A_changed_word_is_marked_in_its_sentence()
    {
        var diff = TextDiff.Compare("The quick brown fox jumps.\nSecond line stays.", "The quick red fox jumps.\nSecond line stays.");
        Assert.Equal(2, diff.Count);
        Assert.Equal("The quick [-brown ][+red ]fox jumps.", Show(diff[0].Parts));
        Assert.False(diff[1].Changed);
    }

    [Fact]
    public void Paragraphs_added_and_removed_stay_whole()
    {
        var before = "One.\nTwo.\nThree.\nFour.";
        var after = "One.\nThree.\nThree and a half.\nFour.";
        var diff = TextDiff.Compare(before, after);
        Assert.Equal(["One.", "[-Two.]", "Three.", "[+Three and a half.]", "Four."], diff.Select(p => Show(p.Parts)));
        // Nothing changed: nothing marked.
        Assert.All(TextDiff.Compare(before, before), p => Assert.False(p.Changed));
        // Extra spaces and blank lines aren't changes.
        Assert.All(TextDiff.Compare("a  b\n\nc", "a b\nc"), p => Assert.False(p.Changed));
    }

    [Fact]
    public void Arabic_words_are_compared_too()
    {
        var diff = TextDiff.Compare("مرحبا بكم في بتل", "أهلا بكم في بتل");
        Assert.Equal("[-مرحبا ][+أهلا ]بكم في بتل", Show(diff[0].Parts));
    }

    [Fact]
    public void A_long_text_with_a_few_changes_is_matched_quickly()
    {
        var words = Enumerable.Range(0, 20000).Select(i => "word" + (i % 97)).ToList();
        var before = string.Join(" ", words);
        words[5000] = "CHANGED";
        words.Insert(15000, "NEW");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var diff = TextDiff.Compare(before, string.Join(" ", words));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), started.Elapsed.ToString());
        var parts = diff.SelectMany(p => p.Parts).Where(p => p.Kind != DiffKind.Same).ToList();
        Assert.Contains(parts, p => p.Kind == DiffKind.Added && p.Text.Contains("CHANGED"));
        Assert.Contains(parts, p => p.Kind == DiffKind.Added && p.Text.Contains("NEW"));
        Assert.Single(parts, p => p.Kind == DiffKind.Removed);
    }

    private static readonly ComparisonText Words = new("Comparison", "Before", "After", "Same text.", "Same pictures.", "Text", "Pages",
        n => $"{n} added", n => $"{n} removed", n => $"{n} unchanged", n => $"Page {n}", "Only before", "Only after", "More pages", false);

    [Fact]
    public async Task Two_text_files_make_a_report()
    {
        var a = Path.Combine(_dir, "contract v1.txt");
        var b = Path.Combine(_dir, "contract v2.txt");
        await File.WriteAllTextAsync(a, "The rent is 500 a month.\nPaid on the first day.\nNo pets.");
        await File.WriteAllTextAsync(b, "The rent is 550 a month.\nPaid on the first day.\nNo pets.\nNo smoking.");
        var output = Path.Combine(_dir, "report.html");
        var result = await FileComparison.CompareAsync(a, b, output, Petal.App.EngineSetup.CreateConverters(), new Petal.Core.Jobs.ConversionSettings(), Words);
        Assert.Equal(3, result.AddedWords);
        Assert.Equal(1, result.RemovedWords);
        var html = await File.ReadAllTextAsync(output);
        Assert.Contains("<del>500 </del><ins>550 </ins>", html);
        Assert.Contains("<ins>No smoking.</ins>", html);
        Assert.Contains("contract v1.txt", html);
    }

    private string Picture(string name, bool dot)
    {
        using var bitmap = new SKBitmap(300, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Navy };
            canvas.DrawRect(20, 20, 120, 60, paint);
            if (dot)
                canvas.DrawCircle(230, 150, 18, new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true });
        }
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        return path;
    }

    [Fact]
    public async Task Two_pictures_show_where_they_differ()
    {
        var a = Picture("a.png", dot: false);
        var b = Picture("b.png", dot: true);
        var output = Path.Combine(_dir, "pictures.html");
        var result = await FileComparison.CompareAsync(a, b, output, Petal.App.EngineSetup.CreateConverters(), new Petal.Core.Jobs.ConversionSettings(), Words);
        Assert.Equal(1, result.ChangedPages);
        Assert.False(result.HasText);
        Assert.Contains("data:image/png;base64,", await File.ReadAllTextAsync(output));
        // Only around the new dot.
        using var x = SKBitmap.Decode(a);
        using var y = SKBitmap.Decode(b);
        var cells = FileComparison.ChangedCells(x, y, out int cell);
        Assert.All(cells, c => Assert.InRange(c.X * cell, 200, 260));
        Assert.Empty(FileComparison.ChangedCells(x, x, out _));
        var same = await FileComparison.CompareAsync(a, Picture("a copy.png", dot: false), Path.Combine(_dir, "same.html"), Petal.App.EngineSetup.CreateConverters(), new Petal.Core.Jobs.ConversionSettings(), Words);
        Assert.True(same.Same);
    }
}
