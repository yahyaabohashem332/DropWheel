using System.Globalization;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

/// <summary>A length of time typed by hand: "7" (minutes), "1:30", "90s", "1h 15m", "٥ دقائق".</summary>
public static partial class Durations
{
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = Normalise(text);
        // 1:30 or 1:02:30
        if (Clock().Match(t) is { Success: true } clock)
        {
            var parts = clock.Value.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var span = parts.Length == 3 ? new TimeSpan(parts[0], parts[1], parts[2]) : new TimeSpan(0, parts[0], parts[1]);
            return Valid(span);
        }
        // A plain number is minutes.
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
            return Valid(TimeSpan.FromMinutes(minutes));
        var total = TimeSpan.Zero;
        bool any = false;
        foreach (Match m in Pieces().Matches(t))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value;
            total += unit switch
            {
                "h" or "hr" or "hrs" or "hour" or "hours" or "س" or "ساعة" or "ساعات" => TimeSpan.FromHours(n),
                "s" or "sec" or "secs" or "second" or "seconds" or "ث" or "ثانية" or "ثواني" or "ثوان" => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMinutes(n),
            };
            any = true;
        }
        return any ? Valid(total) : null;
    }

    private static TimeSpan? Valid(TimeSpan span) => span > TimeSpan.Zero && span < TimeSpan.FromDays(1) ? span : null;

    private static string Normalise(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => c switch
        {
            >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
            >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
            '٫' or ',' => '.',
            _ => c,
        });
        return new string(chars.ToArray());
    }

    [GeneratedRegex(@"^\d{1,2}(:\d{1,2}){1,2}$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(hours|hour|hrs|hr|h|minutes|minute|mins|min|m|seconds|second|secs|sec|s|ساعات|ساعة|س|دقائق|دقيقة|د|ثواني|ثوان|ثانية|ث)")]
    private static partial Regex Pieces();

    /// <summary>"4:05", "1:02:03".</summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
