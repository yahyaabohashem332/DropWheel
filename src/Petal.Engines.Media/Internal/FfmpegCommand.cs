using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Petal.Engines.Media.Foundation;

namespace Petal.Engines.Media.Internal;

/// <summary>
/// An FFmpeg command made ready to run: inputs whose pictures or sound DropWheel's FFmpeg can't decode (H.264, HEVC,
/// AAC, WMV and the like) get a Windows bridge as an extra input, and every place the command reads those streams
/// from (its "-map"s, its filter graph, FFmpeg's own choice of streams) reads them from the bridge instead.
/// Streams only copied, subtitles, cover art, chapters and metadata still come from the file itself.
/// </summary>
internal sealed class PreparedCommand : IAsyncDisposable
{
    public PreparedCommand(IReadOnlyList<string> arguments, IReadOnlyList<WindowsBridge> bridges)
    {
        Arguments = arguments;
        Bridges = bridges;
    }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyList<WindowsBridge> Bridges { get; }

    /// <summary>What went wrong decoding through Windows: then FFmpeg's output is short, and the job has failed.</summary>
    public Exception? Failure => Bridges.Select(b => b.Failure).FirstOrDefault(f => f is not null);

    public async ValueTask DisposeAsync()
    {
        foreach (var bridge in Bridges)
            await bridge.DisposeAsync().ConfigureAwait(false);
    }
}

