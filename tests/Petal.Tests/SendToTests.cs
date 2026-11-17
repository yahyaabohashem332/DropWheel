using System.IO;
using Petal.App.Shell;

namespace Petal.Tests;

/// <summary>DropWheel in Send to: put there with the right-click switch, taken away with it, and never over another program's shortcut.</summary>
public sealed class SendToTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "petal-sendto-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_shortcut_comes_and_goes_with_the_switch()
    {
        var exe = Path.Combine(_folder, "app", "Petal.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var link = Path.Combine(_folder, SendTo.ShortcutName);

        SendTo.Apply(exe, _folder);
        Assert.True(File.Exists(link));
        Assert.Equal(exe, SendTo.Target(link), ignoreCase: true);

        // Moved to another folder: the shortcut follows.
        var moved = Path.Combine(_folder, "new", "Petal.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllBytes(moved, [0x4D, 0x5A]);
        SendTo.Apply(moved, _folder);
        Assert.Equal(moved, SendTo.Target(link), ignoreCase: true);

        SendTo.Apply(null, _folder);
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void Another_program_s_shortcut_of_that_name_is_left_alone()
    {
        var other = Path.Combine(_folder, "other", "Notepad2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, [0x4D, 0x5A]);
        var link = Path.Combine(_folder, SendTo.ShortcutName);
        SendTo.Apply(other, _folder);
        Assert.Equal(other, SendTo.Target(link), ignoreCase: true);

        var exe = Path.Combine(_folder, "app", "Petal.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        SendTo.Apply(exe, _folder);
        Assert.Equal(other, SendTo.Target(link), ignoreCase: true);
        SendTo.Apply(null, _folder);
        Assert.True(File.Exists(link));
    }
}
