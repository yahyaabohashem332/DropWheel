using System.Diagnostics;
using System.Globalization;
using System.Text;
using Petal.Engines.Media.Internal;
using Petal.Engines.Media.Foundation;

namespace Petal.Engines.Media;

/// <summary>How a wide video becomes a tall one.</summary>
public enum ReframeMode
{
    /// <summary>A tall window that follows the person or what moves.</summary>
    Follow,
    /// <summary>A tall window in the middle.</summary>
    Centre,
    /// <summary>The whole picture, over a blurred, enlarged copy of itself.</summary>
    Blurred,
}

/// <param name="Width">The result's width; 1080 for 1080×1920.</param>
/// <param name="Height">The result's height.</param>
public sealed record ReframeOptions(ReframeMode Mode = ReframeMode.Follow, int Width = 1080, int Height = 1920) : MediaToolOptions;

/// <summary>
/// Turns a wide video into a tall one for Reels, Shorts and TikTok. "Follow" moves a 9:16 window across the
/// picture after the subject: found in frames along the video (by the background-removal model when it is
/// on this PC, otherwise by where the picture moves), smoothed so the camera glides and holds still while
/// the subject stays near the middle. The sound is kept as it is.
/// </summary>
public static class Reframer
{
    /// <summary>Where the subject is across a frame (0 left … 1 right), from a frame's PNG or JPEG bytes; null when it can't tell.</summary>
    public delegate Task<double?> SubjectFinder(byte[] frame, CancellationToken cancellationToken);

