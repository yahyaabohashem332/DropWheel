using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Wheels;

namespace Petal.App.Clips;

public enum ClipKind
{
    Text,
    Picture,
    Files,
}

/// <summary>One thing that was copied: text, a picture (kept as PNG), or files.</summary>
public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public List<string>? Files { get; set; }

    /// <summary>For a picture: its PNG, in memory and (when the history is remembered) in a file.</summary>
    [JsonIgnore] public byte[]? Png { get; set; }
    public string? PngFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Copied { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    /// <summary>A line to show for it: the text's start, "Picture 800 × 600", the file names.</summary>
    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Text => string.Join(' ', (Text ?? "").Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim() is var t && t.Length > 80 ? t[..80] + "…" : string.Join(' ', (Text ?? "").Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim(),
        ClipKind.Picture => Localization.L.F("Picture {0} × {1}", Width, Height),
        _ => Files is { Count: 1 } one ? Path.GetFileName(one[0]) : Localization.L.P(Files?.Count ?? 0, "{0} file", "{0} files"),
    };

    public BitmapSource? Picture()
    {
        var png = Png ?? (PngFile is { } f && File.Exists(f) ? File.ReadAllBytes(f) : null);
        if (png is null)
            return null;
        try
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// What was copied lately, to paste again from the Clipboard wheel or its window. Kept while Petal runs
/// (and after it closes, when the user asks). Copies that password managers and other apps mark as private
/// are never kept, nor Petal's own borrowing of the clipboard.
/// </summary>
public static class ClipboardHistory
{
    public const int Keep = 30;
    private const int Pictures = 8;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly List<ClipItem> Items = [];
    private static HwndSource? _window;
    private static int _paused;
    private static System.Windows.Threading.DispatcherTimer? _settle;

    public static event Action? Changed;

    /// <summary>Newest first, pinned ones on top.</summary>
    public static IReadOnlyList<ClipItem> All
    {
        get
        {
            lock (Items)
                return Items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Copied).ToList();
        }
    }

    private static string Folder => Path.Combine(SettingsStore.Directory, "Clipboard");

    private static string FilePath => Path.Combine(Folder, "history.json");

    public static void Start()
    {
        if (_window is not null)
            return;
        Load();
        _window = new HwndSource(new HwndSourceParameters("Petal clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
        ScreenNative.AddClipboardFormatListener(_window.Handle);
        _settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Capture();
        };
        SettingsStore.Saved += s =>
        {
            if (!s.KeepClipboardHistory)
                Clear(keepPinned: true);
            Save();
        };
    }

    public static void Stop()
    {
        if (_window is null)
            return;
        ScreenNative.RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
        _window = null;
        Save();
    }

    /// <summary>Petal is borrowing the clipboard: what passes through now isn't something the user copied.</summary>
    public static void Pause() => Interlocked.Increment(ref _paused);

    public static void Resume()
    {
        // The last change arrives a moment after; let it go by first.
        _ = Task.Delay(400).ContinueWith(_ => Interlocked.Decrement(ref _paused));
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _paused == 0 && SettingsStore.Current.KeepClipboardHistory)
        {
            _settle?.Stop();
            _settle?.Start();
        }
        return IntPtr.Zero;
    }

    private static void Capture()
    {
        if (_paused > 0)
            return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || IsPrivate(data))
                return;
            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem { Kind = ClipKind.Files, Files = [.. files] };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
                item = new ClipItem { Kind = ClipKind.Text, Text = text.Length > 200_000 ? text[..200_000] : text };
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                if (stream.Length < 40_000_000)
                    item = new ClipItem { Kind = ClipKind.Picture, Png = stream.ToArray(), Width = image.PixelWidth, Height = image.PixelHeight };
            }
            if (item is not null)
                Add(item);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException or NotSupportedException)
        {
            // The app that copied keeps the clipboard busy, or renders it late; this one is let go.
        }
    }

    /// <summary>Copies that apps ask clipboard managers to leave alone (passwords, one-time codes).</summary>
    private static bool IsPrivate(IDataObject data)
    {
        var formats = data.GetFormats(false);
        if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore"))
            return true;
        foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!formats.Contains(name))
                continue;
            try
            {
                if (data.GetData(name) is MemoryStream stream && stream.Length >= 4)
                {
                    var bytes = stream.ToArray();
                    if (BitConverter.ToInt32(bytes, 0) == 0)
                        return true;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
            }
        }
        return false;
    }

    internal static void Add(ClipItem item)
    {
        lock (Items)
        {
            // The same thing copied again moves to the top.
            var same = Items.FirstOrDefault(i => i.Kind == item.Kind && i.Kind switch
            {
                ClipKind.Text => i.Text == item.Text,
                ClipKind.Files => i.Files!.SequenceEqual(item.Files!, StringComparer.OrdinalIgnoreCase),
                _ => i.Png is { } a && item.Png is { } b && a.AsSpan().SequenceEqual(b),
            });
            if (same is not null)
            {
                same.Copied = DateTime.Now;
            }
            else
            {
                Items.Add(item);
                var extra = Items.Where(i => !i.Pinned).OrderByDescending(i => i.Copied).Skip(Keep)
                    // Pictures are big: only the latest few stay in memory.
                    .Concat(Items.Where(i => !i.Pinned && i.Kind == ClipKind.Picture).OrderByDescending(i => i.Copied).Skip(Pictures))
                    .Distinct().ToList();
                foreach (var old in extra)
                    Forget(old);
            }
        }
        Save();
        Changed?.Invoke();
    }

    private static void Forget(ClipItem item)
    {
        Items.Remove(item);
        if (item.PngFile is { } file)
            try { File.Delete(file); } catch (IOException) { }
    }

    public static void Remove(ClipItem item)
    {
        lock (Items)
            Forget(item);
        Save();
        Changed?.Invoke();
    }

    public static void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
        Changed?.Invoke();
    }

    public static void Clear(bool keepPinned)
    {
        lock (Items)
            foreach (var item in Items.Where(i => !keepPinned || !i.Pinned).ToList())
                Forget(item);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Puts a clip back on the clipboard.</summary>
    public static bool Put(ClipItem item)
    {
        try
        {
            var data = new DataObject();
            switch (item.Kind)
            {
                case ClipKind.Text:
                    data.SetData(DataFormats.UnicodeText, item.Text ?? "");
                    break;
                case ClipKind.Files:
                    var list = new StringCollection();
                    list.AddRange(item.Files!.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray());
                    if (list.Count == 0)
                        return false;
                    data.SetFileDropList(list);
                    break;
                default:
                    if (item.Picture() is not { } picture)
                        return false;
                    data.SetImage(picture);
                    break;
            }
            Clipboard.SetDataObject(data, true);
            item.Copied = DateTime.Now;
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    /// <summary>Pastes a clip into the app that was in front, and leaves it on the clipboard.</summary>
    public static async Task<bool> PasteAsync(ClipItem item, IntPtr target)
    {
        if (!Put(item))
            return false;
        Changed?.Invoke();
        return await KeySender.SendAsync(target, [new KeyChord(ModifierKeys.Control, Key.V)]);
    }

    // ---------- kept on disk ----------

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(FilePath), SettingsStore.Options) ?? [];
            lock (Items)
            {
                Items.Clear();
                // Without "remember", only the pinned ones come back.
                Items.AddRange(items.Where(i => i.Pinned || SettingsStore.Current.RememberClipboardHistory));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Reading the clipboard history");
        }
    }

    /// <summary>The pinned clips always; the rest only when the history is to be remembered.</summary>
    private static void Save()
    {
        try
        {
            List<ClipItem> keep;
            lock (Items)
                keep = Items.Where(i => i.Pinned || SettingsStore.Current.RememberClipboardHistory).ToList();
            if (keep.Count == 0)
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Folder);
            foreach (var item in keep.Where(i => i.Kind == ClipKind.Picture && i.PngFile is null && i.Png is not null))
            {
                item.PngFile = Path.Combine(Folder, item.Id + ".png");
                File.WriteAllBytes(item.PngFile, item.Png!);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keep, SettingsStore.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving the clipboard history");
        }
    }
}
