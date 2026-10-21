using System.Diagnostics;
using System.Collections.Concurrent;
using Petal.Core.Formats;
using Petal.Core.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media.Tests;

/// <summary>
/// Tiny media files generated on demand with FFmpeg's lavfi sources (testsrc2, sine, aevalsrc) into a temp
/// folder that is deleted when the test run ends.
/// </summary>
internal static class Fixtures
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Cache = new();
    private static int _dirCounter;

    public static string Root { get; } = CreateRoot();

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PetalMediaTests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        // PETAL_KEEP_TEST_OUTPUT=1 keeps the outputs, for checking them with other programs.
        if (Environment.GetEnvironmentVariable("PETAL_KEEP_TEST_OUTPUT") != "1")
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
        }
        return root;
    }

    /// <summary>A fresh empty folder for a test's outputs.</summary>
    public static string NewDir()
    {
        var dir = Path.Combine(Root, "out" + Interlocked.Increment(ref _dirCounter));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string Picture = "testsrc2=s=160x120:r=15:d=1";
    private const string Tone = "sine=f=440:r=44100:d=1";

    /// <summary>name → ffmpeg arguments (inputs, codecs) without the output path.</summary>
    private static readonly Dictionary<string, string[]> Recipes = new()
    {
        // One-second clips in every source format of the matrix.
        ["video.mp4"] = [.. AV, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-shortest"],
        ["video.mov"] = [.. AV, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-shortest"],
        ["video.mkv"] = [.. AV, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-shortest"],
        ["video.webm"] = [.. AV, "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "300k", "-c:a", "libopus", "-b:a", "64k", "-shortest"],
        ["video.avi"] = [.. AV, "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "128k", "-shortest"],
        ["video.wmv"] = [.. AV, "-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "128k", "-shortest"],
        ["video.gif"] = ["-f", "lavfi", "-i", "testsrc2=s=160x120:r=10:d=1", "-c:v", "gif"],
        ["audio.mp3"] = [.. A, "-c:a", "libmp3lame", "-b:a", "192k"],
        ["audio.m4a"] = [.. A, "-c:a", "aac", "-b:a", "192k"],
        ["audio.aac"] = [.. A, "-c:a", "aac", "-b:a", "192k"],
        ["audio.wav"] = [.. A, "-c:a", "pcm_s16le"],
        ["audio.flac"] = [.. A, "-c:a", "flac"],
        ["audio.ogg"] = [.. A, "-c:a", "libvorbis", "-q:a", "5"],
        ["audio.opus"] = [.. A, "-c:a", "libopus", "-b:a", "96k"],
        ["audio.aiff"] = [.. A, "-c:a", "pcm_s16be"],
        ["audio.wma"] = [.. A, "-c:a", "wmav2", "-b:a", "128k"],

        // Special cases.
        ["silent.mp4"] = ["-f", "lavfi", "-i", Picture, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"],
        ["odd.mkv"] = ["-f", "lavfi", "-i", "testsrc2=s=161x121:r=15:d=1", "-c:v", "ffv1"],
        ["odd.gif"] = ["-f", "lavfi", "-i", "testsrc2=s=161x121:r=10:d=1", "-c:v", "gif"],
        ["hevc.mov"] = [.. AV, "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"],
        ["big.mov"] = ["-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"],
        // Five seconds, near-lossless, a keyframe every second: room to compress, trim, split and change speed.
        ["hq.mp4"] = ["-f", "lavfi", "-i", "testsrc2=s=320x240:r=15:d=5", "-f", "lavfi", "-i", "sine=f=440:r=44100:d=5", "-ac", "2",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "8", "-g", "15", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest"],
        ["long.mp4"] = ["-f", "lavfi", "-i", "testsrc2=s=640x480:r=30:d=20", "-f", "lavfi", "-i", "sine=f=440:r=44100:d=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"],
        ["music.mp3"] = ["-f", "lavfi", "-i", "sine=f=440:r=44100:d=3", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "256k"],
        ["mono.wav"] = ["-f", "lavfi", "-i", Tone, "-c:a", "pcm_s16le"],
        ["left_only.wav"] = ["-f", "lavfi", "-i", "aevalsrc=0.5*sin(2*PI*440*t)|0:s=44100:d=1", "-c:a", "pcm_s16le"],
        // Silence 0–1 s, tone 1–2 s, pause 2–2.5 s, tone 2.5–3.5 s, silence to 4.5 s.
        ["padded.wav"] = ["-f", "lavfi", "-i", "aevalsrc='if(between(t,1,2)+between(t,2.5,3.5),0.3*sin(2*PI*440*t),0)':s=44100:d=4.5", "-c:a", "pcm_s16le"],
        ["quiet.wav"] = ["-f", "lavfi", "-i", "aevalsrc=0.03*sin(2*PI*440*t):s=44100:d=6", "-ac", "2", "-c:a", "pcm_s16le"],
        ["cover.jpg"] = ["-f", "lavfi", "-i", "color=c=orange:s=64x64", "-frames:v", "1"],
        ["tagged.mp4"] = [.. AV, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
            "-metadata", "title=Clip Title", "-metadata", "comment=Hello"],
        ["image.png"] = ["-f", "lavfi", "-i", "testsrc2=s=320x240:d=1", "-frames:v", "1", "-c:v", "png"],
    };

    private static string[] AV => ["-f", "lavfi", "-i", Picture, "-f", "lavfi", "-i", Tone, "-ac", "2"];

    private static string[] A => ["-f", "lavfi", "-i", Tone, "-ac", "2"];

    public static Task<string> GetAsync(string name) =>
        Cache.GetOrAdd(name, n => new Lazy<Task<string>>(() => CreateAsync(n))).Value;

    /// <summary>A one-second fixture in the given format ("video.gif" for GIF).</summary>
    public static Task<string> ForFormatAsync(FileFormat format) => GetAsync(format.Family switch
    {
        FormatFamily.Audio => $"audio.{format.WriteExtension}",
        _ => $"video.{format.WriteExtension}",
    });

    private static async Task<string> CreateAsync(string name)
    {
        if (name == "rotated.mp4")
        {
            var source = await GetAsync("video.mp4");
            return await RunAsync(name, ["-display_rotation:v:0", "90", "-i", source, "-c", "copy"]);
        }
        if (name == "tagged.mp3")
        {
            var cover = await GetAsync("cover.jpg");
            return await RunAsync(name, ["-f", "lavfi", "-i", Tone, "-i", cover, "-map", "0:a", "-map", "1:v",
                "-c:a", "libmp3lame", "-b:a", "128k", "-c:v", "copy", "-disposition:v", "attached_pic", "-id3v2_version", "3",
                "-metadata", "title=Petal Test", "-metadata", "artist=Petal Artist", "-metadata", "album=Fixtures"]);
        }
        return await RunAsync(name, Recipes[name]);
    }

    private static async Task<string> RunAsync(string name, string[] args)
    {
        var path = Path.Combine(Root, "fixtures", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = await ProcessRunner.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. args, path]);
        if (!result.Succeeded || !File.Exists(path))
            throw new InvalidOperationException($"Could not create fixture {name}: {result.StandardError}");
        return path;
    }

    /// <summary>A WAV of English speech from Windows' built-in voice, at 44.1 kHz.</summary>
    public static string Speech(string text)
    {
        var path = Path.Combine(Fixtures.NewDir(), "voice.wav");
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                     $"$s.SetOutputToWaveFile('{path.Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}'); $s.Dispose()";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]) { CreateNoWindow = true })!;
        process.WaitForExit(60_000);
        return path;
    }

    public static string Ffmpeg => ToolLocator.Ffmpeg ?? throw new InvalidOperationException("FFmpeg is required for these tests");

    /// <summary>One decoded RGB24 frame, for pixel checks.</summary>
    public static async Task<(byte[] Rgb, int Width, int Height)> RgbFrameAsync(string path, double time)
    {
        var info = await MediaProbe.ProbeAsync(path);
        var bytes = await ProcessRunner.RunForBytesAsync(Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", time.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-",
        ]);
        Assert.Equal(info.Width * info.Height * 3, bytes.Length);
        return (bytes, info.Width, info.Height);
    }

    public static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24, "PNG too short");
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        int ReadInt(int offset) => (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
        return (ReadInt(16), ReadInt(20));
    }
}

/// <summary>Records progress synchronously (unlike <see cref="Progress{T}"/>, which posts to the thread pool).</summary>
internal sealed class RecordingProgress : IProgress<double>
{
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_values)
                return _values.ToList();
        }
    }

    public double Last
    {
        get
        {
            lock (_values)
                return _values.Count == 0 ? double.NaN : _values[^1];
        }
    }

    public event Action<double>? Reported;

    public void Report(double value)
    {
        lock (_values)
            _values.Add(value);
        Reported?.Invoke(value);
    }

    public void AssertCompleteAndMonotonic()
    {
        var values = Values;
        Assert.NotEmpty(values);
        Assert.Equal(1.0, values[^1]);
        for (var i = 1; i < values.Count; i++)
            Assert.True(values[i] >= values[i - 1], $"progress went backwards: {values[i - 1]} → {values[i]}");
        Assert.All(values, v => Assert.InRange(v, 0, 1));
    }
}

internal static class MediaAssert
{
    public static async Task<MediaInfo> ValidAsync(string path, double? duration = null, double tolerance = 0.25)
    {
        Assert.True(File.Exists(path), $"missing output {path}");
        Assert.False(File.Exists(path + ".petal-tmp"), "temp file left behind");
        var info = await MediaProbe.ProbeAsync(path);
        if (duration is { } d)
            Assert.InRange(info.DurationSeconds, d - tolerance, d + tolerance);
        return info;
    }

    public static void NoTempFiles(string directory) =>
        Assert.Empty(Directory.EnumerateFiles(directory, "*.petal-tmp", SearchOption.AllDirectories));

    public static FileFormat Format(string id) => F.FromId(id) ?? throw new ArgumentException(id);
}
