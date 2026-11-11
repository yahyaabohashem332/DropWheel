using Petal.Core.Models;

namespace Petal.Tests;

/// <summary>Keeping the downloaded AI models in a folder the user chooses, and moving them there.</summary>
[Collection(nameof(ModelFolderTests))]
public sealed class ModelFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PetalModelFolder", Guid.NewGuid().ToString("N")[..10]);
    private readonly string _saved = ModelStore.Folder;

    public ModelFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ModelStore.Folder = _saved;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Two models (one in a folder of its own), a program unpacked beside them, and things that aren't DropWheel's.</summary>
    private static void Fill(string folder)
    {
        Write(Path.Combine(folder, ModelCatalog.DeepFilter.FileName), 3000);
        Write(Path.Combine(folder, ModelCatalog.ChatterboxVocabulary.FileName), 2000);
        Write(Path.Combine(folder, ModelStore.LlamaRuntime, "llama-server.exe"), 1000);
        Write(Path.Combine(folder, "notes.txt"), 10);
        Write(Path.Combine(folder, "photos", "beach.jpg"), 10);
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray());
    }

    private static void AssertMoved(string from, string to)
    {
        Assert.True(File.Exists(Path.Combine(to, ModelCatalog.DeepFilter.FileName)));
        Assert.Equal(2000, new FileInfo(Path.Combine(to, ModelCatalog.ChatterboxVocabulary.FileName)).Length);
        Assert.True(File.Exists(Path.Combine(to, ModelStore.LlamaRuntime, "llama-server.exe")));
        Assert.False(File.Exists(Path.Combine(from, ModelCatalog.DeepFilter.FileName)));
        Assert.False(Directory.Exists(Path.Combine(from, ModelStore.LlamaRuntime)));
        // The folder of a model of several files goes with it.
        Assert.False(Directory.Exists(Path.Combine(from, Path.GetDirectoryName(ModelCatalog.ChatterboxVocabulary.FileName)!)));
        // What isn't DropWheel's stays where it was.
        Assert.True(File.Exists(Path.Combine(from, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(from, "photos", "beach.jpg")));
        Assert.False(File.Exists(Path.Combine(to, "notes.txt")));
        Assert.Empty(Directory.EnumerateFiles(to, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task On_one_drive_the_models_and_their_programs_move_and_nothing_else()
    {
        var from = Path.Combine(_root, "models");
        var to = Path.Combine(_root, "elsewhere", "AI models");
        Fill(from);
        ModelStore.Folder = from;
        Assert.Equal(3, ModelStore.ItemsIn(from).Count);
        Assert.Equal(6000, ModelStore.SizeOf(from));
        var changed = 0;
        void Changed() => changed++;
        ModelStore.FolderChanged += Changed;
        try
        {
            Assert.Null(ModelStore.Check(to));
            await ModelStore.MoveAsync(to);
        }
        finally
        {
            ModelStore.FolderChanged -= Changed;
        }

        Assert.Equal(to, ModelStore.Folder);
        Assert.Equal(1, changed);
        AssertMoved(from, to);
    }

    [Fact]
    public async Task To_another_drive_the_models_are_copied_then_taken_away()
    {
        // The tests' own folder, when it is on another drive than the temporary folder (as on a PC with D:).
        var other = Path.Combine(AppContext.BaseDirectory, "model-folder-" + Guid.NewGuid().ToString("N")[..8]);
        if (string.Equals(Path.GetPathRoot(other), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var from = Path.Combine(_root, "models");
            Fill(from);
            ModelStore.Folder = from;
            var reported = new List<double>();
            await ModelStore.MoveAsync(other, new Progress<double>(reported.Add));
            Assert.Equal(other, ModelStore.Folder);
            AssertMoved(from, other);
        }
        finally
        {
            if (Directory.Exists(other))
                Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task A_move_that_fails_part_way_leaves_every_model_where_it_was()
    {
        var from = Path.Combine(_root, "models");
        var to = Path.Combine(_root, "elsewhere");
        Fill(from);
        ModelStore.Folder = from;
        // The second model in use, as a tool would hold it open: the first, already moved, comes back.
        using (new FileStream(Path.Combine(from, ModelCatalog.ChatterboxVocabulary.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => ModelStore.MoveAsync(to));

        Assert.Equal(from, ModelStore.Folder);
        Assert.Equal(3, ModelStore.ItemsIn(from).Count);
        Assert.Empty(ModelStore.ItemsIn(to));
    }

    [Fact]
    public async Task A_copy_to_another_drive_that_fails_leaves_nothing_there_and_everything_here()
    {
        var other = Path.Combine(AppContext.BaseDirectory, "model-folder-" + Guid.NewGuid().ToString("N")[..8]);
        if (string.Equals(Path.GetPathRoot(other), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var from = Path.Combine(_root, "models");
            Fill(from);
            ModelStore.Folder = from;
            using (new FileStream(Path.Combine(from, ModelStore.LlamaRuntime, "llama-server.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => ModelStore.MoveAsync(other));

            Assert.Equal(from, ModelStore.Folder);
            Assert.Equal(3, ModelStore.ItemsIn(from).Count);
            Assert.Empty(Directory.Exists(other) ? Directory.EnumerateFiles(other, "*", SearchOption.AllDirectories) : []);
        }
        finally
        {
            if (Directory.Exists(other))
                Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task A_model_already_in_the_new_folder_is_kept_and_the_old_copy_taken_away()
    {
        var from = Path.Combine(_root, "models");
        var to = Path.Combine(_root, "elsewhere");
        Fill(from);
        Write(Path.Combine(to, ModelCatalog.DeepFilter.FileName), 3000);
        ModelStore.Folder = from;
        await ModelStore.MoveAsync(to);
        AssertMoved(from, to);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_refused()
    {
        var file = Path.Combine(_root, "a file");
        File.WriteAllText(file, "not a folder");
        var problem = ModelStore.Check(Path.Combine(file, "models"));
        Assert.Equal(ModelFolderProblemKind.NotWritable, problem?.Kind);
    }

    [Fact]
    public void The_folder_for_a_choice_falls_back_to_the_usual_one()
    {
        Assert.Equal(ModelStore.DefaultFolder, ModelStore.FolderFor(""));
        Assert.Equal(ModelStore.DefaultFolder, ModelStore.FolderFor("models"));
        // A drive this PC hasn't, as in settings brought from another PC.
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        if ("QRSTUVWXYZ".FirstOrDefault(c => !used.Contains(c)) is var free and not '\0')
            Assert.Equal(ModelStore.DefaultFolder, ModelStore.FolderFor($"{free}:\\AI\\models"));
        var chosen = Path.Combine(_root, "AI models") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(_root, "AI models"), ModelStore.FolderFor(chosen));
    }

    [Fact]
    public void The_chosen_folder_is_kept_in_the_settings()
    {
        var settings = new Petal.App.Settings.AppSettings { ModelsFolder = @"D:\AI models" };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, Petal.App.Settings.SettingsStore.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<Petal.App.Settings.AppSettings>(json, Petal.App.Settings.SettingsStore.Options)!;
        Assert.Equal(@"D:\AI models", back.ModelsFolder);
        Assert.Equal("", new Petal.App.Settings.AppSettings().ModelsFolder);
    }
}
