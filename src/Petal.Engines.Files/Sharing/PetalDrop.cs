using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Petal.Engines.Files.Sharing;

/// <summary>Another PC on the network with Petal set to receive.</summary>
public sealed record DropPeer(string Name, string Id, IPAddress Address, int Port);

/// <summary>What a PC wants to send: who from, and each file's path (relative, with "/") and size.</summary>
public sealed record DropOffer(string From, IReadOnlyList<DropFile> Files)
{
    public long Total => Files.Sum(f => f.Size);
}

public sealed record DropFile(string Name, long Size);

/// <summary>How a send ended.</summary>
public enum DropOutcome
{
    Sent,
    Declined,
    Unreachable,
    Failed,
}

/// <summary>
/// Sending files between two PCs with Petal on the same network, with no account and no internet. A PC
/// that receives answers a question broadcast on the network ("who has Petal?"), and asks its user before
/// anything is saved; files are saved under Downloads\Petal with their folders, never outside it.
/// </summary>
public static class PetalDrop
{
    public const int DiscoveryPort = 47131;
    private const string Question = "petal-drop?1";
    internal const int MaxFiles = 10_000;
    private const int LongestName = 40;

    /// <summary>The PCs that answer within <paramref name="wait"/>, by name.</summary>
    public static async Task<IReadOnlyList<DropPeer>> DiscoverAsync(TimeSpan wait, string ownId, CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var ask = Encoding.UTF8.GetBytes(Question);
        foreach (var target in BroadcastAddresses())
        {
            try { await udp.SendAsync(ask, new IPEndPoint(target, DiscoveryPort), cancellationToken); }
            catch (SocketException) { }
        }
        var peers = new Dictionary<string, DropPeer>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var answer = await udp.ReceiveAsync(timeout.Token);
                try
                {
                    var info = JsonSerializer.Deserialize<Announcement>(answer.Buffer);
                    if (info is { Id.Length: > 0 and <= 64, Name.Length: > 0 } && info.Id != ownId && info.Port is > 0 and < 65536)
                        peers[info.Id] = new DropPeer(ShortName(info.Name), info.Id, answer.RemoteEndPoint.Address, info.Port);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        return peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Every network's broadcast address, and the general one.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    internal sealed record Announcement(string Name, string Id, int Port);

    /// <summary>A PC's name as shown: one line, no control characters, at most 40 characters.</summary>
    public static string ShortName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        return clean.Length <= LongestName ? clean : clean[..LongestName].TrimEnd() + "…";
    }

    /// <summary>The files to send, with their names as the other PC will save them: folders keep their insides.</summary>
    public static List<(string Path, DropFile File)> Collect(IReadOnlyList<string> paths)
    {
        var list = new List<(string, DropFile)>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                list.Add((path, new DropFile(Path.GetFileName(path), new FileInfo(path).Length)));
            }
            else if (Directory.Exists(path))
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/')) ?? "";
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
                        continue;
                    list.Add((file, new DropFile(Path.GetRelativePath(root, file).Replace('\\', '/'), info.Length)));
                }
            }
        }
        return list;
    }

    /// <summary>Offers the files to a PC and, once its user accepts, sends them.</summary>
    public static async Task<DropOutcome> SendAsync(DropPeer peer, string from, IReadOnlyList<string> paths, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = Collect(paths);
        if (files.Count == 0 || files.Count > MaxFiles)
            return DropOutcome.Failed;
        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(peer.Address, peer.Port, connect.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return DropOutcome.Unreachable;
        }
        try
        {
            var stream = client.GetStream();
            var offer = new DropOffer(from, files.Select(f => f.File).ToList());
            await WriteBlockAsync(stream, JsonSerializer.SerializeToUtf8Bytes(offer), cancellationToken);
            // The other side waits for its user: up to two minutes.
            using var answerWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answerWait.CancelAfter(TimeSpan.FromMinutes(2));
            var answer = Encoding.UTF8.GetString(await ReadBlockAsync(stream, 64, answerWait.Token));
            if (answer != "yes")
                return DropOutcome.Declined;
            long total = Math.Max(1, offer.Total), sent = 0;
            var buffer = new byte[1 << 16];
            foreach (var (path, _) in files)
            {
                await using var input = File.OpenRead(path);
                int n;
                while ((n = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    sent += n;
                    progress?.Report((double)sent / total);
                }
            }
            var done = Encoding.UTF8.GetString(await ReadBlockAsync(stream, 64, cancellationToken));
            return done == "done" ? DropOutcome.Sent : DropOutcome.Failed;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            return DropOutcome.Failed;
        }
    }

    internal static async Task WriteBlockAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(data, ct);
    }

    internal static async Task<byte[]> ReadBlockAsync(Stream stream, int max, CancellationToken ct)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(length);
        if (n < 0 || n > max)
            throw new IOException("The other PC sent something unexpected.");
        var data = new byte[n];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    /// <summary>
    /// A relative name from the other PC, made safe: no drive, no "..", no characters Windows refuses, and
    /// no device names such as CON or NUL. Null when nothing usable is left.
    /// </summary>
    public static string? SafeRelativePath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.', ' '))
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(p => IsDeviceName(p) ? "_" + p : p)
            .ToList();
        return parts.Count == 0 ? null : Path.Combine([.. parts]);
    }

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"];

    /// <summary>"nul", "CON.txt", "com1.tar.gz": names Windows keeps for devices in every folder.</summary>
    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        return DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Receiving on this PC: answers the network's "who has Petal?", and takes offers, asking its user each
