using System.Windows.Media;

namespace Petal.App.Overlay;

/// <summary>How the wheel arrives.</summary>
public enum WheelMotion
{
    /// <summary>Blooms out of the pointer (the usual).</summary>
    Bloom,
    /// <summary>Pops out with a little overshoot.</summary>
    Pop,
    /// <summary>Fades in where it is.</summary>
    Fade,
    /// <summary>Unfolds petal by petal, clockwise.</summary>
    Unfold,
    /// <summary>Appears at once.</summary>
    None,
}

/// <summary>What the wheel is made of.</summary>
public enum WheelStyle
{
    /// <summary>Frosted glass over what is behind it.</summary>
    Glass,
    /// <summary>An opaque surface.</summary>
    Solid,
    /// <summary>Glass in greys, without the accent colour.</summary>
    Mono,
}

/// <summary>The wheel's behaviour and looks that the user can change, read each time it opens.</summary>
public sealed record WheelOptions(
    CancelZone CancelZone,
    bool Grow,
    WheelMotion Motion,
    bool ShowNames,
    bool ShowNumbers,
    bool ScrollSwitches,
    WheelStyle Style)
{
    public static WheelOptions Default { get; } = new(CancelZone.Small, true, WheelMotion.Bloom, true, true, true, WheelStyle.Glass);
}

/// <summary>A wheel that needs no files: quick actions, media, the app in front, or one of the user's.</summary>
/// <param name="Key">Stable, for memory: "quick", "media", or a wheel's id.</param>
/// <param name="Content">Built when shown, so switches show their state as it is now.</param>
/// <param name="Hub">What the centre says with nothing under the pointer.</param>
public sealed record RingPage(string Key, string Title, Func<WheelContent> Content, Func<RingHub?>? Hub = null);

/// <summary>The centre of a wheel without files: a title, a line under it, and perhaps a picture that turns.</summary>
public sealed record RingHub(string Title, string Subtitle, ImageSource? Picture = null, bool Spin = false);
