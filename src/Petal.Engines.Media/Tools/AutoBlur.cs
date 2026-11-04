using System.Diagnostics;
using System.Globalization;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>Where something was at a moment, in shares of the frame.</summary>
public readonly record struct BlurKey(double Time, double X, double Y, double Width, double Height);

/// <summary>One face or plate followed through a video.</summary>
public sealed class BlurTrack
{
    public required BlurKind Kind { get; init; }

    /// <summary>Its places over time, in order.</summary>
    public required IReadOnlyList<BlurKey> Keys { get; init; }

    public double Start => Keys[0].Time;

    public double End => Keys[^1].Time;

    /// <summary>A small picture of it at its largest (BGR), to know it by.</summary>
    public byte[]? Picture { get; set; }

    public int PictureWidth { get; set; }

    public int PictureHeight { get; set; }

    /// <summary>The user wants this one left visible.</summary>
    public bool Keep { get; set; }

    /// <summary>Where it is at a moment, between sightings on a straight line; null outside its time.</summary>
    public BlurKey? At(double time, double margin)
    {
        if (time < Start - margin || time > End + margin)
            return null;
        if (time <= Start)
            return Keys[0];
        if (time >= End)
            return Keys[^1];
        int low = 0, high = Keys.Count - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (Keys[middle].Time <= time)
                low = middle;
            else
                high = middle;
        }
        var (a, b) = (Keys[low], Keys[high]);
        double share = (time - a.Time) / Math.Max(1e-6, b.Time - a.Time);
        return new BlurKey(time, a.X + (b.X - a.X) * share, a.Y + (b.Y - a.Y) * share, a.Width + (b.Width - a.Width) * share, a.Height + (b.Height - a.Height) * share);
    }
}

/// <summary>What to look for.</summary>
public sealed record AutoBlurOptions(bool Faces = true, bool Plates = true);

/// <summary>
/// Hides faces and number plates in a video by itself: a first pass looks a few times a second and follows each one
/// through the video, so it can be looked over and some kept; then every frame is written with what is left covered,
/// from a little before it is first seen to a little after it was last seen. What covers it comes from a handful of
/// averaged colours, so it can't be sharpened back.
/// </summary>
public static class AutoBlur
{
    /// <summary>How many times a second frames are looked at.</summary>
    internal const double LookRate = 6;

    /// <summary>The width frames are looked at; large enough for a face of about 1 % of the frame.</summary>
    private const int LookWidth = 1280;

    /// <summary>How long a thing may go unseen and still be the same thing.</summary>
    internal const double SameFor = 1.0;

    /// <summary>How long before and after its sightings a thing stays covered.</summary>
    public const double Margin = 0.35;

    public static IReadOnlyList<ModelInfo> ModelsFor(AutoBlurOptions options)
    {
        var models = new List<ModelInfo>();
        if (options.Faces)
            models.Add(ModelCatalog.FaceFinder);
        if (options.Plates)
            models.Add(ModelCatalog.PlateFinder);
        return models;
    }

