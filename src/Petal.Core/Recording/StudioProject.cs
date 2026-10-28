using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Petal.Core.Imaging;

namespace Petal.Core.Recording;

/// <summary>The pointer's shape in a sample: one of Windows' own, or one taken from the screen (<see cref="StudioProject.Shapes"/>).</summary>
public enum PointerKind { Arrow, Text, Hand, Wait, Busy, Cross, SizeWE, SizeNS, SizeNWSE, SizeNESW, Move, No, Other }

/// <summary>Where the pointer was at a moment of a recording, in the recording's pixels, and what it looked like.</summary>
public readonly record struct PointerSample(double Time, double X, double Y, bool Visible, PointerKind Kind = PointerKind.Arrow, int Shape = -1);

/// <summary>A shortcut pressed during a recording ("Ctrl + C"); plain typing is never kept.</summary>
public readonly record struct KeySample(double Time, string Keys);

/// <summary>A key typed: when, and where the text cursor was when Windows could tell (<paramref name="Placed"/>).</summary>
public readonly record struct TypingSample(double Time, double X, double Y, bool Placed);

/// <summary>A pointer shape that isn't one of Windows' own, as it was on the screen: a picture in the project's folder and its hot spot.</summary>
public sealed record PointerShape(string File, double HotX, double HotY, int Width, int Height);

/// <summary>Where the camera's bubble was from a moment on, in the recording's pixels.</summary>
public readonly record struct CameraPlace(double Time, double X, double Y, double Diameter);

/// <summary>A stretch where the view goes in: how far, and whether it follows the action or looks at one place.</summary>
public sealed record ZoomSegment(double Start, double End, double Level, bool Follow = true, double X = 0, double Y = 0)
{
    [JsonIgnore]
    public double Length => End - Start;
}

public enum EditKind { Cut, Speed }

/// <summary>A stretch taken out, or played faster (<see cref="Speed"/> times).</summary>
public sealed record EditSegment(double Start, double End, EditKind Kind, double Speed = 1)
{
    [JsonIgnore]
    public double Length => End - Start;
}

/// <summary>How a cinematic recording looks.</summary>
public sealed record StudioLook
{
    /// <summary>A gradient's key, "wallpaper", "blur" (the recording itself, blurred) or "none" (the recording fills the frame).</summary>
    public string Background { get; init; } = "violet";

    /// <summary>The space around the recording, as a share of the frame's shorter side.</summary>
    public double Padding { get; init; } = 0.06;

    /// <summary>How round the recording's corners are, as a share of the frame's shorter side.</summary>
    public double Roundness { get; init; } = 0.022;

    public bool Shadow { get; init; } = true;

    /// <summary>"recorded", "16:9", "9:16", "1:1" or "4:5".</summary>
    public string Shape { get; init; } = "recorded";

    /// <summary>The pointer's size against how it was on the screen.</summary>
    public double PointerSize { get; init; } = 1.5;

    public bool SmoothPointer { get; init; } = true;

    public bool HideStillPointer { get; init; } = true;

    public bool ClickRings { get; init; } = true;

    public bool MotionBlur { get; init; } = true;

    public bool ShowKeys { get; init; } = true;

    /// <summary>The microphone's hum and hiss taken down before it is mixed.</summary>
    public bool CleanVoice { get; init; } = true;

    /// <summary>The sound brought to the loudness video apps play at.</summary>
    public bool EvenVolume { get; init; } = true;

    [JsonIgnore]
    public bool Framed => Background != "none";
}

/// <summary>The finished video's size and smoothness.</summary>
public sealed record StudioOutput
{
    /// <summary>"auto", "1080", "1440" or "2160": the frame's shorter side.</summary>
    public string Quality { get; init; } = "auto";

    public int Fps { get; init; } = 60;
}

/// <summary>Everything the studio lets you change: one value, so it can be undone and redone whole.</summary>
public sealed record StudioEdit
{
    public double TrimStart { get; init; }

    public double TrimEnd { get; init; }

    public IReadOnlyList<ZoomSegment> Zooms { get; init; } = [];

    public IReadOnlyList<EditSegment> Edits { get; init; } = [];

    public StudioLook Look { get; init; } = new();

    public StudioOutput Output { get; init; } = new();
}

/// <summary>
/// A recording made into a studio project: the screen's video (with the sound mixed, for playing), the sounds
/// apart, the camera's video, and what happened while it was recorded — the pointer many times a second, the
/// clicks, the typing and the shortcuts — with the edits made to it. It lives in its own folder.
/// </summary>
public sealed class StudioProject
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTime Made { get; set; } = DateTime.Now;

    /// <summary>The screen's video (in the folder, or a full path for a video opened from elsewhere).</summary>
    public string Video { get; set; } = "screen.mp4";

    /// <summary>A copy Windows can play, for a video it can't (else the video itself is played).</summary>
    public string? Preview { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double Fps { get; set; } = 30;

    public double Duration { get; set; }

    /// <summary>The screen's scale when it was recorded (1.5 at 150 %), for the pointer's size.</summary>
    public double PixelScale { get; set; } = 1;

    public string? SystemSound { get; set; }

    public string? Microphone { get; set; }

    public string? Camera { get; set; }

    public int CameraSide { get; set; }

    public List<CameraPlace> CameraPlaces { get; set; } = [];

    /// <summary>The pointer was recorded (a recording made here); a video opened from elsewhere has its pointer in its pictures.</summary>
    public bool HasPointer { get; set; }

    public List<PointerSample> Pointer { get; set; } = [];

    public List<ClickSample> Clicks { get; set; } = [];

    public List<KeySample> Keys { get; set; } = [];

    public List<TypingSample> Typing { get; set; } = [];

    public List<PointerShape> Shapes { get; set; } = [];

    public StudioEdit Edit { get; set; } = new();

    /// <summary>The last video saved from it.</summary>
    public string? Exported { get; set; }

    /// <summary>When the last video was saved, to tell edits not saved yet.</summary>
    public DateTime? ExportedAt { get; set; }

    /// <summary>The folder the project lives in.</summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    [JsonIgnore]
    public bool HasSound => SystemSound is not null || Microphone is not null;

    /// <summary>A file of the project's, wherever it is.</summary>
    public string PathOf(string file) => Path.IsPathRooted(file) ? file : Path.Combine(Folder, file);

    public const string FileName = "project.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The project in a folder, or null when there is none (or it can't be read).</summary>
    public static StudioProject? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var project = JsonSerializer.Deserialize<StudioProject>(File.ReadAllText(path), Options);
            if (project is null || project.Version > CurrentVersion || project.Width <= 0 || project.Height <= 0)
                return null;
            project.Folder = folder;
            return project;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The edits made automatically: the whole recording kept, the view going in where things happen.</summary>
    public StudioEdit Automatic(StudioLook look, StudioOutput output, double zoom) => new()
    {
        TrimStart = 0,
        TrimEnd = Duration,
        Zooms = HasPointer ? ZoomPlanner.Auto(this, zoom) : [],
        Edits = [],
        Look = look,
        Output = output,
    };
}
