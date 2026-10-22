using System.Net;
using Petal.Engines.Files.Sharing;

namespace Petal.Tests;

/// <summary>Sending files between PCs, tried on this PC alone (loopback): accepted, refused, and names kept inside the folder.</summary>
public sealed class PetalDropTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-drop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Files_and_folders_arrive_whole_when_accepted()
    {
        var source = Directory.CreateDirectory(Path.Combine(_dir, "from")).FullName;
        var photo = Path.Combine(source, "photo.jpg");
        var bytes = new byte[300_000];
        new Random(5).NextBytes(bytes);
        await File.WriteAllBytesAsync(photo, bytes);
        var trip = Directory.CreateDirectory(Path.Combine(source, "Trip", "Day 1")).FullName;
        await File.WriteAllTextAsync(Path.Combine(trip, "notes.txt"), "سلام");
        var inbox = Path.Combine(_dir, "inbox");
        DropOffer? asked = null;
        var arrived = new TaskCompletionSource<IReadOnlyList<string>>();
        await using var receiver = DropReceiver.StartLocal("Other PC", "other", inbox, offer => { asked = offer; return Task.FromResult(true); });
        receiver.Received += (_, files) => arrived.TrySetResult(files);
        var outcome = await PetalDrop.SendAsync(new DropPeer("Other PC", "other", IPAddress.Loopback, receiver.Port), "This PC", [photo, Path.Combine(source, "Trip")]);
        Assert.Equal(DropOutcome.Sent, outcome);
        var files = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, files.Count);
        Assert.Equal("This PC", asked!.From);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(inbox, "photo.jpg")));
        Assert.Equal("سلام", await File.ReadAllTextAsync(Path.Combine(inbox, "Trip", "Day 1", "notes.txt")));
        // Sent again: kept beside the first, not over it.
        Assert.Equal(DropOutcome.Sent, await PetalDrop.SendAsync(new DropPeer("Other PC", "other", IPAddress.Loopback, receiver.Port), "This PC", [photo]));
        await Task.Delay(300);
        Assert.True(File.Exists(Path.Combine(inbox, "photo 2.jpg")));
    }

    [Fact]
    public async Task Nothing_is_saved_when_refused()
    {
        var file = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(file, "x");
        var inbox = Path.Combine(_dir, "inbox");
        await using var receiver = DropReceiver.StartLocal("Other PC", "other", inbox, _ => Task.FromResult(false));
        var outcome = await PetalDrop.SendAsync(new DropPeer("Other PC", "other", IPAddress.Loopback, receiver.Port), "This PC", [file]);
        Assert.Equal(DropOutcome.Declined, outcome);
        Assert.False(Directory.Exists(inbox) && Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_PC_that_isnt_there_is_unreachable()
    {
        var file = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(file, "x");
        var outcome = await PetalDrop.SendAsync(new DropPeer("Gone", "gone", IPAddress.Loopback, 1), "This PC", [file]);
        Assert.Equal(DropOutcome.Unreachable, outcome);
    }

    [Theory]
    [InlineData("../../Windows/evil.dll", @"Windows\evil.dll")]
    [InlineData(@"C:\Users\x\a.txt", @"C\Users\x\a.txt")]
    [InlineData("Trip/./Day 1/a.jpg", @"Trip\Day 1\a.jpg")]
    [InlineData("..", null)]
    [InlineData("con:fig?.txt", "config.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("music/nul.tar.gz", @"music\_nul.tar.gz")]
    [InlineData("LPT1 .txt", "_LPT1 .txt")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("notes. . ", "notes")]
    public void Names_from_another_PC_stay_inside_the_folder(string name, string? safe) =>
        Assert.Equal(safe, PetalDrop.SafeRelativePath(name));

    [Fact]
    public void A_PC_name_is_one_short_line()
    {
        Assert.Equal("Sara's laptop", PetalDrop.ShortName("  Sara's\r\n laptop\u202E "));
        Assert.Equal(41, PetalDrop.ShortName(new string('x', 300)).Length);
    }

    [Fact]
    public async Task A_second_offer_while_the_first_is_asked_is_turned_down()
    {
        var file = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(file, "x");
        var inbox = Path.Combine(_dir, "inbox");
        var answer = new TaskCompletionSource<bool>();
        int asked = 0;
        await using var receiver = DropReceiver.StartLocal("Other PC", "other", inbox, _ => { Interlocked.Increment(ref asked); return answer.Task; });
        var peer = new DropPeer("Other PC", "other", IPAddress.Loopback, receiver.Port);
        var first = PetalDrop.SendAsync(peer, "This PC", [file]);
        for (int i = 0; i < 50 && Volatile.Read(ref asked) == 0; i++)
            await Task.Delay(20);
        Assert.Equal(DropOutcome.Declined, await PetalDrop.SendAsync(peer, "Someone", [file]));
        answer.SetResult(true);
        Assert.Equal(DropOutcome.Sent, await first);
        Assert.Equal(1, asked);
    }
}