/// time. Runs only while switched on.
/// </summary>
public sealed class DropReceiver : IAsyncDisposable
{
    private const int MaxOffer = 4 * 1024 * 1024;
    private readonly TcpListener _listener;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    // One offer at a time: others are turned down at once, so nobody on the network can fill the screen with questions.
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly string _name, _id, _folder;
    private readonly Func<DropOffer, Task<bool>> _ask;

    /// <summary>Raised on a background thread when files arrived: who sent them and where they are.</summary>
    public event Action<string, IReadOnlyList<string>>? Received;

    /// <summary>Raised as files arrive, 0 to 1.</summary>
    public event Action<string, double>? Progress;

    public int Port { get; }

    private DropReceiver(string name, string id, string folder, Func<DropOffer, Task<bool>> ask, IPAddress address, bool discoverable)
    {
        _name = name;
        _id = id;
        _folder = folder;
        _ask = ask;
        _listener = new TcpListener(address, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        if (discoverable)
        {
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, PetalDrop.DiscoveryPort));
            _ = AnswerAsync();
        }
        _ = AcceptAsync();
    }

    /// <param name="ask">Asks the user whether to take an offer.</param>
    public static DropReceiver Start(string name, string id, string folder, Func<DropOffer, Task<bool>> ask) =>
        new(name, id, folder, ask, IPAddress.Any, discoverable: true);

    /// <summary>For tests: on this PC only, without answering the network.</summary>
    public static DropReceiver StartLocal(string name, string id, string folder, Func<DropOffer, Task<bool>> ask) =>
        new(name, id, folder, ask, IPAddress.Loopback, discoverable: false);

    private async Task AnswerAsync()
    {
        var reply = JsonSerializer.SerializeToUtf8Bytes(new PetalDrop.Announcement(_name, _id, Port));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var question = await _udp.ReceiveAsync(_stop.Token);
                if (Encoding.UTF8.GetString(question.Buffer) == "petal-drop?1")
                    await _udp.SendAsync(reply, question.RemoteEndPoint, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => TakeAsync(client));
        }
    }

    private async Task TakeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var offerWait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                offerWait.CancelAfter(TimeSpan.FromSeconds(20));
                var offer = JsonSerializer.Deserialize<DropOffer>(await PetalDrop.ReadBlockAsync(stream, MaxOffer, offerWait.Token));
                if (offer is null || offer.Files.Count is 0 or > PetalDrop.MaxFiles || offer.Files.Any(f => f.Size < 0 || PetalDrop.SafeRelativePath(f.Name) is null))
                    return;
                offer = offer with { From = PetalDrop.ShortName(offer.From ?? "") is { Length: > 0 } name ? name : "PC" };
                if (!await _asking.WaitAsync(0))
                {
                    await PetalDrop.WriteBlockAsync(stream, Encoding.UTF8.GetBytes("no"), _stop.Token);
                    return;
                }
                bool yes;
                try
                {
                    yes = await _ask(offer);
                }
                finally
                {
                    _asking.Release();
                }
                await PetalDrop.WriteBlockAsync(stream, Encoding.UTF8.GetBytes(yes ? "yes" : "no"), _stop.Token);
                if (!yes)
                    return;
                var from = offer.From;
                var saved = new List<string>();
                long total = Math.Max(1, offer.Total), got = 0;
                var buffer = new byte[1 << 16];
                foreach (var file in offer.Files)
                {
                    var target = Unique(Path.Combine(_folder, PetalDrop.SafeRelativePath(file.Name)!));
                    // Never outside the folder, whatever the name said.
                    if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(_folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A file name tried to leave the folder.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var output = File.Create(target))
                    {
                        long left = file.Size;
                        while (left > 0)
                        {
                            int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), _stop.Token);
                            if (n == 0)
                                throw new IOException("The other PC stopped sending.");
                            await output.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                            left -= n;
                            got += n;
                            Progress?.Invoke(from, (double)got / total);
                        }
                    }
                    saved.Add(target);
                }
                await PetalDrop.WriteBlockAsync(stream, Encoding.UTF8.GetBytes("done"), _stop.Token);
                Received?.Invoke(offer.From, saved);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var next = Path.Combine(folder, $"{stem} {i}{ext}");
            if (!File.Exists(next))
                return next;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _udp.Dispose();
        _stop.Dispose();
    }
}
