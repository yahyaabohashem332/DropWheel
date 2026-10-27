using System.Globalization;
using System.Text;

namespace Petal.Core.Text;

/// <summary>
/// An event as an iCalendar (.ics) file, which Outlook, the Windows calendar and every calendar app open to add it. A
/// moment whose zone is known is written in UTC; one without a zone keeps its own clock (a "floating" time).
/// </summary>
public static class CalendarFile
{
    /// <param name="start">The start, by the clock of <paramref name="utc"/> when given, otherwise as written.</param>
    /// <param name="utc">The start in UTC, when the zone was known.</param>
    /// <param name="allDay">A date with no time: an event for the whole day.</param>
    public static string Event(string title, DateTime start, DateTime? utc, bool allDay, TimeSpan duration, string? details, DateTime stamp)
    {
        var sb = new StringBuilder();
        void Line(string text)
        {
            // Lines longer than 75 bytes are folded: a line break and a space, never inside a character.
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= 75)
            {
                sb.Append(text).Append("\r\n");
                return;
            }
            int taken = 0, limit = 75;
            var piece = new StringBuilder();
            foreach (var rune in text.EnumerateRunes())
            {
                int size = rune.Utf8SequenceLength;
                if (taken + size > limit)
                {
                    sb.Append(piece).Append("\r\n ");
                    piece.Clear();
                    taken = 0;
                    limit = 74;
                }
                piece.Append(rune.ToString());
                taken += size;
            }
            sb.Append(piece).Append("\r\n");
        }
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//DropWheel//Circle Anything//EN");
        Line("CALSCALE:GREGORIAN");
        Line("BEGIN:VEVENT");
        Line("UID:" + Guid.NewGuid().ToString("N") + "@dropwheel");
        Line("DTSTAMP:" + stamp.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        if (allDay)
        {
            Line("DTSTART;VALUE=DATE:" + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line("DTEND;VALUE=DATE:" + start.AddDays(Math.Max(1, (int)Math.Round(duration.TotalDays))).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
        else if (utc is { } u)
        {
            Line("DTSTART:" + u.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            Line("DTEND:" + u.Add(duration).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        }
        else
        {
            Line("DTSTART:" + start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            Line("DTEND:" + start.Add(duration).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        }
        Line("SUMMARY:" + Escape(title));
        if (!string.IsNullOrWhiteSpace(details))
            Line("DESCRIPTION:" + Escape(details));
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>Text as iCalendar keeps it: backslashes, semicolons and commas escaped, line breaks as \n.</summary>
    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
}
