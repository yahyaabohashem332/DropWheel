using System.Text.Json;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>Reads what a media file contains with FFprobe.</summary>
public static class MediaProbe
{
    /// <summary>Probes a file. Throws <see cref="MediaException"/> when it is not a readable media file.</summary>
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file not found", path);

        string[] args = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path];
        var result = await FfmpegProcess.RunAsync(Ffmpeg.ProbeExe, args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new MediaException($"\"{Path.GetFileName(path)}\" is not a media file FFmpeg can read.", new ExternalToolException("ffprobe", result));

        try
        {
            return Parse(path, result.StandardOutput);
        }
        catch (JsonException e)
        {
            throw new MediaException($"Could not read the media information of \"{Path.GetFileName(path)}\".", e);
        }
    }

    internal static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streamArray.EnumerateArray())
                streams.Add(ParseStream(s));
        }

        var chapters = new List<MediaChapter>();
        if (root.TryGetProperty("chapters", out var chapterArray) && chapterArray.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var c in chapterArray.EnumerateArray())
            {
                chapters.Add(new MediaChapter(
                    n++,
                    Seconds(Str(c, "start_time")),
                    Seconds(Str(c, "end_time")),
                    Tags(c)));
            }
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        var hasFormat = format.ValueKind == JsonValueKind.Object;
        var duration = hasFormat ? Inv.ParseDouble(Str(format, "duration")) : 0;
        if (duration <= 0)
            duration = streams.Select(s => s.Duration?.TotalSeconds ?? 0).DefaultIfEmpty(0).Max();

        long size = hasFormat ? Inv.ParseLong(Str(format, "size")) : 0;
        if (size <= 0)
        {
            try { size = new FileInfo(path).Length; } catch (IOException) { }
        }

        var video = streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Video && !s.IsAttachedPicture);
        return new MediaInfo
        {
            Path = path,
            Container = hasFormat ? Str(format, "format_name") ?? "" : "",
            ContainerLongName = hasFormat ? Str(format, "format_long_name") : null,
            Duration = TimeSpan.FromSeconds(Math.Max(0, duration)),
            FileSize = size,
            BitRate = hasFormat ? Inv.ParseLong(Str(format, "bit_rate")) : 0,
            Video = video,
            Audio = streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Audio),
            CoverArt = streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Video && s.IsAttachedPicture),
            Streams = streams,
            Chapters = chapters,
            Tags = hasFormat ? Tags(format) : new Dictionary<string, string>(),
        };
    }

    private static MediaStreamInfo ParseStream(JsonElement s)
    {
        var kind = Str(s, "codec_type") switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            "subtitle" => MediaStreamKind.Subtitle,
            "data" => MediaStreamKind.Data,
            "attachment" => MediaStreamKind.Attachment,
            _ => MediaStreamKind.Unknown,
        };
        var tags = Tags(s);
        var disposition = s.TryGetProperty("disposition", out var d) ? d : default;

        var fps = Inv.ParseRational(Str(s, "avg_frame_rate"));
        if (fps <= 0 || fps > 1000)
            fps = Inv.ParseRational(Str(s, "r_frame_rate"));
        if (fps > 1000)
            fps = 0;

        var bits = (int)Inv.ParseLong(Str(s, "bits_per_raw_sample"));
        if (bits == 0)
            bits = Int(s, "bits_per_sample");

        var duration = Inv.ParseDouble(Str(s, "duration"), -1);
        tags.TryGetValue("language", out var language);

        return new MediaStreamInfo
        {
            Index = Int(s, "index"),
            Kind = kind,
            Codec = Str(s, "codec_name") ?? "none",
            CodecLongName = Str(s, "codec_long_name"),
            Profile = Str(s, "profile"),
            CodecTag = Str(s, "codec_tag_string"),
            Width = Int(s, "width"),
            Height = Int(s, "height"),
            Rotation = kind == MediaStreamKind.Video ? Rotation(s, tags) : 0,
            PixelFormat = Str(s, "pix_fmt"),
            FrameRate = kind == MediaStreamKind.Video ? fps : 0,
            Channels = Int(s, "channels"),
            ChannelLayout = Str(s, "channel_layout"),
            SampleRate = (int)Inv.ParseLong(Str(s, "sample_rate")),
            SampleFormat = Str(s, "sample_fmt"),
            BitsPerSample = bits,
            BitRate = Inv.ParseLong(Str(s, "bit_rate")),
            Duration = duration >= 0 ? TimeSpan.FromSeconds(duration) : null,
            IsAttachedPicture = Flag(disposition, "attached_pic"),
            IsDefault = Flag(disposition, "default"),
            Language = language,
            Tags = tags,
        };
    }

    /// <summary>Display-matrix rotation is counter-clockwise; the legacy "rotate" tag is clockwise.</summary>
    private static int Rotation(JsonElement s, Dictionary<string, string> tags)
    {
        if (s.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var ccw))
                    return Normalize(-ccw);
            }
        }
        if (tags.TryGetValue("rotate", out var rotate))
            return Normalize(Inv.ParseDouble(rotate));
        return 0;
    }

    private static int Normalize(double degrees)
    {
        var quarter = (int)Math.Round(degrees / 90.0);
        return ((quarter % 4) + 4) % 4 * 90;
    }

    private static Dictionary<string, string> Tags(JsonElement e)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject())
                tags[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        }
        return tags;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            if (v.ValueKind == JsonValueKind.String)
                return (int)Inv.ParseLong(v.GetString());
        }
        return 0;
    }

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;

    private static TimeSpan Seconds(string? text) => TimeSpan.FromSeconds(Math.Max(0, Inv.ParseDouble(text)));
}
