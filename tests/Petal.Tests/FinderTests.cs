using System.Globalization;
using Petal.Core.Text;

namespace Petal.Tests;

/// <summary>What Circle Anything finds in the text it reads: links, numbers to call, moments, money, measurements, colours and sums.</summary>
public sealed class FinderTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 10, 0, 0);

    private static IReadOnlyList<Found> Find(string text, string culture = "en-GB", string? home = "SAR") =>
        Finder.Find(text, Now, new CultureInfo(culture), home);

    private static Found One(string text, FoundKind kind, string culture = "en-GB", string? home = "SAR")
    {
        var found = Find(text, culture, home).Where(f => f.Kind == kind).ToList();
        Assert.True(found.Count == 1, $"{kind} in \"{text}\": {string.Join(", ", Find(text, culture, home).Select(f => $"{f.Kind} '{f.Text}'"))}");
        return found[0];
    }

    [Theory]
    [InlineData("Visit https://dropwheel.app/download today.", "https://dropwheel.app/download", "https://dropwheel.app/download")]
    [InlineData("See www.example.com, then", "www.example.com", "https://www.example.com")]
    [InlineData("on example.org/docs/page?id=4", "example.org/docs/page?id=4", "https://example.org/docs/page?id=4")]
    [InlineData("الموقع: dropwheel.app", "dropwheel.app", "https://dropwheel.app")]
    [InlineData("(https://en.wikipedia.org/wiki/Petal_(botany))", "https://en.wikipedia.org/wiki/Petal_(botany)", "https://en.wikipedia.org/wiki/Petal_(botany)")]
    public void Links_are_found_with_their_address(string text, string shown, string address)
    {
        var link = One(text, FoundKind.Link);
        Assert.Equal(shown, link.Text);
        Assert.Equal(address, link.Value);
    }

    [Theory]
    [InlineData("Saved as report.pdf and notes.txt")]
    [InlineData("e.g. this one")]
    [InlineData("Version 10.0.19045")]
    public void Names_that_only_look_like_links_are_not(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Link);

    [Fact]
    public void An_email_address_is_one_thing_not_a_link()
    {
        var found = Find("Write to support@dropwheel.app.");
        var email = Assert.Single(found);
        Assert.Equal(FoundKind.Email, email.Kind);
        Assert.Equal("support@dropwheel.app", email.Value);
    }

    [Theory]
    [InlineData("Call +966 55 123 4567 now", "+966551234567")]
    [InlineData("جوال: 055 123 4567", "0551234567")]
    [InlineData("جوال: ٠٥٥ ١٢٣ ٤٥٦٧", "0551234567")]
    [InlineData("(555) 123-4567", "5551234567")]
    [InlineData("1-800-555-0199", "18005550199")]
    [InlineData("00971 4 123 4567", "+97141234567")]
    [InlineData("555-123-4567", "5551234567")]
    [InlineData("Tel 01001234567", "01001234567")]
    public void Phone_numbers_are_found_ready_to_dial(string text, string dial) =>
        Assert.Equal(dial, One(text, FoundKind.Phone).Value);

    [Theory]
    [InlineData("Years 2026 1105")]
    [InlineData("Total 1,299.99")]
    [InlineData("Version 10.0.19045.4529")]
    [InlineData("pi is 3.14159265")]
    [InlineData("2020 - 2026")]
    public void Other_numbers_are_not_phone_numbers(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Phone);

    [Fact]
    public void A_date_and_its_time_and_zone_are_one_moment()
    {
        var when = One("The launch is on Nov 5, 2026 at 3:00 PM PST.", FoundKind.When);
        Assert.Equal("Nov 5, 2026 at 3:00 PM PST", when.Text);
        Assert.Equal(new DateTime(2026, 11, 5, 15, 0, 0), when.When);
        Assert.True(when.HasDate && when.HasTime);
        Assert.Equal(TimeSpan.FromHours(-8), when.Offset);
        Assert.Equal(new DateTime(2026, 11, 5, 23, 0, 0, DateTimeKind.Utc), when.Utc);
    }

    [Theory]
    [InlineData("5 November", 2026, 11, 5)]
    [InlineData("November 5th", 2026, 11, 5)]
    [InlineData("5th of November 2027", 2027, 11, 5)]
    [InlineData("Oct 25", 2026, 10, 25)]
    [InlineData("March 3", 2027, 3, 3)]
    [InlineData("٥ نوفمبر ٢٠٢٦", 2026, 11, 5)]
    [InlineData("12 كانون الأول", 2026, 12, 12)]
    [InlineData("le 14 juillet 2027", 2027, 7, 14)]
    [InlineData("2026-12-31", 2026, 12, 31)]
    [InlineData("25/12/2026", 2026, 12, 25)]
    [InlineData("29 Ekim 2026", 2026, 10, 29)]
    public void Dates_are_read_in_many_languages(string text, int year, int month, int day)
    {
        var when = One(text, FoundKind.When);
        Assert.Equal(new DateTime(year, month, day), when.When);
        Assert.True(when.HasDate);
        Assert.False(when.HasTime);
    }

    [Theory]
    [InlineData("en-GB", 11, 5)]
    [InlineData("ar-SA", 11, 5)]
    [InlineData("en-US", 5, 11)]
    public void A_numeric_date_follows_the_readers_order(string culture, int month, int day) =>
        Assert.Equal(new DateTime(2026, month, day), One("05/11/2026", FoundKind.When, culture).When);

    [Fact]
    public void An_arabic_date_and_time_are_read_with_the_evening_mark()
    {
        var when = One("الموعد ٥ نوفمبر ٢٠٢٦ الساعة ٣:٣٠ م", FoundKind.When);
        Assert.Equal(new DateTime(2026, 11, 5, 15, 30, 0), when.When);
        Assert.True(when.HasTime);
    }

    [Theory]
    [InlineData("Live at 3 PM ET", null, "Eastern Standard Time")]
    [InlineData("starts 9:00 AM GMT+3", 3.0, null)]
    [InlineData("at 18:30 UTC+05:30", 5.5, null)]
    [InlineData("المباراة ١٠:٣٠ م بتوقيت مكة", 3.0, null)]
    [InlineData("الساعة 8:00 م بتوقيت القاهرة", null, "Egypt Standard Time")]
    public void A_time_with_its_zone_is_a_moment(string text, double? hours, string? zone)
    {
        var when = One(text, FoundKind.When);
        Assert.True(when.HasTime);
        Assert.Equal(hours is { } h ? TimeSpan.FromHours(h) : null, when.Offset);
        Assert.Equal(zone, when.Zone);
        Assert.NotNull(when.Utc);
    }

    [Theory]
    [InlineData("de 15:30 et 16:00")]
    [InlineData("score 3:2")]
    [InlineData("chapter 5 may apply")]
    [InlineData("طوله 5 م")]
    public void Times_without_a_zone_and_lookalikes_are_not_moments(string text)
    {
        foreach (var f in Find(text).Where(f => f.Kind == FoundKind.When))
            Assert.True(f.HasDate, $"'{f.Text}' read as a moment in \"{text}\"");
        Assert.DoesNotContain(Find("de 15:30 et 16:00"), f => f.Kind == FoundKind.When);
    }

    [Fact]
    public void Times_in_new_york_are_one_offset_in_winter_and_another_in_summer()
    {
        var winter = Finder.Find("Jan 10, 2027 at 3 PM ET", Now, new CultureInfo("en-US"), "USD").Single(f => f.Kind == FoundKind.When);
        var summer = Finder.Find("Jul 10, 2027 at 3 PM ET", Now, new CultureInfo("en-US"), "USD").Single(f => f.Kind == FoundKind.When);
        Assert.Equal(new DateTime(2027, 1, 10, 20, 0, 0), winter.Utc);
        Assert.Equal(new DateTime(2027, 7, 10, 19, 0, 0), summer.Utc);
    }

    [Theory]
    [InlineData("Only $1,299.99 today", 1299.99, "USD")]
    [InlineData("€ 45", 45, "EUR")]
    [InlineData("1.299,99 €", 1299.99, "EUR")]
    [InlineData("45€", 45, "EUR")]
    [InlineData("250 SAR", 250, "SAR")]
    [InlineData("السعر ١٥٠ ريال", 150, "SAR")]
    [InlineData("بقيمة 3.5 مليون جنيه", 3_500_000, "EGP")]
    [InlineData("raised $5m", 5_000_000, "USD")]
    [InlineData("£12.50", 12.5, "GBP")]
    [InlineData("1,500 د.إ", 1500, "AED")]
    [InlineData("750 دينار كويتي", 750, "KWD")]
    [InlineData("₺1.250", 1250, "TRY")]
    public void Money_is_read_with_its_currency(string text, double amount, string code)
    {
        var money = One(text, FoundKind.Money);
        Assert.Equal(amount, money.Amount, 6);
        Assert.Equal(code, money.Unit);
    }

    [Fact]
    public void A_shared_name_means_the_readers_own_currency()
    {
        Assert.Equal("SAR", One("١٥٠ ريال", FoundKind.Money, home: "SAR").Unit);
        Assert.Equal("QAR", One("١٥٠ ريال", FoundKind.Money, home: "QAR").Unit);
        Assert.Equal("SAR", One("١٥٠ ريال", FoundKind.Money, home: "EUR").Unit);
        Assert.Equal("JOD", One("20 دينار", FoundKind.Money, home: "JOD").Unit);
    }

    [Theory]
    [InlineData("le 5 juin")]
    [InlineData("I won 5 games")]
    [InlineData("Room 12")]
    public void Words_are_not_currencies(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Money);

    [Theory]
    [InlineData("a 5 km run", 5, "km")]
    [InlineData("weighs 70 kg", 70, "kg")]
    [InlineData("It's 45°F outside", 45, "°F")]
    [InlineData("-5 °C tonight", -5, "°C")]
    [InlineData("He is 5′11″", 71, "in")]
    [InlineData("السرعة ١٢٠ كم/ساعة", 120, "km/h")]
    [InlineData("مساحتها 3 أفدنة", 3, "feddan")]
    [InlineData("5 m long", 5, "m")]
    [InlineData("500g of flour", 500, "g")]
    [InlineData("a 2 L bottle", 2, "l")]
    [InlineData("1,500 km away", 1500, "km")]
    [InlineData("1,5 kg", 1.5, "kg")]
    public void Measurements_are_read_with_their_unit(string text, double value, string unit)
    {
        var measure = One(text, FoundKind.Measure);
        Assert.Equal(value, measure.Amount, 6);
        Assert.Equal(unit, measure.Unit);
    }

    [Theory]
    [InlineData("posted 5m ago")]
    [InlineData("a 5G phone")]
    [InlineData("in 5 minutes")]
    [InlineData("2026 م")]
    public void Lookalikes_are_not_measurements(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Measure);

    [Theory]
    [InlineData(5, "km", "3.11 mi")]
    [InlineData(45, "°F", "7.2 °C")]
    [InlineData(100, "°C", "212 °F")]
    [InlineData(70, "kg", "154.3 lb")]
    [InlineData(1, "mi²", "2.59 km²")]
    [InlineData(3, "feddan", "12,602 m²")]
    [InlineData(2, "l", "0.53 gal")]
    [InlineData(120, "km/h", "74.6 mph")]
    public void Measurements_go_into_the_other_system(double value, string unit, string first) =>
        Assert.Equal(first, Calculator.ForReader(value, unit)[0].Text);

    [Fact]
    public void A_height_is_also_given_in_feet_and_inches()
    {
        var answers = Calculator.ForReader(180, "cm");
        Assert.Equal("70.9 in", answers[0].Text);
        Assert.Equal("5′ 10.9″", answers[1].Text);
        Assert.Equal("5′ 11″", Calculator.ForReader(71, "in")[1].Text);
    }

    [Theory]
    [InlineData("#fff", "#FFFFFF")]
    [InlineData("color: #1a2b3c;", "#1A2B3C")]
    [InlineData("#7C5CFC80", "#7C5CFC")]
    [InlineData("rgb(255, 0, 128)", "#FF0080")]
    [InlineData("hsl(210, 50%, 40%)", "#336699")]
    public void Colours_are_read_as_hex(string text, string hex) =>
        Assert.Equal(hex, One(text, FoundKind.Colour).Value);

    [Theory]
    [InlineData("Fixed in #123")]
    [InlineData("#hashtag")]
    public void Issue_numbers_and_tags_are_not_colours(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Colour);

    [Theory]
    [InlineData("12 × (3 + 4)", 84)]
    [InlineData("15% of 240", 36)]
    [InlineData("١٢٠٠ / ٤", 300)]
    [InlineData("2^10", 1024)]
    public void A_sum_is_worked_out(string text, double answer) =>
        Assert.Equal(answer, One(text, FoundKind.Sum).Amount, 6);

    [Fact]
    public void A_conversion_typed_out_is_a_sum_with_its_unit()
    {
        var sum = One("5 km in mi", FoundKind.Sum);
        Assert.Equal("mi", sum.Unit);
        Assert.Equal(3.10686, sum.Amount, 4);
    }

    [Theory]
    [InlineData("2026-11-05")]
    [InlineData("055 123 4567")]
    [InlineData("1-800-555-0199")]
    [InlineData("12")]
    [InlineData("v2.5")]
    public void Dates_numbers_and_versions_are_not_sums(string text) =>
        Assert.DoesNotContain(Find(text), f => f.Kind == FoundKind.Sum);

    [Fact]
    public void A_busy_text_gives_each_thing_once()
    {
        var found = Find("Meet on Nov 5 at 3 PM, call +966 55 123 4567 or write to team@dropwheel.app — tickets $25.");
        Assert.Equal([FoundKind.When, FoundKind.Phone, FoundKind.Email, FoundKind.Money], found.Select(f => f.Kind));
        Assert.Equal(new DateTime(2026, 11, 5, 15, 0, 0), found[0].When);
    }

    [Fact]
    public void Money_is_written_with_its_currency_decimals()
    {
        Assert.Equal("1,299.99 USD", Money.Format(1299.99, "USD"));
        Assert.Equal("4,871.250 KWD", Money.Format(4871.25, "KWD"));
        Assert.Equal("150 SAR", Money.Format(150, "SAR"));
        Assert.Equal("45.00 EUR", Money.Format(45, "EUR"));
        var rates = new ExchangeRates(Now, new Dictionary<string, double> { ["USD"] = 1, ["SAR"] = 3.75, ["EUR"] = 0.9 });
        Assert.Equal(375, rates.Convert(100, "USD", "SAR")!.Value, 6);
        Assert.Equal(24, rates.Convert(90, "SAR", "USD")!.Value * 1, 6);
        Assert.Null(rates.Convert(1, "USD", "XYZ"));
    }

    [Fact]
    public void An_event_file_opens_in_any_calendar()
    {
        var title = "Quarterly review, with the team; ready? " + new string('x', 60);
        var ics = CalendarFile.Event(title, new DateTime(2026, 11, 5, 15, 0, 0), new DateTime(2026, 11, 5, 23, 0, 0, DateTimeKind.Utc), false, TimeSpan.FromHours(1), "From the screen", Now);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains("\r\nDTSTART:20261105T230000Z\r\n", ics);
        Assert.Contains("\r\nDTEND:20261106T000000Z\r\n", ics);
        Assert.Contains("SUMMARY:Quarterly review\\, with the team\\; ready?", ics.Replace("\r\n ", ""));
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        var allDay = CalendarFile.Event("عيد", new DateTime(2026, 12, 25), null, true, TimeSpan.FromDays(1), null, Now);
        Assert.Contains("DTSTART;VALUE=DATE:20261225", allDay);
        Assert.Contains("DTEND;VALUE=DATE:20261226", allDay);
        var floating = CalendarFile.Event("Call", new DateTime(2026, 11, 5, 9, 30, 0), null, false, TimeSpan.FromMinutes(30), null, Now);
        Assert.Contains("DTSTART:20261105T093000\r\n", floating);
    }
}
