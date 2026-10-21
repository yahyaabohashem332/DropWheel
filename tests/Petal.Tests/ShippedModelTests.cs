using Petal.Core.Models;

namespace Petal.Tests;

/// <summary>The Store's package brings Piper and llama.cpp with it instead of downloading them.</summary>
[Collection("Models")]
public sealed class ShippedModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-shipped-" + Guid.NewGuid().ToString("N"));
    private readonly string _savedFolder = ModelStore.Folder;
    private readonly string _savedBundled = ModelStore.BundledFolder;

    public ShippedModelTests()
    {
        ModelStore.Folder = Path.Combine(_dir, "downloads");
        ModelStore.BundledFolder = Path.Combine(_dir, "app", "tools", "models");
        Directory.CreateDirectory(ModelStore.Folder);
        Directory.CreateDirectory(ModelStore.BundledFolder);
    }

    public void Dispose()
    {
        ModelStore.Folder = _savedFolder;
        ModelStore.BundledFolder = _savedBundled;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ModelInfo Tiny(string file) => new("tiny", "Tiny", file, "https://example.invalid/" + file, new string('0', 64), 5, "MIT");

    [Fact]
    public void A_download_that_ships_with_the_app_is_found_there_and_never_deleted()
    {
        var model = Tiny("tiny.zip");
        Assert.Null(ModelStore.Find(model));
        var shipped = Path.Combine(ModelStore.BundledFolder, model.FileName);
        File.WriteAllBytes(shipped, [1, 2, 3, 4, 5]);
        Assert.Equal(shipped, ModelStore.Find(model));
        ModelStore.Delete(model);
        Assert.True(File.Exists(shipped));

        // A downloaded copy comes first, and is the one removed.
        var downloaded = Path.Combine(ModelStore.Folder, model.FileName);
        File.WriteAllBytes(downloaded, [5, 4, 3, 2, 1]);
        Assert.Equal(downloaded, ModelStore.Find(model));
        ModelStore.Delete(model);
        Assert.False(File.Exists(downloaded));
        Assert.Equal(shipped, ModelStore.Find(model));
    }

    [Fact]
    public void A_shipped_file_of_the_wrong_size_is_not_used()
    {
        var model = Tiny("short.zip");
        File.WriteAllBytes(Path.Combine(ModelStore.BundledFolder, model.FileName), [1, 2]);
        Assert.Null(ModelStore.Find(model));
    }
}
