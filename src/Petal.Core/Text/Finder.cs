using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

/// <summary>The kinds of things a piece of text can hold that something can be done with.</summary>
public enum FoundKind
{
    Link,
    Email,
    Phone,
    When,
    Money,
    Measure,
    Colour,
    Sum,
}

/// <summary>
/// One thing found in a text: where it is (<see cref="Start"/> and <see cref="Length"/> in the text as given), what it
/// says there, and what it means.
/// </summary>
public sealed record Found(FoundKind Kind, int Start, int Length, string Text)
{
    /// <summary>A link's full address, an e-mail address, a number to dial (+ and digits), a colour as #RRGGBB, a sum's answer as shown.</summary>
    public string Value { get; init; } = "";

    /// <summary>An amount of money or of a unit; a sum's answer.</summary>
    public double Amount { get; init; }

    /// <summary>A unit's short name ("km", "°F") or a currency's code ("USD").</summary>
    public string Unit { get; init; } = "";

    /// <summary>A moment by its own clock, as written; <see cref="HasDate"/> and <see cref="HasTime"/> say which parts were.</summary>
    public DateTime? When { get; init; }

    public bool HasDate { get; init; }

    public bool HasTime { get; init; }

    /// <summary>The offset from UTC of the zone the text named, when it is fixed ("PST", "GMT+3").</summary>
    public TimeSpan? Offset { get; init; }

    /// <summary>A Windows time zone, when the text named one whose offset depends on the date ("ET", "بتوقيت القاهرة").</summary>
    public string? Zone { get; init; }

    /// <summary>The moment in UTC, when the text named its zone; null when it named none.</summary>
    public DateTime? Utc
    {
        get
        {
            if (When is not { } when)
                return null;
            if (Offset is { } offset)
                return DateTime.SpecifyKind(when - offset, DateTimeKind.Utc);
            if (Zone is not null && Finder.Zone(Zone) is { } zone)
            {
                try
                {
                    return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(when, DateTimeKind.Unspecified), zone);
                }
                catch (ArgumentException)
                {
                    // A time skipped when the clocks went forward: an hour later is what was meant.
                    return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(when.AddHours(1), DateTimeKind.Unspecified), zone);
                }
            }
            return null;
        }
    }
}

/// <summary>
/// Finds what a piece of text holds that something can be done with: links, e-mail addresses, phone numbers, dates and
/// times (with the zone they were given in), amounts of money, measurements, colours, and a sum when the whole text is one.
/// English and Arabic first, and the dates of DropWheel's other languages; Arabic-Indic digits count as digits.
/// </summary>
public static partial class Finder
{
    public static IReadOnlyList<Found> Find(string text) => Find(text, DateTime.Now, CultureInfo.CurrentCulture, Money.HomeCurrency);

