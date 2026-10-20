using Petal.Core.Actions;

namespace Petal.Tests;

public class BatchRenameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-rename-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public void Numbers_are_padded_to_the_largest_number()
    {
        var files = Enumerable.Range(0, 12).Select(i => Make($"IMG_{i}.jpg")).ToList();
        var plan = BatchRename.Plan(files, new RenameRule { Pattern = "Trip {n}" });
        Assert.Equal("Trip 01.jpg", plan[0].NewName);
        Assert.Equal("Trip 12.jpg", plan[11].NewName);
        Assert.All(plan, i => Assert.Equal(RenameProblem.None, i.Problem));
    }

    [Fact]
    public void Find_replace_case_and_numbers_combine()
    {
        var a = Make("holiday photo.JPG");
        var plan = BatchRename.Plan([a], new RenameRule { Pattern = "{name} {n}", Find = "PHOTO", Replace = "pic", Case = NameCase.Title, Start = 5 });
        // The extension is kept as it was.
        Assert.Equal("Holiday Pic 5.JPG", plan[0].NewName);
    }

    [Fact]
    public void Folder_token_is_the_parent_folder()
    {
        var a = Make("a.png");
        var plan = BatchRename.Plan([a], new RenameRule { Pattern = "{folder}-{name}" });
        Assert.Equal($"{Path.GetFileName(_dir)}-a.png", plan[0].NewName);
    }

    [Fact]
    public void Date_comes_from_the_photo_when_known()
    {
        var a = Make("a.jpg");
        var plan = BatchRename.Plan([a], new RenameRule { Pattern = "{date} {name}" }, _ => new DateTime(2024, 7, 3));
        Assert.Equal("2024-07-03 a.jpg", plan[0].NewName);
    }

    [Fact]
    public void Clashes_and_existing_files_are_flagged()
    {
        var a = Make("a.txt");
        var b = Make("b.txt");
        Make("keep.txt");
        var same = BatchRename.Plan([a, b], new RenameRule { Pattern = "x" });
        Assert.All(same, i => Assert.Equal(RenameProblem.Duplicate, i.Problem));

        var taken = BatchRename.Plan([a], new RenameRule { Pattern = "keep" });
        Assert.Equal(RenameProblem.Exists, taken[0].Problem);

        var empty = BatchRename.Plan([a], new RenameRule { Find = "a", Replace = "" });
        Assert.Equal(RenameProblem.Empty, empty[0].Problem);
    }

    [Fact]
    public void A_dotfile_keeps_its_name()
    {
        var dot = Make(".gitkeep");
        var plan = BatchRename.Plan([dot], new RenameRule());
        Assert.Equal(RenameProblem.None, plan[0].Problem);
        Assert.False(plan[0].Changes);
    }

    [Fact]
    public void Invalid_characters_become_dashes()
    {
        Assert.Equal("a-b-c", BatchRename.Clean("a:b?c. "));
    }

    [Fact]
    public void Names_can_be_swapped_and_undone()
    {
        var a = Make("a.txt");
        var b = Make("b.txt");
        // a → b and b → a in one go: the temporary step makes it possible.
        var plan = new List<RenameItem> { new(a, "b.txt", RenameProblem.None), new(b, "a.txt", RenameProblem.None) };
        var done = BatchRename.Apply(plan);
        Assert.Equal(2, done.Count);
        Assert.Equal("a.txt", File.ReadAllText(Path.Combine(_dir, "b.txt")));
        Assert.Equal("b.txt", File.ReadAllText(Path.Combine(_dir, "a.txt")));

        BatchRename.Undo(done);
        Assert.Equal("a.txt", File.ReadAllText(a));
        Assert.Equal("b.txt", File.ReadAllText(b));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Folders_are_renamed_without_an_extension()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Old.Stuff")).FullName;
        var plan = BatchRename.Plan([folder], new RenameRule { Pattern = "{name} 2024" });
        Assert.Equal("Old.Stuff 2024", plan[0].NewName);
        BatchRename.Apply(plan);
        Assert.True(Directory.Exists(Path.Combine(_dir, "Old.Stuff 2024")));
    }
}
