using System.IO;
using Petal.Recipes;

namespace Petal.Tests;

/// <summary>Edit by Text: what was taken out of a recording is kept, for the same hearing of the same file.</summary>
public sealed class TextEditingTests
{
    [Fact]
    public void What_was_taken_out_opens_again_as_it_was_left()
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-textedit-" + Guid.NewGuid().ToString("N")[..8]);
        var video = Path.Combine(folder, "talk.mp4");
        const string model = @"C:\models\ggml-small-q5_1.bin";
        var before = TextEditing.CacheFolder;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(video, [1, 2, 3]);
            TextEditing.CacheFolder = Path.Combine(folder, "cache");
            Assert.Null(TextEditing.Recall(video, model, "auto", 40));

            TextEditing.Remember(video, model, "auto", 40, new HashSet<int> { 12, 3, 7 });
            Assert.Equal([3, 7, 12], TextEditing.Recall(video, model, "auto", 40)!.Order().ToArray());

            // Heard another way (another model, another language, or into other pieces) is another edit.
            Assert.Null(TextEditing.Recall(video, @"C:\models\ggml-base-q5_1.bin", "auto", 40));
            Assert.Null(TextEditing.Recall(video, model, "en", 40));
            Assert.Null(TextEditing.Recall(video, model, "auto", 41));

            // Everything put back forgets it.
            TextEditing.Remember(video, model, "auto", 40, new HashSet<int>());
            Assert.Null(TextEditing.Recall(video, model, "auto", 40));

            // A changed file starts again.
            TextEditing.Remember(video, model, "auto", 40, new HashSet<int> { 1 });
            File.WriteAllBytes(video, [1, 2, 3, 4]);
            Assert.Null(TextEditing.Recall(video, model, "auto", 40));
        }
        finally
        {
            TextEditing.CacheFolder = before;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