    /// <param name="now">For dates written without a year: the next time that day comes.</param>
    /// <param name="culture">For numeric dates: which comes first, the day or the month.</param>
    /// <param name="home">The reader's currency, for names several currencies share ("ريال", "$").</param>
    public static IReadOnlyList<Found> Find(string text, DateTime now, CultureInfo culture, string? home)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var plain = Western(text);
        var found = new List<Found>();
        found.AddRange(Links(plain, text));
        found.AddRange(Emails(plain, text));
        found.AddRange(Moments(plain, text, now, culture));
        foreach (var m in Money.Find(plain, home))
            found.Add(new Found(FoundKind.Money, m.Start, m.Length, text.Substring(m.Start, m.Length)) { Amount = m.Amount, Unit = m.Code });
        foreach (var m in Calculator.FindMeasures(plain))
            found.Add(new Found(FoundKind.Measure, m.Start, m.Length, text.Substring(m.Start, m.Length)) { Amount = m.Value, Unit = m.Unit });
        found.AddRange(Phones(plain, text));
        found.AddRange(Colours(plain, text));
        // The first kind to claim a stretch of text keeps it.
        var kept = new List<Found>();
        foreach (var f in found)
            if (!kept.Any(k => f.Start < k.Start + k.Length && k.Start < f.Start + f.Length))
                kept.Add(f);
        if (Sum(plain, text, kept) is { } sum)
            kept.Add(sum);
        return kept.OrderBy(f => f.Start).ThenBy(f => f.Kind).ToList();
    }

    /// <summary>The text with Western digits in place of Arabic-Indic ones, and the Arabic decimal and thousands marks as . and ,; same length.</summary>
    internal static string Western(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch
            {
                >= '٠' and <= '٩' => (char)('0' + (chars[i] - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (chars[i] - '۰')),
                '٫' => '.',
                '٬' => ',',
                '−' => '-',
                _ => chars[i],
            };
        return new string(chars);
    }

    // ---------- links and addresses ----------

    private static IEnumerable<Found> Links(string plain, string text)
    {
        foreach (Match m in LinkPattern().Matches(plain))
        {
            var value = m.Value;
            // A closing bracket belongs to the link only when it opened one.
            while (value.EndsWith(')') && value.Count(c => c == '(') < value.Count(c => c == ')'))
                value = value[..^1];
            if (value.Length < 4)
                continue;
            var address = value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length < 3 || !uri.Host.Contains('.'))
                continue;
            yield return new Found(FoundKind.Link, m.Index, value.Length, text.Substring(m.Index, value.Length)) { Value = address };
        }
    }

    private static IEnumerable<Found> Emails(string plain, string text)
    {
        foreach (Match m in EmailPattern().Matches(plain))
            yield return new Found(FoundKind.Email, m.Index, m.Length, text.Substring(m.Index, m.Length)) { Value = m.Value };
    }

    // ---------- phone numbers ----------

    private static IEnumerable<Found> Phones(string plain, string text)
    {
        foreach (Match m in PhonePattern().Matches(plain))
        {
            var run = m.Value.TrimEnd(' ', '-', '.');
            var digits = new string(run.Where(char.IsAsciiDigit).ToArray());
            bool international = run.StartsWith('+') || run.StartsWith("00", StringComparison.Ordinal);
            if (digits.Length < 8 || digits.Length > 15 || DateLike().IsMatch(run))
                continue;
            // A decimal or a version number has one dot; a phone number written with dots has groups of digits.
            if (run.Contains('.') && !DottedPhone().IsMatch(run))
                continue;
            bool local = run.StartsWith('0') || run.StartsWith('(');
            bool grouped = UsGroups().IsMatch(run);
            if (!international && !local && !grouped)
                continue;
            if (!international && !run.Any(c => c is ' ' or '-' or '(' or '.') && digits.Length < 9)
                continue;
            var dial = international ? "+" + (digits.StartsWith("00", StringComparison.Ordinal) && !run.StartsWith('+') ? digits[2..] : digits) : digits;
            yield return new Found(FoundKind.Phone, m.Index, run.Length, text.Substring(m.Index, run.Length)) { Value = dial };
        }
    }

    // ---------- colours ----------

    private static IEnumerable<Found> Colours(string plain, string text)
    {
        foreach (Match m in HexColour().Matches(plain))
        {
            var hex = m.Groups["hex"].Value;
            // Three digits only count after a #, and digits alone are a number, not a colour.
            if (hex.All(char.IsAsciiDigit) && hex.Length != 6)
                continue;
            var full = hex.Length is 3 or 4 ? string.Concat(hex[..3].Select(c => new string(c, 2))) : hex[..6];
            yield return new Found(FoundKind.Colour, m.Index, m.Length, text.Substring(m.Index, m.Length)) { Value = "#" + full.ToUpperInvariant() };
        }
        foreach (Match m in RgbColour().Matches(plain))
        {
            int r = int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture), g = int.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
            if (r > 255 || g > 255 || b > 255)
                continue;
            yield return new Found(FoundKind.Colour, m.Index, m.Length, text.Substring(m.Index, m.Length)) { Value = $"#{r:X2}{g:X2}{b:X2}" };
        }
        foreach (Match m in HslColour().Matches(plain))
        {
            double h = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), s = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), l = double.Parse(m.Groups["l"].Value, CultureInfo.InvariantCulture);
            if (s > 100 || l > 100)
                continue;
            var (r, g, b) = FromHsl(h % 360, s / 100, l / 100);
            yield return new Found(FoundKind.Colour, m.Index, m.Length, text.Substring(m.Index, m.Length)) { Value = $"#{r:X2}{g:X2}{b:X2}" };
        }
    }

    internal static (int R, int G, int B) FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    // ---------- a sum ----------

    /// <summary>The whole text as a sum or a conversion ("12 × (3 + 4)", "15% of 240", "5 km in mi"), when nothing else claims it.</summary>
    private static Found? Sum(string plain, string text, List<Found> kept)
    {
        var trimmed = plain.Trim();
        if (trimmed.Length is < 3 or > 120 || !trimmed.Any(char.IsAsciiDigit))
            return null;
        // A date, a phone number or a link that fills the text is not a sum to work out.
        if (kept.Any(k => k.Kind is not FoundKind.Measure and not FoundKind.Money && k.Length >= trimmed.Length - 2))
            return null;
        bool sign = SumSign().IsMatch(trimmed);
        bool converts = Calculator.Evaluate(trimmed) is { Unit.Length: > 0 } && kept.Count(k => k.Kind == FoundKind.Measure) == 1 && ConvertWord().IsMatch(trimmed);
        if (!sign && !converts)
            return null;
        if (Calculator.Evaluate(trimmed) is not { } answer)
            return null;
        int start = text.Length - text.TrimStart().Length;
        return new Found(FoundKind.Sum, start, trimmed.Length, text.Substring(start, trimmed.Length)) { Amount = answer.Value, Value = answer.Shown, Unit = answer.Unit };
    }

    // ---------- dates and times ----------

    private static readonly string[] MonthNames =
    [
        "january|jan|janvier|janv|enero|ene|ocak|januari|ژانویه|جنوری|يناير|كانون الثاني",
        "february|feb|février|fevrier|févr|febrero|şubat|subat|februari|فوریه|فروری|فبراير|شباط",
        "march|mar|mars|marzo|mart|maret|مارس|مارچ|آذار|اذار",
        "april|apr|avril|abril|abr|nisan|آوریل|اپریل|أبريل|ابريل|إبريل|نيسان",
        "may|mai|mayo|mayıs|mayis|mei|مه|مئی|مايو|أيار|ايار",
        "june|jun|juin|junio|haziran|juni|ژوئن|جون|يونيو|يونيه|حزيران",
        "july|jul|juillet|juil|julio|temmuz|juli|ژوئیه|جولائی|يوليو|يوليه|تموز",
        "august|aug|août|aout|agosto|ağustos|agustos|agustus|اوت|اگست|أغسطس|اغسطس|آب",
        "september|sep|sept|septembre|septiembre|eylül|eylul|سپتامبر|ستمبر|سبتمبر|أيلول|ايلول",
        "october|oct|octobre|octubre|ekim|oktober|اکتبر|اکتوبر|أكتوبر|اكتوبر|تشرين الأول|تشرين الاول",
        "november|nov|novembre|noviembre|kasım|kasim|نوامبر|نومبر|نوفمبر|تشرين الثاني",
        "december|dec|décembre|decembre|déc|diciembre|dic|aralık|aralik|desember|دسامبر|دسمبر|ديسمبر|كانون الأول|كانون الاول",
    ];

    private static readonly Dictionary<string, int> MonthOf = MonthNames
        .SelectMany((names, i) => names.Split('|').Select(n => (n, i + 1)))
        .GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);

    private static readonly string MonthAlternation = string.Join("|", MonthOf.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

    private static readonly Regex DayMonth = new(
        @"(?<![\w.])(?<d>\d{1,2})(?:st|nd|rd|th|er|º|°)?(?:\s+(?:of|de))?\s+(?<mon>" + MonthAlternation + @")\.?(?:,?\s+(?:de\s+)?(?<y>\d{4}))?(?![\w])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MonthDay = new(
        @"(?<![\w])(?<mon>" + MonthAlternation + @")\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y>\d{4}))?(?![\w.]\d|[\w])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Time zones by the names texts give them: a fixed offset in hours, or a Windows zone whose offset changes.</summary>
    private static readonly Dictionary<string, (double? Hours, string? Zone)> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = (0, null), ["GMT"] = (0, null), ["Z"] = (0, null),
        ["EST"] = (-5, null), ["EDT"] = (-4, null), ["CST"] = (-6, null), ["CDT"] = (-5, null),
        ["MST"] = (-7, null), ["MDT"] = (-6, null), ["PST"] = (-8, null), ["PDT"] = (-7, null),
        ["AKST"] = (-9, null), ["AKDT"] = (-8, null), ["HST"] = (-10, null),
        ["ET"] = (null, "Eastern Standard Time"), ["CT"] = (null, "Central Standard Time"),
        ["MT"] = (null, "Mountain Standard Time"), ["PT"] = (null, "Pacific Standard Time"),
        ["BST"] = (1, null), ["WET"] = (0, null), ["WEST"] = (1, null), ["CET"] = (1, null), ["CEST"] = (2, null),
        ["EET"] = (2, null), ["EEST"] = (3, null), ["MSK"] = (3, null), ["TRT"] = (3, null),
        ["GST"] = (4, null), ["PKT"] = (5, null), ["IST"] = (5.5, null), ["SGT"] = (8, null), ["HKT"] = (8, null),
        ["JST"] = (9, null), ["KST"] = (9, null), ["AWST"] = (8, null), ["AEST"] = (10, null), ["AEDT"] = (11, null),
        ["NZST"] = (12, null), ["NZDT"] = (13, null),
        // "بتوقيت ...": the cities and countries Arabic texts name.
        ["مكة"] = (3, null), ["مكة المكرمة"] = (3, null), ["السعودية"] = (3, null), ["الرياض"] = (3, null), ["جدة"] = (3, null),
        ["الكويت"] = (3, null), ["قطر"] = (3, null), ["الدوحة"] = (3, null), ["البحرين"] = (3, null), ["المنامة"] = (3, null),
        ["بغداد"] = (3, null), ["العراق"] = (3, null), ["الأردن"] = (3, null), ["عمّان"] = (3, null), ["دمشق"] = (3, null), ["سوريا"] = (3, null),
        ["إسطنبول"] = (3, null), ["اسطنبول"] = (3, null), ["تركيا"] = (3, null),
        ["دبي"] = (4, null), ["الإمارات"] = (4, null), ["الامارات"] = (4, null), ["أبوظبي"] = (4, null), ["ابوظبي"] = (4, null),
        ["مسقط"] = (4, null), ["عُمان"] = (4, null), ["سلطنة عمان"] = (4, null),
        ["غرينتش"] = (0, null), ["جرينتش"] = (0, null),
        ["القاهرة"] = (null, "Egypt Standard Time"), ["مصر"] = (null, "Egypt Standard Time"),
        ["بيروت"] = (null, "Middle East Standard Time"), ["لبنان"] = (null, "Middle East Standard Time"),
        ["المغرب"] = (null, "Morocco Standard Time"), ["الرباط"] = (null, "Morocco Standard Time"), ["الدار البيضاء"] = (null, "Morocco Standard Time"),
        ["تونس"] = (1, null), ["الجزائر"] = (1, null),
    };

    private static readonly Regex ZoneAfter = new(
        @"\G\s?(?:\(\s?)?(?:(?<tz>UTC|GMT|EST|EDT|CST|CDT|MST|MDT|PST|PDT|AKST|AKDT|HST|ET|CT|MT|PT|BST|WEST|WET|CEST|CET|EEST|EET|MSK|TRT|GST|PKT|IST|SGT|HKT|JST|KST|AWST|AEST|AEDT|NZST|NZDT)(?<off>\s?[+-]\s?\d{1,2}(?::?\d{2})?)?|(?:بتوقيت|بتوقيت مدينة|حسب توقيت)\s+(?<city>"
        + "مكة المكرمة|مكة|السعودية|الرياض|جدة|الكويت|قطر|الدوحة|البحرين|المنامة|بغداد|العراق|الأردن|عمّان|دمشق|سوريا|إسطنبول|اسطنبول|تركيا|دبي|الإمارات|الامارات|أبوظبي|ابوظبي|مسقط|سلطنة عمان|عُمان|غرينتش|جرينتش|القاهرة|مصر|بيروت|لبنان|المغرب|الرباط|الدار البيضاء|تونس|الجزائر"
        + @")(?:\s?(?<off2>[+-]\s?\d{1,2}))?)(?:\s?\))?(?![\w])",
        // Zone names count in capitals only: "et" and "pt" are words in French and Portuguese.
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record Piece(int Start, int End, DateTime? Date, (int Hour, int Minute)? Time, TimeSpan? Offset, string? Zone);

    private static IEnumerable<Found> Moments(string plain, string text, DateTime now, CultureInfo culture)
    {
        var dates = new List<Piece>();
        void Date(Match m, int year, int month, int day, bool yearGiven)
        {
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month))
                return;
            var date = new DateTime(year, month, day);
            // Without a year: the next time the day comes (a week back still counts as this one).
            if (!yearGiven && date < now.Date.AddDays(-7))
                date = date.AddYears(1);
            if (!dates.Any(d => m.Index < d.End && d.Start < m.Index + m.Length))
                dates.Add(new Piece(m.Index, m.Index + m.Length, date, null, null, null));
        }
        foreach (Match m in IsoDate().Matches(plain))
            Date(m, Int(m, "y"), Int(m, "m"), Int(m, "d"), true);
        foreach (Match m in DayMonth.Matches(plain))
            Date(m, m.Groups["y"].Success ? Int(m, "y") : now.Year, MonthOf[m.Groups["mon"].Value], Int(m, "d"), m.Groups["y"].Success);
        foreach (Match m in MonthDay.Matches(plain))
            Date(m, m.Groups["y"].Success ? Int(m, "y") : now.Year, MonthOf[m.Groups["mon"].Value], Int(m, "d"), m.Groups["y"].Success);
        foreach (Match m in NumericDate().Matches(plain))
        {
            int a = Int(m, "a"), b = Int(m, "b"), year = Int(m, "y");
            if (m.Groups["y"].Value.Length == 2)
                year += year < 70 ? 2000 : 1900;
            bool monthFirst = culture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('M');
            if (a > 12) monthFirst = false;
            else if (b > 12) monthFirst = true;
            Date(m, year, monthFirst ? a : b, monthFirst ? b : a, true);
        }

        var times = new List<Piece>();
        foreach (Match m in Time12().Matches(plain))
        {
            int hour = Int(m, "h"), minute = m.Groups["mi"].Success ? Int(m, "mi") : 0;
            var ap = m.Groups["ap"].Value.ToLowerInvariant().Replace(".", "");
            bool arabicMark = ap is "ص" or "م";
            // "5 م" is as likely five metres: the Arabic marks count only with the minutes.
            if (hour is < 1 or > 12 || (arabicMark && !m.Groups["mi"].Success))
                continue;
            bool pm = ap.StartsWith('p') || ap.StartsWith('م');
            hour = hour % 12 + (pm ? 12 : 0);
            times.Add(WithZone(plain, m.Index, m.Index + m.Length, hour, minute));
        }
        foreach (Match m in Time24().Matches(plain))
        {
            if (times.Any(t => m.Index < t.End && t.Start < m.Index + m.Length))
                continue;
            times.Add(WithZone(plain, m.Index, m.Index + m.Length, Int(m, "h"), Int(m, "mi")));
        }

        var used = new HashSet<Piece>();
        foreach (var date in dates)
        {
            // The time that goes with the date: right after it ("at 3 PM"), or right before it ("3 PM on 5 Nov").
            var time = times.FirstOrDefault(t => !used.Contains(t) && t.Start >= date.End && t.Start - date.End <= 24 && Joins(plain[date.End..t.Start]))
                ?? times.FirstOrDefault(t => !used.Contains(t) && t.End <= date.Start && date.Start - t.End <= 12 && Joins(plain[t.End..date.Start]));
            if (time is not null)
            {
                used.Add(time);
                int start = Math.Min(date.Start, time.Start), end = Math.Max(date.End, time.End);
                var when = date.Date!.Value.AddHours(time.Time!.Value.Hour).AddMinutes(time.Time.Value.Minute);
                yield return new Found(FoundKind.When, start, end - start, text[start..end]) { When = when, HasDate = true, HasTime = true, Offset = time.Offset, Zone = time.Zone };
            }
            else
                yield return new Found(FoundKind.When, date.Start, date.End - date.Start, text[date.Start..date.End]) { When = date.Date, HasDate = true };
        }
        foreach (var time in times.Where(t => !used.Contains(t)))
        {
            // A time alone is worth something only with its zone: the moment in the reader's own time.
            if (time.Offset is null && time.Zone is null)
                continue;
            var when = now.Date.AddHours(time.Time!.Value.Hour).AddMinutes(time.Time.Value.Minute);
            yield return new Found(FoundKind.When, time.Start, time.End - time.Start, text[time.Start..time.End]) { When = when, HasTime = true, Offset = time.Offset, Zone = time.Zone };
        }
    }

    /// <summary>A time, and the zone written right after it when there is one.</summary>
    private static Piece WithZone(string plain, int start, int end, int hour, int minute)
    {
        var zone = ZoneAfter.Match(plain, end);
        if (!zone.Success)
            return new Piece(start, end, null, (hour, minute), null, null);
        var name = zone.Groups["tz"].Success ? zone.Groups["tz"].Value : zone.Groups["city"].Value;
        var (hours, id) = Zones[name];
        var extra = zone.Groups["off"].Success ? zone.Groups["off"].Value : zone.Groups["off2"].Success ? zone.Groups["off2"].Value : null;
        TimeSpan? offset = hours is { } h ? TimeSpan.FromHours(h) : null;
        if (extra is not null)
        {
            // "GMT+3", "UTC+05:30": the offset written is the zone.
            var digits = extra.Replace(" ", "");
            int sign = digits[0] == '-' ? -1 : 1;
            var parts = digits[1..].Split(':');
            int hh = int.Parse(parts[0].Length > 2 ? parts[0][..^2] : parts[0], CultureInfo.InvariantCulture);
            int mm = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : parts[0].Length > 2 ? int.Parse(parts[0][^2..], CultureInfo.InvariantCulture) : 0;
            if (hh > 14 || mm > 59)
                return new Piece(start, end, null, (hour, minute), null, null);
            offset = (offset ?? TimeSpan.Zero) + TimeSpan.FromMinutes(sign * (hh * 60 + mm));
            id = null;
        }
        return new Piece(start, zone.Index + zone.Length, null, (hour, minute), offset, id);
    }

    /// <summary>What may stand between a date and its time: spaces, commas and "at".</summary>
    private static bool Joins(string between) => JoinWords().IsMatch(between);

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>A Windows time zone by id, or null when this PC doesn't know it.</summary>
    internal static TimeZoneInfo? Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<![\w@/.-])(?:(?:https?://|www\.)[^\s<>""'«»]+|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|net|org|io|dev|app|ai|edu|gov|info|me|co|sa|ae|eg|qa|kw|bh|om|jo|ma|tn|dz|iq|lb|tr|pk|id|ir|uk|de|fr|es|us|ca|au|in)(?:/[^\s<>""'«»]*)?)(?<![.,;:!?\]}'""»])", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}(?![\w-])")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w+.,/])(?:\+|00)?(?:\(\d{1,4}\)[ .-]?)?\d[\d .-]{5,18}\d(?![\w,/]|\.\d)")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^\d{1,4}[-/. ]\d{1,2}[-/. ]\d{1,4}$")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"^(?:\+|00)?\d{2,4}(?:\.\d{2,4}){2,4}$")]
    private static partial Regex DottedPhone();

    [GeneratedRegex(@"^(?:1[ .-])?\(?\d{3}\)?[ .-]?\d{3}[ .-]\d{4}$")]
    private static partial Regex UsGroups();

    [GeneratedRegex(@"(?<![\w&])#(?<hex>[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![\w])")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\brgba?\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*(?:,\s*[\d.]+%?\s*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColour();

    [GeneratedRegex(@"\bhsla?\(\s*(?<h>\d{1,3}(?:\.\d+)?)(?:deg)?\s*,\s*(?<s>\d{1,3}(?:\.\d+)?)%\s*,\s*(?<l>\d{1,3}(?:\.\d+)?)%\s*(?:,\s*[\d.]+%?\s*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex HslColour();

    [GeneratedRegex(@"\d\s*[+*/×÷^%]|[+*/×÷^]\s*\d|\d\s+-\s+\d|\(\s*\d|\b(?:sqrt|sin|cos|tan|log|ln|of|من)\b|√", RegexOptions.IgnoreCase)]
    private static partial Regex SumSign();

    [GeneratedRegex(@"\s(?:in|to|into|as|=|إلى|الى)\s|\sب", RegexOptions.IgnoreCase)]
    private static partial Regex ConvertWord();

    [GeneratedRegex(@"(?<![\w.:/-])(?<y>\d{4})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})(?![\w:/-]|\.\d)")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\w.:/-])(?<a>\d{1,2})[/.-](?<b>\d{1,2})[/.-](?<y>\d{4}|\d{2})(?![\w:/-]|\.\d)")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<![\w:.,])(?<h>\d{1,2})(?:[:.](?<mi>[0-5]\d))?\s?(?<ap>a\.?m\.?|p\.?m\.?|صباحًا|صباحا|مساءً|مساء|ص|م)(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex Time12();

    [GeneratedRegex(@"(?<![\w:.,])(?<h>[01]?\d|2[0-3]):(?<mi>[0-5]\d)(?![\d:])(?!\.\d)")]
    private static partial Regex Time24();

    [GeneratedRegex(@"^[\s,،@–—-]*(?:(?:at|on|à|le|a las|el|alle|um|saat|pukul|jam|الساعة|في|يوم|ساعت|بجے)[\s,،@–—-]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex JoinWords();
}
