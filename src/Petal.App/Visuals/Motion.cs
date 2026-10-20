using System.Windows;
using System.Windows.Media.Animation;

namespace Petal.App.Visuals;

/// <summary>CSS-style cubic-bezier easing.</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }

    public CubicBezierEase()
    {
        EasingMode = EasingMode.EaseIn; // EaseInCore is used as-is
    }

    public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(s) = t with Newton's method, then return y(s).
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bezier(s, X1, X2) - t;
            double dx = Derivative(s, X1, X2);
            if (Math.Abs(x) < 1e-5) break;
            if (Math.Abs(dx) < 1e-6) break;
            s -= x / dx;
            s = Math.Clamp(s, 0, 1);
        }
        return Bezier(s, Y1, Y2);
    }

    private static double Bezier(double s, double p1, double p2) =>
        3 * p1 * s * (1 - s) * (1 - s) + 3 * p2 * s * s * (1 - s) + s * s * s;

    private static double Derivative(double s, double p1, double p2) =>
        3 * p1 * (1 - s) * (1 - s) + 6 * (p2 - p1) * s * (1 - s) + 3 * (1 - p2) * s * s;

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);
}

/// <summary>Damped spring easing that overshoots and settles, like a physical spring.</summary>
public sealed class SpringEase : EasingFunctionBase
{
    /// <summary>Damping ratio, below 1 overshoots.</summary>
    public double Damping { get; init; } = 0.55;

    /// <summary>Oscillation speed in radians over the animation.</summary>
    public double Stiffness { get; init; } = 11;

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        double zeta = Damping, w = Stiffness;
        double wd = w * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * w * t) * (Math.Cos(wd * t) + zeta * w / wd * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase { Damping = Damping, Stiffness = Stiffness };
}

public static class Motion
{
    /// <summary>Wheel bloom: fast exponential settle without overshoot (~180 ms).</summary>
    public static readonly IEasingFunction Bloom = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });

    public static readonly IEasingFunction Standard = Freeze(new CubicBezierEase(0.2, 0, 0, 1));

    /// <summary>Popping cards and windows out of the cursor, overshooting to ~1.17×.</summary>
    public static readonly IEasingFunction Pop = Freeze(new SpringEase { Damping = 0.5, Stiffness = 12 });

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    public static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double? from = null, double delayMs = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Standard,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        if (from is { } f)
            a.From = f;
        return a;
    }
}