    public static async Task<string> ReframeAsync(string input, string output, ReframeOptions options, SubjectFinder? finder = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        int w = info.Width, h = info.Height;
        if (w <= 0 || h <= 0)
            throw new MediaException("The video's size can't be read.");
        int outW = Even(options.Width), outH = Even(options.Height);
        double tall = outW / (double)outH;
        string filter;
        if (options.Mode == ReframeMode.Blurred)
        {
            // gblur and hue, not boxblur and eq, which FFmpeg has only under the GPL: four passes of a 49-pixel box are a
            // Gaussian of sigma 28; hue's b of -0.6 darkens as eq's brightness of -0.06 does (15 of 255).
            filter = Invariant($"split[a][b];[a]scale={outW}:{outH}:force_original_aspect_ratio=increase,crop={outW}:{outH},gblur=sigma=28,hue=b=-0.6[bg];") +
                     Invariant($"[b]scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2");
            progress?.Report(0.05);
        }
        else
        {
            // The tall window: the full height, as wide as the shape allows (never wider than the picture).
            int cropW = Math.Min(w, Even((int)Math.Round(h * tall)));
            int cropH = cropW < w ? h : Math.Min(h, Even((int)Math.Round(w / tall)));
            string x;
            if (options.Mode == ReframeMode.Follow && cropW < w)
            {
                var samples = finder is not null
                    ? await FindSubjectAsync(input, info, finder, new Scaled(progress, 0, 0.35), cancellationToken).ConfigureAwait(false)
                    : await FindMotionAsync(input, info, new Scaled(progress, 0, 0.35), cancellationToken).ConfigureAwait(false);
                var path = Path(samples, cropW / (double)w);
                x = Expression(path, cropW, w);
            }
            else
            {
                x = Invariant($"{(w - cropW) / 2}");
            }
            filter = Invariant($"crop={cropW}:{cropH}:'{x}':{(h - cropH) / 2},scale={outW}:{outH}:flags=lanczos,setsar=1");
        }
        var target = Containers.ForOutput(output, input);
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = filter,
            Audio = ToolSupport.CopyAudioIfPossible(info, target),
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(new Scaled(progress, options.Mode == ReframeMode.Follow ? 0.35 : 0.05, 1)), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Where the subject is at times through the video, as told by <paramref name="finder"/>.</summary>
    private static async Task<List<(double T, double X)>> FindSubjectAsync(string input, MediaInfo info, SubjectFinder finder, IProgress<double>? progress, CancellationToken ct)
    {
        double length = info.Duration.TotalSeconds;
        int count = (int)Math.Clamp(Math.Round(length), 8, 90);
        var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds(length * (i + 0.5) / count)).ToList();
        var frames = await MediaPreview.ExtractFramesAsync(input, times, 512, ct).ConfigureAwait(false);
        var samples = new List<(double, double)>();
        for (int i = 0; i < frames.Count; i++)
        {
            if (await finder(frames[i], ct).ConfigureAwait(false) is { } x)
                samples.Add((times[i].TotalSeconds, Math.Clamp(x, 0, 1)));
            progress?.Report((i + 1.0) / frames.Count);
        }
        return samples.Count > 0 ? samples : await FindMotionAsync(input, info, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the picture moves, twice a second: the middle of what changed from the frame before, weighted
    /// toward the centre a little so a still scene stays put.
    /// </summary>
    internal static async Task<List<(double T, double X)>> FindMotionAsync(string input, MediaInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        const int W = 160;
        int h = Math.Max(2, (int)Math.Round(W * info.Height / (double)info.Width) / 2 * 2);
        var start = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-an", "-sn", "-vf", Invariant($"fps=2,scale={W}:{h},format=gray"), "-f", "rawvideo", "-" })
            start.ArgumentList.Add(a);
        await using var run = await FfmpegRun.StartAsync(start, ct).ConfigureAwait(false);
        var process = run.Process;
        _ = process.StandardError.ReadToEndAsync(ct);
        var stream = process.StandardOutput.BaseStream;
        var frame = new byte[W * h];
        byte[]? last = null;
        var samples = new List<(double, double)>();
        int index = 0;
        double length = Math.Max(0.5, info.Duration.TotalSeconds);
        try
        {
            while (await ReadFrameAsync(stream, frame, ct).ConfigureAwait(false))
            {
                if (last is not null && Centre(frame, last, W, h) is { } x)
                    samples.Add((index / 2.0, x));
                last ??= new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, last, 0, frame.Length);
                index++;
                progress?.Report(Math.Min(1, index / 2.0 / length));
            }
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await run.FinishAsync().ConfigureAwait(false);
        return samples;
    }

    private static async Task<bool> ReadFrameAsync(Stream stream, byte[] frame, CancellationToken ct)
    {
        int read = 0;
        while (read < frame.Length)
        {
            int n = await stream.ReadAsync(frame.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>The middle of the change between two grey frames (0 … 1 across), or null when hardly anything moved.</summary>
    internal static double? Centre(byte[] now, byte[] before, int w, int h)
    {
        double sum = 0, weighted = 0;
        for (int x = 0; x < w; x++)
        {
            double column = 0;
            for (int y = 0; y < h; y++)
            {
                int d = Math.Abs(now[y * w + x] - before[y * w + x]);
                if (d > 12)
                    column += d;
            }
            sum += column;
            weighted += column * (x + 0.5) / w;
        }
        // Less than a sliver of the picture changed: no opinion.
        if (sum < w * h * 0.5)
            return null;
        return weighted / sum;
    }

    /// <summary>
    /// The window's middle over time: samples steadied (each the middle of its neighbours), then a camera
    /// that only moves when the subject leaves the middle third of the window, gliding there.
    /// </summary>
    public static List<(double T, double X)> Path(IReadOnlyList<(double T, double X)> samples, double windowShare)
    {
        if (samples.Count == 0)
            return [(0, 0.5)];
        var steady = new List<(double T, double X)>();
        for (int i = 0; i < samples.Count; i++)
        {
            var near = samples.Skip(Math.Max(0, i - 2)).Take(5).Select(s => s.X).OrderBy(x => x).ToList();
            steady.Add((samples[i].T, near[near.Count / 2]));
        }
        double half = windowShare / 2;
        double deadZone = windowShare / 6;
        double camera = Math.Clamp(steady[0].X, half, 1 - half);
        var path = new List<(double T, double X)> { (0, camera) };
        foreach (var (t, x) in steady)
        {
            if (Math.Abs(x - camera) > deadZone)
                camera = Math.Clamp(camera + (x - camera) * 0.7, half, 1 - half);
            if (Math.Abs(path[^1].X - camera) > 1e-4)
                path.Add((t, camera));
        }
        return Thin(path, MostPoints);
    }

    /// <summary>More moves than this make FFmpeg's command too long; a long video keeps every n-th.</summary>
    internal const int MostPoints = 200;

    private static List<(double T, double X)> Thin(List<(double T, double X)> path, int most)
    {
        if (path.Count <= most)
            return path;
        double step = (path.Count - 1) / (double)(most - 1);
        return Enumerable.Range(0, most).Select(i => path[(int)Math.Round(i * step)]).ToList();
    }

    /// <summary>
    /// The window's left edge as an FFmpeg expression of the time t: straight lines between the path's
    /// points, eased over the last 0.8 seconds before each, and held after the last. The pieces are chosen
    /// by halving the time span, so the expression is only about log2(points) deep (FFmpeg stops near 100).
    /// </summary>
    public static string Expression(IReadOnlyList<(double T, double X)> path, int windowWidth, int pictureWidth)
    {
        string Left(double x) => Invariant($"{Math.Round(Math.Clamp(x * pictureWidth - windowWidth / 2.0, 0, pictureWidth - windowWidth), 1)}");
        if (path.Count == 1)
            return Left(path[0].X);
        // Piece i holds until path[i + 1].T; the last one holds to the end.
        var pieces = new List<string>(path.Count);
        for (int i = 1; i < path.Count; i++)
        {
            var (t0, x0) = path[i - 1];
            var (t1, x1) = path[i];
            double moveFrom = Math.Max(t0, t1 - 0.8);
            string a = Left(x0), b = Left(x1);
            pieces.Add(a == b || t1 - moveFrom < 0.05
                ? a
                : Invariant($"if(lt(t,{moveFrom:0.###}),{a},{a}+({b}-{a})*(t-{moveFrom:0.###})/{t1 - moveFrom:0.###})"));
        }
        pieces.Add(Left(path[^1].X));
        string Build(int from, int to)
        {
            if (from == to)
                return pieces[from];
            int middle = (from + to) / 2;
            return Invariant($"if(lt(t,{path[middle + 1].T:0.###}),{Build(from, middle)},{Build(middle + 1, to)})");
        }
        return Build(0, pieces.Count - 1);
    }

    private static int Even(int n) => Math.Max(2, n / 2 * 2);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed class Scaled(IProgress<double>? inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner?.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
