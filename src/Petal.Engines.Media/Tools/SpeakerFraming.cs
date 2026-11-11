using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Petal.Engines.Media.Internal;
using Petal.Engines.Media.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Petal.Engines.Media;

/// <summary>A face seen in a frame: its box as shares of the frame (0 … 1), and how much its mouth moved since the frame before.</summary>
internal readonly record struct SeenFace(double X, double Y, double Width, double Height, double Mouth)
{
    public double CentreX => X + Width / 2;

    public double CentreY => Y + Height / 2;
}

/// <summary>
/// Where the camera of a tall (or square) clip looks: the middle of the window across the picture over time (shares of
/// the width, in the clip's own seconds), whether it cuts or glides to each place, how far in it goes, and how high.
/// </summary>
internal sealed record CameraPath(IReadOnlyList<CameraKey> Keys, double Zoom, double CentreY);

/// <summary>The camera is at <paramref name="X"/> at <paramref name="T"/>, having cut there or glided from where it was.</summary>
internal readonly record struct CameraKey(double T, double X, bool Cut);

/// <summary>
/// Frames a clip on the person speaking, as an editor would for Reels, Shorts and TikTok: faces are found by Windows'
/// own face detector a few times a second, followed from frame to frame, and the one whose mouth moves (who is talking)
/// is kept in the window. The camera holds still while the speaker stays near its middle and glides when they move
/// away; when someone else starts talking it cuts to them, as a vision mixer would. With no face clear and steady
/// enough (a screen, slides, a wide hall), there is no path: the whole picture is shown instead.
/// </summary>
internal static class SpeakerFraming
{
    /// <summary>Frames looked at per second.</summary>
    internal const double Rate = 4;

    /// <summary>The width frames are read at; a face of 3% of the picture's height is still found.</summary>
    private const int ReadWidth = 640;

    /// <summary>Finds who speaks through a stretch of the video and the camera path that follows them; null when no face is clear.</summary>
    public static async Task<CameraPath?> FollowAsync(string video, MediaInfo info, double start, double end, double windowShare, double maxZoom, CancellationToken ct)
    {
        if (!FaceDetector.IsSupported || info.Width <= 0 || info.Height <= 0)
            return null;
        var frames = await FacesAsync(video, info, start, end, ct).ConfigureAwait(false);
        return Plan(frames, windowShare, maxZoom);
    }

