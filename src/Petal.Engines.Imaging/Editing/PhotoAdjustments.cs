namespace Petal.Engines.Imaging;

/// <summary>
/// Edit Photo sliders. Every value is neutral at 0. Ranges: <see cref="Exposure"/> −4…+4 EV,
/// <see cref="Hue"/> −180…+180°, <see cref="Sharpness"/>/<see cref="NoiseReduction"/>/<see cref="Grain"/>
/// 0…100, everything else −100…+100.
/// </summary>
public sealed record PhotoAdjustments
{
    public static PhotoAdjustments Neutral { get; } = new();

    // Light
    public double Exposure { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Gamma { get; init; }

    // Color
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Saturation { get; init; }
    public double Vibrance { get; init; }
    public double Hue { get; init; }

    // Detail
    public double Clarity { get; init; }
    public double Sharpness { get; init; }
    public double NoiseReduction { get; init; }

    // Effects
    public double Dehaze { get; init; }
    public double Grain { get; init; }
    public double Vignette { get; init; }

    public bool IsNeutral => this == Neutral;

    /// <summary>Values clamped to their slider ranges.</summary>
    public PhotoAdjustments Clamped() => this with
    {
        Exposure = Math.Clamp(Exposure, -4, 4),
        Brightness = C(Brightness), Contrast = C(Contrast), Highlights = C(Highlights), Shadows = C(Shadows),
        Whites = C(Whites), Blacks = C(Blacks), Gamma = C(Gamma),
        Temperature = C(Temperature), Tint = C(Tint), Saturation = C(Saturation), Vibrance = C(Vibrance),
        Hue = Math.Clamp(Hue, -180, 180),
        Clarity = C(Clarity), Sharpness = Math.Clamp(Sharpness, 0, 100), NoiseReduction = Math.Clamp(NoiseReduction, 0, 100),
        Dehaze = C(Dehaze), Grain = Math.Clamp(Grain, 0, 100), Vignette = C(Vignette),
    };

    private static double C(double v) => Math.Clamp(v, -100, 100);

    /// <summary>Slider rows in window order, so the UI can be generated from data.</summary>
    public static IReadOnlyList<PhotoAdjustmentSlider> Sliders { get; } =
    [
        new("Exposure", "Light", -4, 4, 0.01, "0.00 EV", a => a.Exposure, (a, v) => a with { Exposure = v }),
        new("Brightness", "Light", -100, 100, 1, "+0;-0;0", a => a.Brightness, (a, v) => a with { Brightness = v }),
        new("Contrast", "Light", -100, 100, 1, "+0;-0;0", a => a.Contrast, (a, v) => a with { Contrast = v }),
        new("Highlights", "Light", -100, 100, 1, "+0;-0;0", a => a.Highlights, (a, v) => a with { Highlights = v }),
        new("Shadows", "Light", -100, 100, 1, "+0;-0;0", a => a.Shadows, (a, v) => a with { Shadows = v }),
        new("Whites", "Light", -100, 100, 1, "+0;-0;0", a => a.Whites, (a, v) => a with { Whites = v }),
        new("Blacks", "Light", -100, 100, 1, "+0;-0;0", a => a.Blacks, (a, v) => a with { Blacks = v }),
        new("Gamma", "Light", -100, 100, 1, "+0;-0;0", a => a.Gamma, (a, v) => a with { Gamma = v }),
        new("Temperature", "Color", -100, 100, 1, "+0;-0;0", a => a.Temperature, (a, v) => a with { Temperature = v }),
        new("Tint", "Color", -100, 100, 1, "+0;-0;0", a => a.Tint, (a, v) => a with { Tint = v }),
        new("Saturation", "Color", -100, 100, 1, "+0;-0;0", a => a.Saturation, (a, v) => a with { Saturation = v }),
        new("Vibrance", "Color", -100, 100, 1, "+0;-0;0", a => a.Vibrance, (a, v) => a with { Vibrance = v }),
        new("Hue", "Color", -180, 180, 1, "0°", a => a.Hue, (a, v) => a with { Hue = v }),
        new("Clarity", "Detail", -100, 100, 1, "+0;-0;0", a => a.Clarity, (a, v) => a with { Clarity = v }),
        new("Sharpness", "Detail", 0, 100, 1, "0", a => a.Sharpness, (a, v) => a with { Sharpness = v }),
        new("Noise Reduction", "Detail", 0, 100, 1, "0", a => a.NoiseReduction, (a, v) => a with { NoiseReduction = v }),
        new("Dehaze", "Effects", -100, 100, 1, "+0;-0;0", a => a.Dehaze, (a, v) => a with { Dehaze = v }),
        new("Grain", "Effects", 0, 100, 1, "0", a => a.Grain, (a, v) => a with { Grain = v }),
        new("Vignette", "Effects", -100, 100, 1, "+0;-0;0", a => a.Vignette, (a, v) => a with { Vignette = v }),
    ];
}

/// <summary>One slider of the Edit Photo window.</summary>
/// <param name="Section">"Light", "Color", "Detail" or "Effects".</param>
/// <param name="Format">.NET number format for the value label, e.g. "0.00 EV".</param>
public sealed record PhotoAdjustmentSlider(
    string Label,
    string Section,
    double Minimum,
    double Maximum,
    double Step,
    string Format,
    Func<PhotoAdjustments, double> Get,
    Func<PhotoAdjustments, double, PhotoAdjustments> Set);