    /// <summary>Sightings frame by frame become tracks: each joins the one it overlaps or is nearest, if that was seen lately.</summary>
    internal static List<BlurTrack> Follow(IReadOnlyList<(double Time, IReadOnlyList<BlurSighting> Found)> frames)
    {
        var open = new List<(List<BlurKey> Keys, BlurKind Kind, double Score, int Seen)>();
        foreach (var (time, found) in frames)
        {
            var taken = new HashSet<int>();
            foreach (var sighting in found.OrderByDescending(f => f.Area))
            {
                int best = -1;
                double bestScore = 0;
                for (int t = 0; t < open.Count; t++)
                {
                    if (taken.Contains(t) || open[t].Kind != sighting.Kind)
                        continue;
                    var last = open[t].Keys[^1];
                    if (time - last.Time > SameFor)
                        continue;
                    var before = new BlurSighting(sighting.Kind, last.X, last.Y, last.Width, last.Height, 0);
                    double overlap = before.Overlap(sighting);
                    double dx = last.X + last.Width / 2 - (sighting.X + sighting.Width / 2), dy = last.Y + last.Height / 2 - (sighting.Y + sighting.Height / 2);
                    double near = Math.Sqrt(dx * dx + dy * dy) / Math.Max(1e-6, Math.Max(last.Width, sighting.Width) * 1.5);
                    double score = overlap > 0 ? 1 + overlap : near < 1 ? 1 - near : 0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = t;
                    }
                }
                var key = new BlurKey(time, sighting.X, sighting.Y, sighting.Width, sighting.Height);
                if (best >= 0)
                {
                    var track = open[best];
                    track.Keys.Add(key);
                    open[best] = (track.Keys, track.Kind, Math.Max(track.Score, sighting.Score), track.Seen + 1);
                    taken.Add(best);
                }
                else
                {
                    open.Add(([key], sighting.Kind, sighting.Score, 1));
                    taken.Add(open.Count - 1);
                }
            }
        }
        // Seen once and not very sure is more likely a pattern on a wall than a face; better to err towards covering what is sure.
        return open.Where(t => t.Seen >= 2 || t.Score >= 0.8)
            .Select(t => new BlurTrack { Kind = t.Kind, Keys = t.Keys })
            .OrderBy(t => t.Start)
            .ToList();
    }

    /// <summary>Looks through a video and returns what is in it to hide.</summary>
    public static async Task<IReadOnlyList<BlurTrack>> FindAsync(string input, AutoBlurOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        return await Task.Run(() => Look(input, info, options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static List<BlurTrack> Look(string input, MediaInfo info, AutoBlurOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int width = Math.Min(LookWidth, Even(info.Width));
        int height = Even((int)Math.Round((double)width * info.Height / info.Width));
        using var detector = new AutoBlurDetector(options.Faces, options.Plates);
        var start = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-an", "-sn", "-dn",
                     "-vf", $"fps={Inv(LookRate)},scale={width}:{height}", "-pix_fmt", "bgr24", "-f", "rawvideo", "-" })
            start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new MediaException("FFmpeg didn't start.");
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        var frames = new List<(double, IReadOnlyList<BlurSighting>)>();
        var largest = new List<(BlurSighting Sighting, double Time, byte[] Picture)>();
        var frame = new byte[width * height * 3];
        double total = Math.Max(1, info.Duration.TotalSeconds * LookRate);
        try
        {
            using var stream = process.StandardOutput.BaseStream;
            while (ReadAll(stream, frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                double time = frames.Count / LookRate;
                var found = detector.Find(frame, width, height);
                frames.Add((time, found));
                foreach (var f in found)
                    largest.Add((f, time, Crop(frame, width, height, f)));
                // Pictures for long videos are thinned out over time, keeping the larger ones of each stretch, so the memory doesn't fill up.
                if (largest.Count > 1500)
                    largest = largest.GroupBy(l => (l.Sighting.Kind, Stretch: (int)(l.Time / 20)))
                        .SelectMany(g => g.OrderByDescending(l => l.Sighting.Area).Take(10)).ToList();
                progress?.Report(Math.Min(0.99, frames.Count / total));
            }
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (InvalidOperationException) { }
        }
        var tracks = Follow(frames);
        foreach (var track in tracks)
        {
            var best = largest.Where(l => l.Sighting.Kind == track.Kind && l.Time >= track.Start && l.Time <= track.End && Near(track, l.Time, l.Sighting))
                .OrderByDescending(l => l.Sighting.Area).FirstOrDefault();
            if (best.Picture is not null)
            {
                track.Picture = best.Picture;
                (track.PictureWidth, track.PictureHeight) = Size(best.Sighting, width, height);
            }
        }
        progress?.Report(1);
        return tracks;
    }

    private static bool Near(BlurTrack track, double time, in BlurSighting s)
    {
        if (track.At(time, 0) is not { } key)
            return false;
        return new BlurSighting(s.Kind, key.X, key.Y, key.Width, key.Height, 0).Overlap(s) > 0.2;
    }

    private static (int W, int H) Size(in BlurSighting s, int width, int height) =>
        (Math.Max(1, (int)(s.Width * width)), Math.Max(1, (int)(s.Height * height)));

    private static byte[] Crop(byte[] frame, int width, int height, in BlurSighting s)
    {
        var (w, h) = Size(s, width, height);
        int x0 = Math.Clamp((int)(s.X * width), 0, width - 1), y0 = Math.Clamp((int)(s.Y * height), 0, height - 1);
        var crop = new byte[w * h * 3];
        for (int y = 0; y < h && y0 + y < height; y++)
            Buffer.BlockCopy(frame, (y0 + y) * width * 3 + x0 * 3, crop, y * w * 3, Math.Min(w, width - x0) * 3);
        return crop;
    }

    private static int Even(int size) => Math.Max(2, size / 2 * 2);

    private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool ReadAll(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>A little beyond the box the model drew: hair and chin for a face, the frame for a plate.</summary>
    internal static (int X, int Y, int W, int H) Covered(in BlurKey key, BlurKind kind, int width, int height)
    {
        double grow = kind == BlurKind.Face ? 0.3 : 0.2;
        double x = key.X - key.Width * grow, y = key.Y - key.Height * grow * (kind == BlurKind.Face ? 1.2 : 1), w = key.Width * (1 + 2 * grow), h = key.Height * (1 + 2 * grow);
        int left = Math.Clamp((int)Math.Floor(x * width), 0, width), top = Math.Clamp((int)Math.Floor(y * height), 0, height);
        int right = Math.Clamp((int)Math.Ceiling((x + w) * width), 0, width), bottom = Math.Clamp((int)Math.Ceiling((y + h) * height), 0, height);
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Covers a part of a frame (BGR) so it can't be recovered: the part comes down to a few averaged colours, with smooth
    /// joins between them; a face gets an oval, so it doesn't look like a box moving about.
    /// </summary>
    internal static void Cover(byte[] frame, int width, int height, (int X, int Y, int W, int H) box, bool oval)
    {
        var (bx, by, bw, bh) = box;
        if (bw < 2 || bh < 2)
            return;
        const int Cells = 6;
        var cells = new double[Cells * Cells * 3];
        var counts = new int[Cells * Cells];
        for (int y = 0; y < bh; y++)
        {
            int cy = y * Cells / bh, row = (by + y) * width * 3 + bx * 3;
            for (int x = 0; x < bw; x++)
            {
                int cell = cy * Cells + x * Cells / bw, i = row + x * 3;
                cells[cell * 3] += frame[i];
                cells[cell * 3 + 1] += frame[i + 1];
                cells[cell * 3 + 2] += frame[i + 2];
                counts[cell]++;
            }
        }
        for (int c = 0; c < Cells * Cells; c++)
            for (int k = 0; k < 3; k++)
                cells[c * 3 + k] /= Math.Max(1, counts[c]);
        for (int y = 0; y < bh; y++)
        {
            double fy = Math.Clamp((y + 0.5) * Cells / bh - 0.5, 0, Cells - 1.001);
            int y0 = (int)fy;
            double ty = fy - y0;
            int row = (by + y) * width * 3 + bx * 3;
            for (int x = 0; x < bw; x++)
            {
                double share = 1;
                if (oval)
                {
                    double ex = (x + 0.5) / bw * 2 - 1, ey = (y + 0.5) / bh * 2 - 1, r = ex * ex + ey * ey;
                    if (r >= 1)
                        continue;
                    // Soft only in the last few percent, so nothing of the face shows through at the edge.
                    share = Math.Min(1, (1 - r) / 0.08);
                }
                double fx = Math.Clamp((x + 0.5) * Cells / bw - 0.5, 0, Cells - 1.001);
                int x0 = (int)fx;
                double tx = fx - x0;
                int a = (y0 * Cells + x0) * 3, b = a + 3, c = a + Cells * 3, d = c + 3, i = row + x * 3;
                for (int k = 0; k < 3; k++)
                {
                    double value = (cells[a + k] * (1 - tx) + cells[b + k] * tx) * (1 - ty) + (cells[c + k] * (1 - tx) + cells[d + k] * tx) * ty;
                    frame[i + k] = (byte)Math.Clamp(frame[i + k] * (1 - share) + value * share + 0.5, 0, 255);
                }
            }
        }
    }

    /// <summary>Writes the video with what wasn't kept covered in every frame; its sound and quality settings stay as they are for this kind of file.</summary>
    public static async Task<string> ApplyAsync(string input, string output, IReadOnlyList<BlurTrack> tracks, ConversionSettings? settings = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);
        var active = tracks.Where(t => !t.Keep).ToList();
        var plan = ToolSupport.Plan(input, info, target, settings ?? ConversionSettings.Default);
        await Task.Run(() => Render(input, output, info, plan, active, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static void Render(string input, string output, MediaInfo info, EncodePlan plan, List<BlurTrack> tracks, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = plan.Target;
        int width = Even(info.Width), height = Even(info.Height);
        double rate = info.FrameRate > 0 ? info.FrameRate : 30;
        var temporary = Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileNameWithoutExtension(output) + ".part" + Path.GetExtension(output));
        var read = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-an", "-sn", "-dn",
                     "-vf", $"fps={Inv(rate)},scale={width}:{height}", "-pix_fmt", "bgr24", "-f", "rawvideo", "-" })
            read.ArgumentList.Add(a);
        var write = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{width}x{height}", "-r", Inv(rate), "-i", "-",
            "-i", input, "-map", "0:v:0", "-map", "1:a?", "-map_metadata", "1" };
        args.AddRange(Containers.VideoEncoderArgs(target.Video, plan.Quality, hardware: false));
        args.AddRange(["-pix_fmt", "yuv420p"]);
        args.AddRange(ToolSupport.CopyAudioIfPossible(info, target) switch
        {
            StreamMode.Copy => ["-c:a", "copy"],
            StreamMode.Encode => Containers.AudioEncoderArgs(target.Audio, plan.AudioKbps, 16),
            _ => ["-an"],
        });
        if (target.FastStart)
            args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-shortest", "-f", target.Muxer, temporary]);
        foreach (var a in args)
            write.ArgumentList.Add(a);
        using var reader = Process.Start(read) ?? throw new MediaException("FFmpeg didn't start.");
        using var writer = Process.Start(write) ?? throw new MediaException("FFmpeg didn't start.");
        var writeErrors = writer.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = reader.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var frame = new byte[width * height * 3];
            var source = reader.StandardOutput.BaseStream;
            var sink = writer.StandardInput.BaseStream;
            long number = 0;
            double total = Math.Max(1, info.Duration.TotalSeconds * rate);
            while (ReadAll(source, frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                double time = number / rate;
                foreach (var track in tracks)
                    if (track.At(time, Margin) is { } key)
                        Cover(frame, width, height, Covered(key, track.Kind, width, height), track.Kind == BlurKind.Face);
                sink.Write(frame);
                number++;
                if (number % 8 == 0)
                    progress?.Report(Math.Min(0.99, number / total));
            }
            sink.Close();
            writer.WaitForExit();
            if (writer.ExitCode != 0)
                throw new MediaException("FFmpeg couldn't write the video: " + writeErrors.GetAwaiter().GetResult());
            if (number == 0)
                throw new MediaException("No frames could be read from this video.");
            File.Move(temporary, output, true);
            progress?.Report(1);
        }
        catch
        {
            foreach (var p in new[] { reader, writer })
                if (!p.HasExited)
                    try { p.Kill(); } catch (InvalidOperationException) { }
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        finally
        {
            if (!reader.HasExited)
                try { reader.Kill(); } catch (InvalidOperationException) { }
        }
    }
}
