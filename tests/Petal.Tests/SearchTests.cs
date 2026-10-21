using ImageMagick;
using ImageMagick.Drawing;
using Petal.Core.Models;
using Petal.Engines.Imaging;
using Petal.Recipes.Search;
using Xunit.Abstractions;

namespace Petal.Tests;

[CollectionDefinition("Models", DisableParallelization = true)]
public sealed class ModelsCollection;

[Collection("Models")]
public sealed class SearchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-search-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (SearchIndex Index, SearchIndexer Indexer) Create()
    {
        var index = new SearchIndex(Path.Combine(_dir, "index", "search.db"));
        return (index, new SearchIndexer(index, Petal.App.EngineSetup.CreateConverters(), new Petal.Core.Jobs.ConversionSettings()));
    }

    private static async Task ReadAllAsync(SearchIndexer indexer)
    {
        while (await indexer.ReadNextAsync(CancellationToken.None))
        {
        }
    }

    [Fact]
    public async Task Words_inside_files_and_their_names_are_found()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(Path.Combine(files, "Contracts"));
        File.WriteAllText(Path.Combine(files, "Contracts", "flat.txt"), "This lease agreement for the flat starts in March.");
        File.WriteAllText(Path.Combine(files, "notes.md"), "# عقد إيجار الشقة\n\nيبدأ العقد في شهر آذار.");
        using (var screenshot = new MagickImage(MagickColors.White, 900, 300))
        {
            new Drawables().Font("Arial").FontPointSize(48).FillColor(MagickColors.Black).Text(40, 120, "Invoice 2041 paid").Draw(screenshot);
            screenshot.Write(Path.Combine(files, "capture.png"));
        }
        using (var photo = new MagickImage("plasma:", 320, 240))
            photo.Write(Path.Combine(files, "Holiday Beach 2024.jpg"));

        var (index, indexer) = Create();
        using (index)
        {
            Assert.Equal(4, indexer.Scan([files], CancellationToken.None));
            Assert.Equal(4, index.Status().Waiting);
            await ReadAllAsync(indexer);
            Assert.Equal(new IndexStatus(4, 0, 0), index.Status());

            string First(string query) => Path.GetFileName(index.Search(query, null).ByWords.FirstOrDefault()?.Path ?? "");
            Assert.Equal("flat.txt", First("lease agreement"));
            Assert.Equal("flat.txt", First("contracts"));
            // Arabic, with or without the hamza, and part of a word.
            Assert.Equal("notes.md", First("عقد ايجار"));
            Assert.Equal("notes.md", First("إيجار"));
            Assert.Equal("Holiday Beach 2024.jpg", First("beach"));
            if (Petal.Engines.Documents.DocumentEngine.IsOcrAvailable)
                Assert.Equal("capture.png", First("invoice 2041"));
            var hit = index.Search("march", null).ByWords.Single();
            output.WriteLine(hit.Snippet);
            Assert.Contains("«march»", hit.Snippet);

            // A file that's deleted is forgotten at the next look; a changed one is read again.
            File.Delete(Path.Combine(files, "Holiday Beach 2024.jpg"));
            File.AppendAllText(Path.Combine(files, "Contracts", "flat.txt"), " The deposit is two months.");
            indexer.Scan([files], CancellationToken.None);
            Assert.Equal(new IndexStatus(3, 1, 0), index.Status());
            await ReadAllAsync(indexer);
            Assert.Equal("", First("beach"));
            Assert.Equal("flat.txt", First("deposit"));
        }
    }

    /// <summary>With PETAL_SEARCH_MODELS and PETAL_SEARCH_IMAGES (see MeaningTests), pictures are found by what they show.</summary>
    [Fact]
    public async Task Pictures_are_found_by_what_they_show()
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
            var files = Path.Combine(_dir, "photos");
            Directory.CreateDirectory(files);
            foreach (var image in Directory.GetFiles(images, "*.jpg"))
                File.Copy(image, Path.Combine(files, Path.GetFileName(image)));
            var (index, indexer) = Create();
            using (index)
            {
                indexer.Scan([files], CancellationToken.None);
                await ReadAllAsync(indexer);
                Assert.Equal(6, index.Status().Pictures);
                foreach (var (query, expected) in new[] { ("two cats on a sofa", "39769"), ("قطط على أريكة", "39769"), ("stop sign", "724"), ("غرفة نوم", "632") })
                {
                    var hits = index.Search(query, Meaning.EmbedText(query)).ByMeaning;
                    output.WriteLine($"{query}: {string.Join(", ", hits.Select(h => $"{Path.GetFileNameWithoutExtension(h.Path)} {h.Score:0.000}"))}");
                    Assert.NotEmpty(hits);
                    Assert.EndsWith(expected, Path.GetFileNameWithoutExtension(hits[0].Path));
                    Assert.True(hits.Count <= 3, "Only the pictures that match, not all of them.");
                }
            }
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }
}
