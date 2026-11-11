using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Petal.Core.Recording;
using Petal.Core.Util;
using SkiaSharp;

namespace Petal.App.Screen.Studio;

/// <summary>
/// Makes the finished video of a studio project. First the sound: the kept stretches of each track (sped-up ones
/// faster without going higher), the microphone cleaned of hum and hiss, the tracks mixed and brought to the
/// loudness video apps play at. Then the picture: every frame of the finished video drawn from the recording's
/// frame at that moment, at the size and smoothness chosen, and both put together in an MP4.
/// </summary>
internal static class StudioExport
{
    public static async Task RenderAsync(StudioProject project, string output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("FFmpeg was not found. Install FFmpeg or place ffmpeg.exe in DropWheel's tools folder.");
        var edit = project.Edit;
        var timeline = new EditTimeline(project.Duration, edit);
        if (timeline.Length < 0.1)
            throw new InvalidOperationException("Nothing is left of the recording after the cuts.");
        var work = Path.Combine(Path.GetTempPath(), "DropWheel studio " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var sound = await SoundAsync(ffmpeg, project, timeline, Path.Combine(work, "sound.wav"), cancellationToken);
            progress?.Report(0.03);
            var temp = Path.Combine(work, "video.mp4");
            await PictureAsync(ffmpeg, project, timeline, sound, temp, new Progress<double>(p => progress?.Report(0.03 + 0.97 * p)), cancellationToken);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------- the sound ----------

    /// <summary>The finished sound as a WAV, or null when the recording has none.</summary>
    internal static async Task<string?> SoundAsync(string ffmpeg, StudioProject project, EditTimeline timeline, string output, CancellationToken cancellationToken)
    {
        var look = project.Edit.Look;
        // The tracks apart when the recording kept them, else the video's own sound.
        var inputs = new List<(string File, bool Voice)>();
        if (project.SystemSound is { } system && File.Exists(project.PathOf(system)))
            inputs.Add((project.PathOf(system), false));
        if (project.Microphone is { } mic && File.Exists(project.PathOf(mic)))
            inputs.Add((project.PathOf(mic), true));
        if (inputs.Count == 0)
        {
            var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(project.PathOf(project.Video), cancellationToken);
            if (!info.HasAudio)
                return null;
            inputs.Add((project.PathOf(project.Video), false));
        }
        File.WriteAllText(output + ".graph", Graph(inputs.Select(i => i.Voice).ToList(), timeline.Pieces, look), new UTF8Encoding(false));
        var args = new List<string> { "-v", "error" };
        foreach (var (file, _) in inputs)
            args.AddRange(["-i", file]);
        args.AddRange(["-/filter_complex", output + ".graph", "-map", "[out]", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-y", output]);
        var (code, errors) = await RunAsync(ffmpeg, args, cancellationToken);
        if (code != 0 || !File.Exists(output))
        {
            Log.Info($"The studio's sound failed: {errors.Trim()}");
            throw new InvalidOperationException("The sound couldn't be made.");
        }
        return output;
    }

    /// <summary>The filter graph: each track cut to the kept pieces and joined, the voice cleaned, all mixed and evened out.</summary>
    internal static string Graph(IReadOnlyList<bool> voices, IReadOnlyList<EditTimeline.Piece> pieces, StudioLook look)
    {
        var g = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        for (int i = 0; i < voices.Count; i++)
        {
            g.Append(c, $"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");
            if (pieces.Count > 1)
            {
                g.Append(c, $",asplit={pieces.Count}");
                for (int k = 0; k < pieces.Count; k++)
                    g.Append(c, $"[s{i}_{k}]");
                g.Append(";\n");
            }
            else
            {
                g.Append(c, $"[s{i}_0];\n");
            }
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                g.Append(c, $"[s{i}_{k}]atrim=start={p.Start:0.######}:end={p.End:0.######},asetpts=PTS-STARTPTS{Tempo(p.Speed)}[p{i}_{k}];\n");
            }
            for (int k = 0; k < pieces.Count; k++)
                g.Append(c, $"[p{i}_{k}]");
            g.Append(c, $"concat=n={pieces.Count}:v=0:a=1");
            if (voices[i] && look.CleanVoice)
                g.Append(",highpass=f=80,afftdn=nr=12:nf=-40:tn=1");
            g.Append(c, $"[t{i}];\n");
        }
        for (int i = 0; i < voices.Count; i++)
            g.Append(c, $"[t{i}]");
        if (voices.Count > 1)
            g.Append(c, $"amix=inputs={voices.Count}:duration=longest:normalize=0,");
        g.Append(look.EvenVolume ? "loudnorm=I=-16:TP=-1.5:LRA=11" : "alimiter=limit=0.95");
        g.Append("[out]");
        return g.ToString();
    }

    /// <summary>atempo goes up to twice as fast at a time: faster takes several.</summary>
    private static string Tempo(double speed)
    {
        if (Math.Abs(speed - 1) < 1e-6)
            return "";
        var sb = new StringBuilder();
        double left = speed;
        while (left > 2 + 1e-9)
        {
            sb.Append(",atempo=2");
            left /= 2;
        }
        sb.Append(CultureInfo.InvariantCulture, $",atempo={left:0.######}");
        return sb.ToString();
    }

    // ---------- the picture ----------

    private static async Task PictureAsync(string ffmpeg, StudioProject project, EditTimeline timeline, string? sound, string output, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var edit = project.Edit;
        var (width, height) = StudioLayout.OutputSize(project.Width, project.Height, edit.Look, edit.Output);
        int fps = Math.Clamp(edit.Output.Fps, 15, 60);
        string sourceFps = project.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        // The recording and the camera are H.264, which Windows decodes for FFmpeg.
        await using var decodeRun = await StartAsync(ffmpeg, ["-v", "error", "-i", project.PathOf(project.Video), "-vf", $"fps={sourceFps},scale={project.Width}:{project.Height}", "-f", "rawvideo", "-pix_fmt", "bgra", "-"], output: true, cancellationToken: cancellationToken);
        var decode = decodeRun.Process;
        Petal.Engines.Media.Foundation.FfmpegRun? facesRun = null;
        if (project.Camera is { } camera && File.Exists(project.PathOf(camera)) && project.CameraSide > 0 && project.CameraPlaces.Count > 0)
            facesRun = await StartAsync(ffmpeg, ["-v", "error", "-i", project.PathOf(camera), "-vf", $"fps={sourceFps}", "-s", $"{project.CameraSide}x{project.CameraSide}", "-f", "rawvideo", "-pix_fmt", "bgra", "-"], output: true, cancellationToken: cancellationToken);
        await using var facesOwner = facesRun;
        var faces = facesRun?.Process;
        var args = new List<string>
        {
            "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", "-",
        };
        if (sound is not null)
            args.AddRange(["-i", sound]);
        args.AddRange(["-map", "0:v"]);
        if (sound is not null)
            args.AddRange(["-map", "1:a", .. Petal.Engines.Media.MediaEncoders.Aac(192)]);
        args.AddRange([.. Petal.Engines.Media.MediaEncoders.H264(17, hardware: false), "-movflags", "+faststart", "-y", output]);
        await using var encodeRun = await StartAsync(ffmpeg, args, input: true, cancellationToken: cancellationToken);
        var encode = encodeRun.Process;
        var errors = new StringBuilder();
        foreach (var p in new[] { decode, encode, faces })
            if (p is not null)
                _ = p.StandardError.ReadToEndAsync(cancellationToken).ContinueWith(t => { if (t.IsCompletedSuccessfully) lock (errors) errors.Append(t.Result); }, TaskScheduler.Default);
        try
        {
            await Task.Run(() => Draw(project, timeline, width, height, fps, decode, faces, encode, progress, cancellationToken), cancellationToken);
            await encode.WaitForExitAsync(cancellationToken);
            await decode.WaitForExitAsync(cancellationToken);
            await decodeRun.FinishAsync();
            if (encode.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                Log.Info($"The studio's video failed: {errors.ToString().Trim()}");
                throw new InvalidOperationException("The video couldn't be made.");
            }
        }
        finally
        {
            foreach (var p in new[] { decode, encode, faces })
            {
                try { if (p is { HasExited: false }) p.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }

    private static void Draw(StudioProject project, EditTimeline timeline, int width, int height, int fps, Process decode, Process? faces, Process encode,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var art = new PointerArt(project);
        using var wallpaper = project.Edit.Look.Background == "wallpaper" && StudioBackgrounds.WallpaperPath() is { } w ? SKImage.FromEncodedData(w) : null;
        using var renderer = new StudioRenderer(project, width, height, art, wallpaper);
        int frameBytes = project.Width * project.Height * 4;
        var frame = new byte[frameBytes];
        var screenInfo = new SKImageInfo(project.Width, project.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameHandle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        int side = Math.Max(1, project.CameraSide);
        var face = new byte[side * side * 4];
        var faceHandle = GCHandle.Alloc(face, GCHandleType.Pinned);
        var source = decode.StandardOutput.BaseStream;
        var sink = encode.StandardInput.BaseStream;
        try
        {
            using var screenPixels = new SKPixmap(screenInfo, frameHandle.AddrOfPinnedObject(), project.Width * 4);
            using var facePixmap = new SKPixmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Opaque), faceHandle.AddrOfPinnedObject(), side * 4);
            using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(target);
            int have = -1, haveFace = -1;
            bool ended = false, facesEnded = faces is null;
            int total = Math.Max(1, (int)Math.Ceiling(timeline.Length * fps));
            for (int k = 0; k < total; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double output = k / (double)fps;
                double time = timeline.ToSource(output);
                int want = Math.Max(0, (int)Math.Floor(time * project.Fps + 1e-6));
                // The recording's frames are read in order; those cut out or skipped by a speed-up are passed over.
                while (!ended && have < want)
                {
                    if (ReadFull(source, frame))
                    {
                        have++;
                        for (int i = 3; i < frameBytes; i += 4)
                            frame[i] = 255;
                    }
                    else
                    {
                        ended = true;
                    }
                }
                if (have < 0)
                    throw new InvalidOperationException("The recording couldn't be read.");
                while (!facesEnded && haveFace < want)
                {
                    if (ReadFull(faces!.StandardOutput.BaseStream, face))
                        haveFace++;
                    else
                        facesEnded = true;
                }
                double speed = timeline.At(time)?.Speed ?? 1;
                using (var screen = SKImage.FromPixels(screenPixels))
                using (var faceImage = haveFace >= 0 ? SKImage.FromPixels(facePixmap) : null)
                    renderer.Draw(canvas, screen, time, speed / fps, faceImage);
                canvas.Flush();
                sink.Write(target.GetPixelSpan());
                if (k % 10 == 0)
                    progress.Report(k / (double)total);
            }
        }
        finally
        {
            frameHandle.Free();
            faceHandle.Free();
            try { sink.Close(); } catch (IOException) { }
        }
    }

    /// <summary>Starts FFmpeg; what it can't decode itself (the recording's H.264) Windows decodes for it.</summary>
    private static async Task<Petal.Engines.Media.Foundation.FfmpegRun> StartAsync(string ffmpeg, IEnumerable<string> args, bool input = false, bool output = false,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = input, RedirectStandardOutput = output };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return await Petal.Engines.Media.Foundation.FfmpegRun.StartAsync(info, cancellationToken);
    }

    private static async Task<(int Code, string Errors)> RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        await using var run = await StartAsync(ffmpeg, args, cancellationToken: cancellationToken);
        var process = run.Process;
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        await run.FinishAsync();
        return (process.ExitCode, await errors);
    }

    private static bool ReadFull(Stream stream, byte[] buffer)
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
}
