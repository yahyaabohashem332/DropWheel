using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Imaging.Tests;

/// <summary>
/// Search by meaning against the real models. PETAL_SEARCH_MODELS is a folder holding them (as
/// ModelStore names them); PETAL_SEARCH_IMAGES holds COCO val2017 photos 139 (living room), 632
/// (bedroom), 724 (stop sign), 776 (teddy bears), 1000 (tennis team) and 39769 (cats on a sofa).
/// </summary>
public sealed class MeaningTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("two cats sleeping on a pink sofa", "39769")]
    [InlineData("قطتان نائمتان على أريكة", "39769")]
    [InlineData("a stop sign by the road", "724")]
    [InlineData("إشارة توقف في الشارع", "724")]
    [InlineData("teddy bears", "776")]
    [InlineData("ألعاب أطفال من القماش", "776")]
    [InlineData("لافتة قف", "724")]
    [InlineData("أطفال يلعبون التنس", "1000")]
    [InlineData("صالة جلوس فيها تلفاز", "139")]
    [InlineData("a bedroom with a bed and books", "632")]
    [InlineData("غرفة نوم", "632")]
    [InlineData("children with tennis rackets", "1000")]
    [InlineData("a living room with a television", "139")]
    public void Words_find_the_picture_they_describe(string query, string expected)
    {
        var models = Environment.GetEnvironmentVariable("PETAL_SEARCH_MODELS");
        var images = Environment.GetEnvironmentVariable("PETAL_SEARCH_IMAGES");
        if (models is null || images is null)
        {
            output.WriteLine("Skipped: PETAL_SEARCH_MODELS and PETAL_SEARCH_IMAGES aren't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = models;
        try
        {
            Assert.True(Meaning.IsReady);
            var pictures = Directory.GetFiles(images, "*.jpg").ToDictionary(p => p, Meaning.EmbedImage);
            var asked = Meaning.EmbedText(query);
            var ranked = pictures.Select(p => (Name: Path.GetFileNameWithoutExtension(p.Key), Score: Meaning.Similarity(asked, p.Value)))
                .OrderByDescending(p => p.Score).ToList();
            output.WriteLine($"{query}: " + string.Join(", ", ranked.Select(r => $"{r.Name[5..].TrimStart('0')} {r.Score:0.000}")));
            Assert.EndsWith(expected, ranked[0].Name);
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }
}
