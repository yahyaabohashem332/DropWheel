using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Petal.App.Wheels;

/// <summary>What is playing: the title, who made it, the cover, and whether it plays, shuffles or repeats.</summary>
public sealed record NowPlaying(string Title, string Artist, string App, bool Playing, bool? Shuffle, bool? Repeat, ImageSource? Cover);

/// <summary>
/// The music or video playing in any app that shows in Windows' own media controls (Spotify, a browser
/// tab, Media Player…), through the same session Windows uses for its volume pop-up.
/// </summary>
public static class MediaControl
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager.GetCurrentSession();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error(ex, "Reading what is playing");
            return null;
        }
    }

    /// <summary>What plays now, or null when nothing is.</summary>
    public static async Task<NowPlaying?> CurrentAsync()
    {
        var session = await SessionAsync();
        if (session is null)
            return null;
        try
        {
            var playback = session.GetPlaybackInfo();
            var media = await session.TryGetMediaPropertiesAsync();
            ImageSource? cover = null;
            if (media?.Thumbnail is { } thumbnail)
            {
                try
                {
                    using var stream = (await thumbnail.OpenReadAsync()).AsStreamForRead();
                    var copy = new MemoryStream();
                    await stream.CopyToAsync(copy);
                    copy.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 160;
                    image.StreamSource = copy;
                    image.EndInit();
                    image.Freeze();
                    cover = image;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException)
                {
                    cover = null;
                }
            }
            var app = session.SourceAppUserModelId ?? "";
            return new NowPlaying(
                media?.Title ?? "",
                media?.Artist ?? "",
                FriendlyApp(app),
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback.Controls.IsShuffleEnabled ? playback.IsShuffleActive : null,
                playback.Controls.IsRepeatEnabled ? playback.AutoRepeatMode is not null and not Windows.Media.MediaPlaybackAutoRepeatMode.None : null,
                cover);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"Spotify" for "Spotify.exe", "Chrome" for "chrome", the app's own name for a packaged one.</summary>
    private static string FriendlyApp(string id)
    {
        var name = id.Split('!')[0];
        name = Path.GetFileNameWithoutExtension(name.Contains('_') ? name.Split('_')[0] : name);
        name = name.Contains('.') ? name.Split('.')[^1] : name;
        return name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public static async Task<bool> PlayPauseAsync() => await SessionAsync() is { } s && await s.TryTogglePlayPauseAsync();

    public static async Task<bool> NextAsync() => await SessionAsync() is { } s && await s.TrySkipNextAsync();

    public static async Task<bool> PreviousAsync() => await SessionAsync() is { } s && await s.TrySkipPreviousAsync();

    public static async Task<bool> ShuffleAsync()
    {
        var session = await SessionAsync();
        if (session is null) return false;
        var info = session.GetPlaybackInfo();
        return info.Controls.IsShuffleEnabled && await session.TryChangeShuffleActiveAsync(!(info.IsShuffleActive ?? false));
    }

    /// <summary>Repeat cycles off, all, one, as players do.</summary>
    public static async Task<bool> RepeatAsync()
    {
        var session = await SessionAsync();
        if (session is null) return false;
        var info = session.GetPlaybackInfo();
        if (!info.Controls.IsRepeatEnabled) return false;
        var next = info.AutoRepeatMode switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            Windows.Media.MediaPlaybackAutoRepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.List,
        };
        return await session.TryChangeAutoRepeatModeAsync(next);
    }
}