    /// <summary>The faces in each frame read, <see cref="Rate"/> a second from the start of the stretch.</summary>
    internal static async Task<List<List<SeenFace>>> FacesAsync(string video, MediaInfo info, double start, double end, CancellationToken ct)
    {
        int w = ReadWidth, h = Math.Max(2, (int)Math.Round(ReadWidth * info.Height / (double)info.Width / 2) * 2);
        var detector = await FaceDetector.CreateAsync().AsTask(ct).ConfigureAwait(false);
        // Smaller faces than this are crowds and posters, not a speaker.
        uint smallest = (uint)Math.Max(12, h * 0.035);
        detector.MinDetectableFaceSize = new BitmapSize { Width = smallest, Height = smallest };
        var startInfo = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Inv(start), "-t", Inv(Math.Max(0.1, end - start)), "-i", video,
                     "-an", "-sn", "-dn", "-vf", $"fps={Inv(Rate)},scale={w}:{h},format=gray", "-f", "rawvideo", "-" })
            startInfo.ArgumentList.Add(a);
        await using var run = await FfmpegRun.StartAsync(startInfo, ct).ConfigureAwait(false);
        var process = run.Process;
        _ = process.StandardError.ReadToEndAsync(ct);
        var stream = process.StandardOutput.BaseStream;
        var frame = new byte[w * h];
        byte[]? before = null;
        var all = new List<List<SeenFace>>();
        try
        {
            while (await ReadAsync(stream, frame, ct).ConfigureAwait(false))
            {
                using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, w, h);
                bitmap.CopyFromBuffer(frame.AsBuffer());
                var found = await detector.DetectFacesAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                var faces = new List<SeenFace>(found.Count);
                foreach (var face in found)
                {
                    var box = face.FaceBox;
                    double mouth = before is null ? 0 : MouthMovement(frame, before, w, h, (int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
                    faces.Add(new SeenFace(box.X / (double)w, box.Y / (double)h, box.Width / (double)w, box.Height / (double)h, mouth));
                }
                all.Add(faces);
                before ??= new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, before, 0, frame.Length);
            }
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await run.FinishAsync().ConfigureAwait(false);
        return all;
    }

    private static async Task<bool> ReadAsync(Stream stream, byte[] frame, CancellationToken ct)
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

    /// <summary>
    /// How much a face's mouth moved between two frames: the change in the lower part of its box beyond the change in
    /// its upper part (a head that moves changes both; a mouth that talks changes the lower one).
    /// </summary>
    internal static double MouthMovement(byte[] now, byte[] before, int w, int h, int x, int y, int bw, int bh)
    {
        double Change(int top, int bottom)
        {
            int left = Math.Clamp(x + bw / 5, 0, w - 1), right = Math.Clamp(x + bw * 4 / 5, left + 1, w);
            top = Math.Clamp(top, 0, h - 1);
            bottom = Math.Clamp(bottom, top + 1, h);
            double sum = 0;
            int n = 0;
            for (int row = top; row < bottom; row++)
                for (int col = left; col < right; col++)
                {
                    sum += Math.Abs(now[row * w + col] - before[row * w + col]);
                    n++;
                }
            return n == 0 ? 0 : sum / n;
        }
        double mouth = Change(y + bh * 6 / 10, y + bh * 11 / 10);
        double eyes = Change(y + bh / 10, y + bh * 5 / 10);
        return Math.Max(0, mouth - eyes);
    }

    /// <summary>A face followed from frame to frame: where it is in each frame it was seen in, by frame number.</summary>
    internal sealed class Track
    {
        public readonly SortedDictionary<int, SeenFace> Seen = new();

        public SeenFace Last => Seen.Values.Last();

        public int LastFrame => Seen.Keys.Last();

        /// <summary>How much the mouth moved around a frame (about a second either side).</summary>
        public double Talking(int frame)
        {
            double sum = 0;
            int n = 0;
            for (int k = frame - 4; k <= frame + 4; k++)
                if (Seen.TryGetValue(k, out var face))
                {
                    sum += face.Mouth / Math.Max(0.02, face.Height);
                    n++;
                }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>Where the face is at a frame: as seen, or as last seen within a second.</summary>
        public SeenFace? At(int frame)
        {
            if (Seen.TryGetValue(frame, out var face))
                return face;
            for (int k = 1; k <= 4; k++)
            {
                if (Seen.TryGetValue(frame - k, out face))
                    return face;
                if (Seen.TryGetValue(frame + k, out face))
                    return face;
            }
            return null;
        }
    }

    /// <summary>Faces joined into tracks: a face belongs to the track whose last place is nearest, within about its own width.</summary>
    internal static List<Track> Tracks(IReadOnlyList<List<SeenFace>> frames)
    {
        var tracks = new List<Track>();
        for (int f = 0; f < frames.Count; f++)
        {
            var taken = new HashSet<Track>();
            foreach (var face in frames[f].OrderByDescending(x => x.Height))
            {
                Track? best = null;
                double bestDistance = double.MaxValue;
                foreach (var track in tracks)
                {
                    if (taken.Contains(track) || f - track.LastFrame > 8)
                        continue;
                    var last = track.Last;
                    double distance = Math.Abs(last.CentreX - face.CentreX) + Math.Abs(last.CentreY - face.CentreY) * 0.5;
                    double size = Math.Abs(last.Height - face.Height) / Math.Max(last.Height, face.Height);
                    if (distance < Math.Max(last.Width, face.Width) * 1.2 && size < 0.5 && distance < bestDistance)
                    {
                        best = track;
                        bestDistance = distance;
                    }
                }
                if (best is null)
                {
                    best = new Track();
                    tracks.Add(best);
                }
                best.Seen[f] = face;
                taken.Add(best);
            }
        }
        return tracks;
    }

    /// <summary>
    /// The camera path for the faces seen: who speaks at each frame (one face: that one; several: whose mouth moves
    /// most, held until another talks clearly more for most of a second), the camera on them, still while they stay near
    /// the middle of the window, gliding when they leave it, cutting when the speaker changes. Null when no face is seen
    /// in a quarter of the frames.
    /// </summary>
    internal static CameraPath? Plan(IReadOnlyList<List<SeenFace>> frames, double windowShare, double maxZoom)
    {
        if (frames.Count == 0)
            return null;
        // Faces seen only now and then (a poster, a passer-by) don't count.
        var tracks = Tracks(frames).Where(t => t.Seen.Count >= Math.Max(2, frames.Count / 10)).ToList();
        if (tracks.Count == 0 || tracks.Sum(t => t.Seen.Count) < frames.Count / 4)
            return null;

        // Who speaks, frame by frame.
        var speaker = new Track?[frames.Count];
        Track? current = null;
        int challenger = 0;
        Track? rival = null;
        for (int f = 0; f < frames.Count; f++)
        {
            var present = tracks.Where(t => t.At(f) is not null).ToList();
            if (present.Count == 0)
            {
                speaker[f] = current;
                continue;
            }
            if (current is null || !present.Contains(current))
            {
                current = present.OrderByDescending(t => t.Talking(f)).ThenByDescending(t => t.At(f)!.Value.Height).First();
                challenger = 0;
            }
            else if (present.Count > 1)
            {
                var other = present.Where(t => t != current).OrderByDescending(t => t.Talking(f)).First();
                if (other.Talking(f) > current.Talking(f) * 1.35 + 0.02)
                {
                    challenger = other == rival ? challenger + 1 : 1;
                    rival = other;
                    if (challenger >= 3)
                    {
                        current = other;
                        challenger = 0;
                    }
                }
                else
                    challenger = 0;
            }
            speaker[f] = current;
        }

        // How far in: the speaker's face about a seventh of the picture's height, no further than the picture's sharpness allows.
        var heights = new List<double>();
        var middles = new List<double>();
        for (int f = 0; f < frames.Count; f++)
            if (speaker[f]?.At(f) is { } seen)
            {
                heights.Add(seen.Height);
                middles.Add(seen.CentreY);
            }
        if (heights.Count == 0)
            return null;
        heights.Sort();
        middles.Sort();
        double zoom = Math.Clamp(0.14 / Math.Max(0.01, heights[heights.Count / 2]), 1, Math.Max(1, maxZoom));

        // The camera: on the speaker, still within a dead zone, gliding beyond it, cutting to a new speaker.
        double window = windowShare / zoom;
        // Still while the face stays within the middle three tenths of the window: near the middle, without chasing every nod.
        double half = window / 2, deadZone = window * 0.15;
        var keys = new List<CameraKey>();
        double camera = -1;
        Track? framed = null;
        for (int f = 0; f < frames.Count; f++)
        {
            if (speaker[f] is not { } who || who.At(f) is not { } face)
                continue;
            // Steadied: the middle of where the face was over three frames.
            var near = Enumerable.Range(f - 1, 3).Select(k => who.At(k)).Where(x => x is not null).Select(x => x!.Value.CentreX).OrderBy(x => x).ToList();
            double target = Math.Clamp(near[near.Count / 2], half, 1 - half);
            double t = f / Rate;
            if (camera < 0)
            {
                camera = target;
                framed = who;
                keys.Add(new CameraKey(0, camera, true));
            }
            else if (who != framed)
            {
                framed = who;
                bool far = Math.Abs(target - camera) > window * 0.35;
                camera = target;
                keys.Add(new CameraKey(t, camera, far));
            }
            else if (Math.Abs(target - camera) > deadZone)
            {
                camera = target;
                keys.Add(new CameraKey(t + 0.6, camera, false));
            }
        }
        return keys.Count == 0 ? null : new CameraPath(keys, zoom, middles[middles.Count / 2]);
    }

    /// <summary>
    /// The window's left edge, in pixels of the picture, as an FFmpeg expression of the output time t: still between
    /// keys, gliding (eased) into a glided-to key over up to 0.8 seconds, jumping at a cut. <paramref name="toOutput"/>
    /// moves the clip's times to the output's (after pauses are cut). The pieces are chosen by halving, so the expression
    /// stays shallow however many keys there are.
    /// </summary>
    internal static string Expression(IReadOnlyList<CameraKey> keys, int windowWidth, int pictureWidth, Func<double, double> toOutput)
    {
        string Left(double x) => Inv(Math.Round(Math.Clamp(x * pictureWidth - windowWidth / 2.0, 0, pictureWidth - windowWidth), 1));
        if (keys.Count == 0)
            return Left(0.5);
        // Pieces: from each key's time (output) on, until the next key's.
        var pieces = new List<(double From, string Value)>();
        var times = keys.Select(k => toOutput(k.T)).ToList();
        pieces.Add((0, Left(keys[0].X)));
        for (int i = 1; i < keys.Count; i++)
        {
            double at = times[i];
            string from = Left(keys[i - 1].X), to = Left(keys[i].X);
            if (from == to)
                continue;
            if (keys[i].Cut)
            {
                pieces.Add((at, to));
                continue;
            }
            double glide = Math.Min(0.8, Math.Max(0.15, at - Math.Max(times[i - 1], pieces[^1].From)));
            double begin = at - glide;
            // Eased: slow out, slow in (3u² − 2u³).
            var u = $"((t-{Inv(begin)})/{Inv(glide)})";
            pieces.Add((begin, $"{from}+({to}-{from})*(3*{u}*{u}-2*{u}*{u}*{u})"));
            pieces.Add((at, to));
        }
        // Times out of order (a glide begun before the piece it follows) are folded into the one before.
        var ordered = new List<(double From, string Value)>();
        foreach (var piece in pieces)
        {
            while (ordered.Count > 0 && piece.From <= ordered[^1].From)
                ordered.RemoveAt(ordered.Count - 1);
            ordered.Add(piece);
        }
        string Build(int from, int to)
        {
            if (from == to)
                return ordered[from].Value;
            int middle = (from + to) / 2;
            return $"if(lt(t,{Inv(ordered[middle + 1].From)}),{Build(from, middle)},{Build(middle + 1, to)})";
        }
        return Build(0, ordered.Count - 1);
    }

    private static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
