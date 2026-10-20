using System.Globalization;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

public enum MetadataScope
{
    /// <summary>Container tags (title, artist, album, comment…).</summary>
    File,
    /// <summary>Technical facts about the file; read-only.</summary>
    Info,
    /// <summary>One stream (track); <see cref="MetadataGroup.Index"/> is the stream index.</summary>
    Stream,
    /// <summary>One chapter; <see cref="MetadataGroup.Index"/> is the chapter index.</summary>
    Chapter,
}

/// <summary>One "Key: value" row. Only editable rows are tags that can be changed or removed.</summary>
public sealed record MetadataEntry(string Key, string Value, bool Editable);

/// <param name="Index">Stream or chapter index for <see cref="MetadataScope.Stream"/>/<see cref="MetadataScope.Chapter"/>, otherwise −1.</param>
public sealed record MetadataGroup(string Name, MetadataScope Scope, int Index, IReadOnlyList<MetadataEntry> Entries);

/// <summary>Tag changes to write. A null value removes the tag. Keys are FFmpeg tag names ("title", "artist", "comment"…).</summary>
/// <param name="Streams">Per stream index (as in <see cref="MediaStreamInfo.Index"/>).</param>
/// <param name="Chapters">Per chapter index.</param>
public sealed record MetadataEdits(
    IReadOnlyDictionary<string, string?>? File = null,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, string?>>? Streams = null,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, string?>>? Chapters = null);

/// <summary>
/// Reads and writes audio/video tags with FFmpeg (streams are copied, never re-encoded).
/// Note that MP4/MOV/M4A only store well-known tag names; Matroska, FLAC and Ogg keep any key.
/// </summary>
public static class MediaMetadata
{
    /// <summary>All tags grouped as File, Info, each stream and each chapter.</summary>
    public static async Task<IReadOnlyList<MetadataGroup>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var groups = new List<MetadataGroup>
        {
            new("File", MetadataScope.File, -1, Tags(info.Tags)),
        };

        var facts = new List<MetadataEntry>
        {
            new("Format", info.ContainerLongName ?? info.Container, false),
            new("Duration", Clock(info.Duration), false),
            new("File Size", Inv.I(info.FileSize) + " bytes", false),
        };
        if (info.BitRate > 0)
            facts.Add(new("Bit Rate", $"{info.BitRate / 1000} kb/s", false));
        groups.Add(new("Info", MetadataScope.Info, -1, facts));

        var track = 1;
        foreach (var s in info.Streams)
        {
            var kind = s.IsAttachedPicture ? "Cover Art" : s.Kind.ToString();
            var entries = new List<MetadataEntry> { new("Codec", s.CodecLongName ?? s.Codec, false) };
            if (s.Kind == MediaStreamKind.Video)
            {
                entries.Add(new("Resolution", $"{s.DisplayWidth}×{s.DisplayHeight}", false));
                if (s.FrameRate > 0 && !s.IsAttachedPicture)
                    entries.Add(new("Frame Rate", s.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps", false));
                if (s.Rotation != 0)
                    entries.Add(new("Rotation", $"{s.Rotation}°", false));
            }
            else if (s.Kind == MediaStreamKind.Audio)
            {
                entries.Add(new("Channels", s.ChannelLayout is { Length: > 0 } layout ? $"{s.Channels} ({layout})" : Inv.I(s.Channels), false));
                entries.Add(new("Sample Rate", $"{s.SampleRate} Hz", false));
            }
            if (s.BitRate > 0)
                entries.Add(new("Bit Rate", $"{s.BitRate / 1000} kb/s", false));
            entries.AddRange(Tags(s.Tags));
            groups.Add(new($"Track {track++} · {kind}", MetadataScope.Stream, s.Index, entries));
        }

        foreach (var c in info.Chapters)
        {
            var entries = new List<MetadataEntry>
            {
                new("Start", Clock(c.Start), false),
                new("End", Clock(c.End), false),
            };
            entries.AddRange(Tags(c.Tags));
            groups.Add(new($"Chapter {c.Index + 1}", MetadataScope.Chapter, c.Index, entries));
        }
        return groups;
    }

    /// <summary>Writes a copy with the edited tags; everything else (streams, other tags, chapters) is kept.</summary>
    public static async Task<string> WriteAsync(string input, string output, MetadataEdits edits, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        var target = Containers.ForOutput(output, input);
        var (args, outputIndex) = CopyArgs(input, info, includeData: false);
        args.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);

        foreach (var (key, value) in edits.File ?? new Dictionary<string, string?>())
            args.AddRange(["-metadata", Tag(key, value)]);
        foreach (var (stream, tags) in edits.Streams ?? new Dictionary<int, IReadOnlyDictionary<string, string?>>())
        {
            if (!outputIndex.TryGetValue(stream, out var index))
                continue;
            foreach (var (key, value) in tags)
                args.AddRange([$"-metadata:s:{index}", Tag(key, value)]);
        }
        foreach (var (chapter, tags) in edits.Chapters ?? new Dictionary<int, IReadOnlyDictionary<string, string?>>())
        {
            if (chapter < 0 || chapter >= info.Chapters.Count)
                continue;
            foreach (var (key, value) in tags)
                args.AddRange([$"-metadata:c:{chapter}", Tag(key, value)]);
        }
        args.AddRange(Containers.MuxerArgs(target));

        await Ffmpeg.EncodeAsync(args, output, target.Muxer, info.DurationSeconds, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>
    /// Writes a copy without any tags, chapters, encoder stamps or data tracks (e.g. GPS tracks); audio/video
    /// streams and cover art are copied untouched, and rotation is kept.
    /// </summary>
    public static async Task<string> RemoveAllAsync(string input, string output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        var target = Containers.ForOutput(output, input);
        var (args, _) = CopyArgs(input, info, includeData: false);
        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact"]);
        if (target.FastStart)
            args.AddRange(["-movflags", "+faststart"]);
        if (target.Muxer == "mp3")
            args.AddRange(["-id3v2_version", info.CoverArt is null ? "0" : "3", "-write_xing", "1"]);

        await Ffmpeg.EncodeAsync(args, output, target.Muxer, info.DurationSeconds, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Maps every stream except data tracks and returns input → output stream index.</summary>
    private static (List<string> Args, Dictionary<int, int> OutputIndex) CopyArgs(string input, MediaInfo info, bool includeData)
    {
        var args = new List<string> { "-i", input };
        var map = new Dictionary<int, int>();
        foreach (var s in info.Streams)
        {
            if (s.Kind is MediaStreamKind.Unknown || (!includeData && s.Kind is MediaStreamKind.Data))
                continue;
            map[s.Index] = map.Count;
            args.AddRange(["-map", $"0:{s.Index}"]);
        }
        if (map.Count == 0)
            throw new MediaException($"\"{Path.GetFileName(input)}\" has no audio or video to keep.");
        args.AddRange(["-c", "copy"]);
        return (args, map);
    }

    private static string Tag(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('='))
            throw new ArgumentException($"'{key}' is not a valid tag name.", nameof(key));
        return $"{key.Trim()}={value ?? ""}";
    }

    private static List<MetadataEntry> Tags(IReadOnlyDictionary<string, string> tags) =>
        tags.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(t => new MetadataEntry(t.Key, t.Value, true)).ToList();

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss\.ff", CultureInfo.InvariantCulture);
}