internal static partial class FfmpegCommand
{
    /// <summary>Options that take no value. Every other option takes the token after it.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "hide_banner", "nostdin", "stdin", "y", "n", "nostats", "stats", "benchmark", "benchmark_all", "xerror", "report",
        "debug_ts", "dump", "hex", "copy_unknown", "ignore_unknown", "print_graphs",
        "vn", "an", "sn", "dn", "shortest", "re", "copyts", "start_at_zero", "accurate_seek", "noaccurate_seek",
        "autorotate", "noautorotate", "copyinkf", "fix_sub_duration", "autoscale", "noautoscale", "bitexact",
        "display_hflip", "display_vflip",
    };

    /// <summary>Input formats that are no file to look into: raw data, devices, generated sources.</summary>
    private static readonly HashSet<string> RawFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "lavfi", "rawvideo", "f32le", "f64le", "s16le", "s24le", "s32le", "u8", "s8", "f32be", "s16be", "image2pipe",
        "concat", "gdigrab", "dshow", "ffmetadata", "matroska", "nut", "yuv4mpegpipe", "mjpeg",
    };

    private sealed record Option(string Name, int Token, int? ValueToken);

    private sealed class FileArg
    {
        public required int Index { get; init; }

        public required int PathToken { get; init; }

        public required List<Option> Options { get; init; }

        public MediaInfo? Info { get; set; }

        public string? Value(IReadOnlyList<string> tokens, string name) =>
            Options.LastOrDefault(o => o.Name == name) is { ValueToken: { } v } ? tokens[v] : null;

        public bool Has(string name) => Options.Any(o => o.Name == name);
    }

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Written), MediaInfo> Probed = new();

    /// <summary>Makes a command ready to run; with nothing for Windows to decode it comes back as it was.</summary>
    public static async Task<PreparedCommand> PrepareAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var tokens = arguments.ToList();
        var (inputs, outputs, graphs) = Parse(tokens);
        if (inputs.Count == 0)
            return new PreparedCommand(arguments, []);

        var decodable = await WindowsMedia.FfmpegDecodersAsync(cancellationToken).ConfigureAwait(false);
        var any = false;
        foreach (var input in inputs)
        {
            if (await InfoOfAsync(tokens, input, cancellationToken).ConfigureAwait(false) is not { } info)
                continue;
            input.Info = info;
            any |= info.Streams.Any(s => NeedsWindows(s, decodable));
        }
        if (!any)
            return new PreparedCommand(arguments, []);

        // Which streams of which inputs are decoded, and so must come through Windows.
        var wanted = new Dictionary<int, SortedSet<int>>();
        void Want(FileArg input, MediaStreamInfo stream)
        {
            if (!NeedsWindows(stream, decodable))
                return;
            if (!wanted.TryGetValue(input.Index, out var set))
                wanted[input.Index] = set = [];
            set.Add(stream.Index);
        }

        var edits = new List<(int Token, string[] Replacement)>();
        var mapsOf = new Dictionary<FileArg, List<(Option Map, List<(FileArg Input, MediaStreamInfo Stream)> Streams)>>();
        foreach (var output in outputs)
        {
            var maps = output.Options.Where(o => o.Name == "map" && o.ValueToken is not null).ToList();
            var resolved = new List<(Option, List<(FileArg, MediaStreamInfo)>)>();
            var counts = new Dictionary<MediaStreamKind, int>();
            if (maps.Count > 0)
            {
                foreach (var map in maps)
                {
                    var streams = Resolve(tokens[map.ValueToken!.Value], inputs, label: false);
                    foreach (var (input, stream) in streams)
                    {
                        var n = counts.GetValueOrDefault(stream.Kind);
                        counts[stream.Kind] = n + 1;
                        if (!IsCopy(tokens, output, stream.Kind, n))
                            Want(input, stream);
                    }
                    resolved.Add((map, streams));
                }
            }
            else if (graphs.Count == 0)
            {
                var auto = AutoSelected(tokens, output, inputs);
                var needs = false;
                foreach (var (input, stream) in auto)
                {
                    var n = counts.GetValueOrDefault(stream.Kind);
                    counts[stream.Kind] = n + 1;
                    if (!IsCopy(tokens, output, stream.Kind, n) && NeedsWindows(stream, decodable))
                    {
                        Want(input, stream);
                        needs = true;
                    }
                }
                // FFmpeg's own choice would take the stream from the file: the same choice, made explicit.
                if (needs)
                    resolved.Add((new Option("map", output.PathToken, null), auto));
            }
            mapsOf[output] = resolved;
        }
        foreach (var graph in graphs)
        {
            foreach (Match m in InputLabel().Matches(graph.Text))
            {
                foreach (var (input, stream) in Resolve(m.Value[1..^1], inputs, label: true))
                    Want(input, stream);
            }
        }
        if (wanted.Count == 0)
            return new PreparedCommand(arguments, []);

        // One bridge per input, its tracks in the order of the file's streams; bridge inputs go after the last input.
        var bridges = new List<WindowsBridge>();
        var bridgeOf = new Dictionary<int, (int Input, List<int> Streams)>();
        try
        {
            var inserted = new List<string>();
            foreach (var (index, streams) in wanted.OrderBy(w => w.Key))
            {
                var input = inputs[index];
                var info = input.Info!;
                var tracks = new List<BridgeTrack>();
                foreach (var stream in streams.Select(i => info.Streams.First(s => s.Index == i)))
                {
                    var nth = info.Streams.Where(o => o.Kind == stream.Kind).TakeWhile(o => o.Index != stream.Index).Count();
                    var lead = stream.Kind == MediaStreamKind.Audio ? await FirstPacketAsync(info, nth, cancellationToken).ConfigureAwait(false) : null;
                    var tenBit = stream.Kind == MediaStreamKind.Video && await MoreThan8BitsAsync(info, stream, nth, cancellationToken).ConfigureAwait(false);
                    var tellTale = await TellTaleAsync(info, stream, cancellationToken).ConfigureAwait(false);
                    tracks.Add(new BridgeTrack(stream, nth, lead, tenBit, tellTale));
                }
                var (start, duration) = Window(tokens, input, info);
                var bridge = await WindowsBridge.StartAsync(new BridgeRequest(info.Path, tracks, start, duration), cancellationToken).ConfigureAwait(false);
                bridges.Add(bridge);
                bridgeOf[index] = (inputs.Count + bridgeOf.Count, streams.ToList());
                foreach (var name in new[] { "itsoffset" })
                {
                    if (input.Value(tokens, name) is { } value)
                        inserted.AddRange(["-" + name, value]);
                }
                if (input.Has("noautorotate"))
                    inserted.Add("-noautorotate");
                inserted.AddRange(bridge.InputArgs);
            }
            edits.Add((inputs[^1].PathToken, [tokens[inputs[^1].PathToken], .. inserted]));

            string Ref(FileArg input, MediaStreamInfo stream) =>
                bridgeOf.TryGetValue(input.Index, out var b) && b.Streams.IndexOf(stream.Index) is >= 0 and var at
                    ? $"{b.Input}:{at}"
                    : $"{input.Index}:{stream.Index}";

            foreach (var output in outputs)
            {
                foreach (var (map, streams) in mapsOf[output])
                {
                    var bridged = streams.Any(s => bridgeOf.TryGetValue(s.Input.Index, out var b) && b.Streams.Contains(s.Stream.Index));
                    if (!bridged)
                        continue;
                    // A stream copied by this output still comes from the file.
                    var counts = new Dictionary<MediaStreamKind, int>();
                    var values = new List<string>();
                    foreach (var (input, stream) in streams)
                    {
                        var n = counts.GetValueOrDefault(stream.Kind);
                        counts[stream.Kind] = n + 1;
                        values.Add(IsCopy(tokens, output, stream.Kind, n) ? $"{input.Index}:{stream.Index}" : Ref(input, stream));
                    }
                    var replacement = values.SelectMany(v => new[] { "-map", v }).ToArray();
                    if (map.ValueToken is { } valueToken)
                        edits.Add((map.Token, replacement));
                    else
                        edits.Add((output.PathToken, [.. replacement, tokens[output.PathToken]]));
                }
                // A later "-map" of an auto choice replaces nothing: drop the pair it stood for.
            }
            foreach (var graph in graphs)
            {
                var text = InputLabel().Replace(graph.Text, m =>
                {
                    var streams = Resolve(m.Value[1..^1], inputs, label: true);
                    return streams.Count == 1 && bridgeOf.TryGetValue(streams[0].Input.Index, out var b) && b.Streams.Contains(streams[0].Stream.Index)
                        ? $"[{Ref(streams[0].Input, streams[0].Stream)}]"
                        : m.Value;
                });
                if (text == graph.Text)
                    continue;
                edits.Add((graph.ValueToken, [graph.FromFile ? FilterFile(text) : text]));
            }

            return new PreparedCommand(Apply(tokens, edits), bridges);
        }
        catch
        {
            foreach (var bridge in bridges)
                await bridge.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Where FFmpeg puts the first packet of the nth sound stream, from the start of the file's timeline.</summary>
    private static async Task<TimeSpan?> FirstPacketAsync(MediaInfo info, int nth, CancellationToken cancellationToken)
    {
        string[] args = ["-v", "error", "-select_streams", $"a:{nth}", "-show_entries", "packet=pts_time", "-read_intervals", "%+#1", "-of", "csv=p=0", info.Path];
        var result = await FfmpegProcess.RunAsync(Ffmpeg.ProbeExe, args, null, cancellationToken).ConfigureAwait(false);
        var first = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimEnd(',');
        return result.Succeeded && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds) - info.StartTime
            : null;
    }

    /// <summary>
    /// Whether a picture has more than 8 bits. FFprobe gives the pixel format only of what FFmpeg decodes; otherwise
    /// the profile says (H.264's "High 10"), or for HEVC, whose profile it leaves unknown, the stream's own header
    /// does: in an hvcC, the luma's bits less 8 are the low 3 bits of its 18th byte.
    /// </summary>
    internal static async Task<bool> MoreThan8BitsAsync(MediaInfo info, MediaStreamInfo stream, int nth, CancellationToken cancellationToken)
    {
        static bool Deep(string name) =>
            name.Contains("10", StringComparison.Ordinal) || name.Contains("12", StringComparison.Ordinal) || name.Contains("16", StringComparison.Ordinal);
        if (stream.PixelFormat is { Length: > 0 } format && format != "unknown")
            return Deep(format);
        if (stream.Profile is { Length: > 0 } profile && profile != "unknown")
            return Deep(profile) || profile.Contains("4:2:2", StringComparison.Ordinal) || profile.Contains("4:4:4", StringComparison.Ordinal) || profile == "Rext";
        if (stream.Codec != "hevc")
            return false;
        string[] args = ["-v", "error", "-select_streams", $"v:{nth}", "-show_entries", "stream=extradata", "-show_data", "-of", "json", info.Path];
        var result = await FfmpegProcess.RunAsync(Ffmpeg.ProbeExe, args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            return false;
        var header = HeaderBytes(result.StandardOutput, 19);
        return header.Length >= 19 && header[0] == 1 && (header[17] & 7) > 0;
    }

    /// <summary>
    /// Where a file has more than one stream of a kind: 32 bytes from the middle of one of the stream's first packets
    /// that no other stream of the kind holds, for telling which of Windows' streams it is. Null where the file has one
    /// stream of the kind, or where its streams begin alike (then either is as good).
    /// </summary>
    internal static async Task<byte[]?> TellTaleAsync(MediaInfo info, MediaStreamInfo stream, CancellationToken cancellationToken)
    {
        var others = info.Streams.Where(s => s.Kind == stream.Kind && s.Index != stream.Index && !s.IsAttachedPicture).ToList();
        if (others.Count == 0)
            return null;
        // A sound's first two seconds or so; a picture's first few frames, which are large.
        var count = stream.Kind == MediaStreamKind.Video ? 3 : 100;
        var mine = await PacketsAsync(info.Path, stream.Index, count, cancellationToken).ConfigureAwait(false);
        var theirs = new List<byte[]>();
        foreach (var other in others)
            theirs.AddRange(await PacketsAsync(info.Path, other.Index, count, cancellationToken).ConfigureAwait(false));
        foreach (var packet in mine.Where(p => p.Length >= 24))
        {
            // The middle of a packet: its start may be framed differently by Windows (start codes for lengths).
            var middle = packet.Length >= 48 ? packet[(packet.Length / 2 - 16)..(packet.Length / 2 + 16)] : packet[4..];
            if (!theirs.Any(t => t.AsSpan().IndexOf(middle) >= 0))
                return middle;
        }
        return null;
    }

    /// <summary>The first packets of a stream as the file holds them.</summary>
    private static async Task<List<byte[]>> PacketsAsync(string path, int index, int count, CancellationToken cancellationToken)
    {
        string[] args = ["-v", "error", "-select_streams", Inv.I(index), "-show_entries", "packet=data", "-show_data", "-read_intervals", $"%+#{count}", "-of", "json", path];
        var result = await FfmpegProcess.RunAsync(Ffmpeg.ProbeExe, args, null, cancellationToken).ConfigureAwait(false);
        var packets = new List<byte[]>();
        if (!result.Succeeded)
            return packets;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            if (json.RootElement.TryGetProperty("packets", out var list))
            {
                foreach (var packet in list.EnumerateArray())
                {
                    if (packet.TryGetProperty("data", out var data) && data.GetString() is { } dump)
                        packets.Add(HeaderBytes(dump, int.MaxValue));
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return packets;
    }

    /// <summary>The first bytes of the hex dump FFprobe writes for "-show_data" ("00000000: 0102 2000 ...  ascii").</summary>
    internal static byte[] HeaderBytes(string probeJson, int count)
    {
        var bytes = new List<byte>();
        foreach (Match line in DumpLine().Matches(probeJson))
        {
            var hex = line.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal);
            for (var i = 0; i + 1 < hex.Length && bytes.Count < count; i += 2)
                bytes.Add(Convert.ToByte(hex.Substring(i, 2), 16));
            if (bytes.Count >= count)
                break;
        }
        return [.. bytes];
    }

    [GeneratedRegex(@"[0-9a-f]{8}:((?: [0-9a-f]{2,4})+)")]
    private static partial Regex DumpLine();

    internal static bool NeedsWindows(MediaStreamInfo stream, IReadOnlySet<string> decodable) =>
        stream.Kind is MediaStreamKind.Video or MediaStreamKind.Audio && !stream.IsAttachedPicture && !WindowsMedia.CanDecode(stream.Codec, decodable);

    private sealed record Graph(int ValueToken, string Text, bool FromFile);

    private static (List<FileArg> Inputs, List<FileArg> Outputs, List<Graph> Graphs) Parse(List<string> tokens)
    {
        var inputs = new List<FileArg>();
        var outputs = new List<FileArg>();
        var graphs = new List<Graph>();
        var pending = new List<Option>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "-i" && i + 1 < tokens.Count)
            {
                inputs.Add(new FileArg { Index = inputs.Count, PathToken = i + 1, Options = pending });
                pending = [];
                i++;
                continue;
            }
            if (token.Length > 1 && token[0] == '-')
            {
                var name = token[1..];
                var bare = name.Split(':')[0];
                if (Flags.Contains(bare))
                {
                    pending.Add(new Option(bare, i, null));
                    continue;
                }
                if (i + 1 >= tokens.Count)
                    break;
                if (bare is "filter_complex" or "lavfi")
                    graphs.Add(new Graph(i + 1, tokens[i + 1], false));
                else if (bare is "/filter_complex" && File.Exists(tokens[i + 1]))
                    graphs.Add(new Graph(i + 1, File.ReadAllText(tokens[i + 1]), true));
                else
                    pending.Add(new Option(bare, i, i + 1));
                i++;
                continue;
            }
            outputs.Add(new FileArg { Index = outputs.Count, PathToken = i, Options = pending });
            pending = [];
        }
        return (inputs, outputs, graphs);
    }

    private static async Task<MediaInfo?> InfoOfAsync(List<string> tokens, FileArg input, CancellationToken cancellationToken)
    {
        var path = tokens[input.PathToken];
        if (path == "-" || path.StartsWith("pipe:", StringComparison.Ordinal) || path.StartsWith(@"\\.\pipe\", StringComparison.Ordinal) || path.Contains('%'))
            return null;
        if (input.Value(tokens, "f") is { } format && RawFormats.Contains(format))
            return null;
        if (input.Value(tokens, "f") is "image2")
            return null;
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
                return null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (Probed.TryGetValue(key, out var known))
            return known;
        MediaInfo info;
        try
        {
            info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaException)
        {
            return null;
        }
        if (Probed.Count > 256)
            Probed.Clear();
        Probed[key] = info;
        return info;
    }

    /// <summary>
    /// The streams a "-map" value or a filter graph's input label stands for: "0:3", "0:v", "1:a:0", "0:s?", "0".
    /// A filter label takes the first match only.
    /// </summary>
    private static List<(FileArg Input, MediaStreamInfo Stream)> Resolve(string value, List<FileArg> inputs, bool label)
    {
        var result = new List<(FileArg, MediaStreamInfo)>();
        var spec = value.TrimEnd('?');
        if (spec.Length == 0 || spec[0] is '-' or '[')
            return result;
        var parts = spec.Split(':');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= inputs.Count)
            return result;
        var input = inputs[index];
        if (input.Info is not { } info)
            return result;
        IEnumerable<MediaStreamInfo> streams = info.Streams;
        var rest = parts.Skip(1).ToArray();
        if (rest.Length > 0)
        {
            if (int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var absolute))
            {
                streams = streams.Where(s => s.Index == absolute);
            }
            else
            {
                MediaStreamKind? kind = rest[0] switch
                {
                    "v" or "V" => MediaStreamKind.Video,
                    "a" => MediaStreamKind.Audio,
                    "s" => MediaStreamKind.Subtitle,
                    "d" => MediaStreamKind.Data,
                    "t" => MediaStreamKind.Attachment,
                    _ => null,
                };
                if (kind is null)
                    return result;
                streams = streams.Where(s => s.Kind == kind && !(rest[0] == "V" && s.IsAttachedPicture));
                if (rest.Length > 1 && int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var nth))
                    streams = streams.Skip(nth).Take(1);
            }
        }
        if (label)
            streams = streams.Take(1);
        foreach (var stream in streams)
            result.Add((input, stream));
        return result;
    }

    /// <summary>
    /// What FFmpeg picks for an output with no "-map": the biggest picture, the sound with the most channels, and
    /// for Matroska, MP4 and MOV the first subtitle it can carry.
    /// </summary>
    private static List<(FileArg Input, MediaStreamInfo Stream)> AutoSelected(List<string> tokens, FileArg output, List<FileArg> inputs)
    {
        var all = inputs.Where(i => i.Info is not null).SelectMany(i => i.Info!.Streams.Select(s => (Input: i, Stream: s))).ToList();
        var result = new List<(FileArg, MediaStreamInfo)>();
        if (!output.Has("vn"))
        {
            var video = all.Where(x => x.Stream.Kind == MediaStreamKind.Video)
                .OrderByDescending(x => x.Stream.IsAttachedPicture ? 0 : 1).ThenByDescending(x => (long)x.Stream.Width * x.Stream.Height).ThenByDescending(x => x.Stream.IsDefault)
                .FirstOrDefault();
            if (video.Input is not null)
                result.Add(video);
        }
        if (!output.Has("an"))
        {
            var audio = all.Where(x => x.Stream.Kind == MediaStreamKind.Audio).OrderByDescending(x => x.Stream.Channels).ThenByDescending(x => x.Stream.IsDefault).FirstOrDefault();
            if (audio.Input is not null)
                result.Add(audio);
        }
        // FFmpeg picks a kind only for a format that carries it: no sound for a picture, no picture for sound.
        var format = output.Value(tokens, "f") ?? Path.GetExtension(tokens[output.PathToken]).TrimStart('.').ToLowerInvariant();
        if (format is "image2" or "image2pipe" or "png" or "jpg" or "jpeg" or "bmp" or "gif" or "apng" or "webp" or "rawvideo" or "yuv4mpegpipe" or "mjpeg" or "tif" or "tiff")
            result.RemoveAll(x => x.Item2.Kind == MediaStreamKind.Audio);
        if (format is "mp3" or "wav" or "flac" or "opus" or "aiff" or "adts" or "aac" or "ac3" or "f32le" or "s16le" or "s24le" or "s32le" or "w64" or "wv" or "mka" or "oga" or "ogg")
            result.RemoveAll(x => x.Item2.Kind == MediaStreamKind.Video);
        if (!output.Has("sn"))
        {
            var extension = Path.GetExtension(tokens[output.PathToken]).ToLowerInvariant();
            var subtitle = all.FirstOrDefault(x => x.Stream.Kind == MediaStreamKind.Subtitle &&
                (extension is ".mkv" || extension is ".mp4" or ".mov" or ".m4v" && x.Stream.Codec is "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text"));
            if (subtitle.Input is not null)
                result.Add(subtitle);
        }
        return result;
    }

    /// <summary>Whether an output copies the nth stream of a kind it carries, from its "-c", "-c:v", "-c:a:1", "-vcodec"…</summary>
    private static bool IsCopy(List<string> tokens, FileArg output, MediaStreamKind kind, int nth)
    {
        var letter = kind switch
        {
            MediaStreamKind.Video => "v",
            MediaStreamKind.Audio => "a",
            MediaStreamKind.Subtitle => "s",
            MediaStreamKind.Data => "d",
            _ => "t",
        };
        string? codec = null;
        foreach (var option in output.Options)
        {
            if (option.ValueToken is not { } v)
                continue;
            var name = tokens[option.Token][1..];
            var parts = name.Split(':');
            var applies = parts[0] switch
            {
                "c" or "codec" => parts.Length == 1 || parts[1] == letter && (parts.Length == 2 || parts[2] == Inv.I(nth)),
                "vcodec" => letter == "v",
                "acodec" => letter == "a",
                "scodec" => letter == "s",
                _ => false,
            };
            if (applies)
                codec = tokens[v];
        }
        return codec == "copy";
    }

    /// <summary>Where a bridge starts and for how long, from the input's "-ss", "-t", "-to" and "-sseof".</summary>
    private static (TimeSpan Start, TimeSpan? Duration) Window(List<string> tokens, FileArg input, MediaInfo info)
    {
        var start = input.Value(tokens, "ss") is { } ss ? Time(ss) : TimeSpan.Zero;
        if (input.Value(tokens, "sseof") is { } fromEnd)
            start = info.Duration + Time(fromEnd);
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        TimeSpan? duration = null;
        if (input.Value(tokens, "t") is { } t)
            duration = Time(t);
        else if (input.Value(tokens, "to") is { } to)
            duration = Time(to) - start;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        return (start, duration);
    }

    /// <summary>FFmpeg's time syntax: "[-][HH:]MM:SS[.m…]" or "[-]S[.m…]" with an optional "s", "ms" or "us".</summary>
    internal static TimeSpan Time(string text)
    {
        var value = text.Trim();
        var negative = value.StartsWith('-');
        if (negative)
            value = value[1..];
        double seconds;
        if (value.Contains(':'))
        {
            seconds = 0;
            foreach (var part in value.Split(':'))
                seconds = seconds * 60 + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        else if (value.EndsWith("ms", StringComparison.Ordinal))
            seconds = double.Parse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture) / 1000;
        else if (value.EndsWith("us", StringComparison.Ordinal))
            seconds = double.Parse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture) / 1_000_000;
        else
            seconds = double.Parse(value.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(negative ? -seconds : seconds);
    }

    private static List<string> Apply(List<string> tokens, List<(int Token, string[] Replacement)> edits)
    {
        // A "-map" edit replaces the option and its value; any other edit replaces one token.
        var result = new List<string>(tokens.Count + 16);
        var byToken = edits.GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.SelectMany(e => e.Replacement).ToArray());
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!byToken.TryGetValue(i, out var replacement))
            {
                result.Add(tokens[i]);
                continue;
            }
            result.AddRange(replacement);
            if (tokens[i] == "-map")
                i++;
        }
        return result;
    }

    private static string FilterFile(string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "filters");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "windows-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A filter graph's input label: "[0:v]", "[1:a:0]", "[0:3]".</summary>
    [GeneratedRegex(@"\[\d+(?::[^\]\[;,]*)?\]")]
    private static partial Regex InputLabel();
}
