namespace Petal.Engines.Imaging;

/// <summary>
/// What countries ask of photos, checked against their own offices' pages in November 2026. Only what an office states
/// is here: where one gives no size at all (a Saudi passport, a Sudanese one, a Qatari visa) there is no entry, and
/// <see cref="Custom"/> takes any size. Where an office gives no head size, it is the usual one for the size of photo.
/// </summary>
public static class IdPhotoCatalog
{
    private const uint White = 0xFFFFFF;
    private const uint LightGrey = 0xEDEDED;
    private const uint LightBlue = 0xBFD8F2;

    /// <summary>The international size most offices take: ICAO's.</summary>
    public static readonly IdPhotoSpec Default = new("icao", "International (ICAO)", "", 35, 45, 32, 36);

    /// <summary>For anything else: the size is chosen by hand and the head follows it.</summary>
    public static readonly IdPhotoSpec Custom = new("custom", "Custom size", "", 35, 45, 31.5, 36, White, HeadStated: false);

    public static readonly IReadOnlyList<IdPhotoSpec> All =
    [
        Default,
        Custom,
        new("tr-passport", "Turkey", "Passport", 50, 60, 32, 36),
        new("tr-residence", "Turkey", "Residence permit", 50, 60, 32, 36, White, HeadStated: false),
        new("ae", "United Arab Emirates", "", 35, 45, 31.5, 36),
        new("eg-passport", "Egypt", "Passport", 40, 60, 32, 36, White, HeadStated: false),
        new("eg-visa", "Egypt", "Visa", 50.8, 50.8, 25, 35, White, HeadStated: false),
        new("jo", "Jordan", "Passport", 35, 45, 32, 36, White, HeadStated: false),
        new("iq-visa", "Iraq", "Visa", 50.8, 50.8, 25, 35, White, HeadStated: false),
        new("sy", "Syria", "Passport", 40, 40, 26, 31, White, HeadStated: false),
        new("lb", "Lebanon", "Passport", 35, 45, 32, 36, White, HeadStated: false),
        new("kw", "Kuwait", "Passport", 40, 50, 32, 36, LightBlue, HeadStated: false),
        new("bh", "Bahrain", "Passport", 40, 60, 32, 36, White, HeadStated: false),
        new("om-visa", "Oman", "Visa", 40, 60, 32, 36, White, HeadStated: false),
        new("ye", "Yemen", "Passport", 40, 60, 32, 36),
        new("ly-visa", "Libya", "Visa", 50.8, 50.8, 25, 35, White, HeadStated: false),
        new("ma", "Morocco", "Passport", 35, 45, 32, 36, White, HeadStated: false),
        new("dz", "Algeria", "Passport", 35, 45, 32, 36, White, WithoutHair: true),
        new("tn", "Tunisia", "Passport", 35, 45, 32, 36, White, HeadStated: false),
        new("ps", "Palestine", "Passport", 35, 45, 32, 36, LightBlue, HeadStated: false),
        new("ir-visa", "Iran", "Visa", 30, 40, 28, 32),
        new("pk-visa", "Pakistan", "Visa", 35, 45, 31.5, 36),
        new("in-passport", "India", "Passport", 35, 45, 36, 38),
        new("in-visa", "India", "Visa", 50.8, 50.8, 25, 35),
        new("id-visa", "Indonesia", "Visa", 40, 60, 30, 36),
        new("my-passport", "Malaysia", "Passport", 35, 50, 25, 30),
        new("my-visa", "Malaysia", "Visa", 35, 50, 30, 35),
        new("us", "United States", "Passport", 50.8, 50.8, 25, 35),
        new("ca-passport", "Canada", "Passport", 50, 70, 31, 36),
        new("ca-visa", "Canada", "Visa", 35, 45, 31, 36),
        new("gb", "United Kingdom", "Passport", 35, 45, 29, 34, LightGrey),
        new("schengen", "Schengen area", "Visa", 35, 45, 32, 36, LightGrey),
        new("de", "Germany", "", 35, 45, 32, 36, LightGrey),
        new("fr", "France", "", 35, 45, 32, 36, LightGrey, WithoutHair: true),
        new("es", "Spain", "Passport", 35, 45, 23, 30, White, HeadStated: false),
        new("au", "Australia", "Passport", 35, 45, 32, 36),
        new("cn-visa", "China", "Visa", 33, 48, 28, 33),
        new("jp-passport", "Japan", "Passport", 35, 45, 32, 36),
        new("jp-visa", "Japan", "Visa", 45, 45, 28, 34, White, HeadStated: false),
        new("ru-visa", "Russia", "Visa", 35, 45, 31.5, 36),
        new("ru-passport", "Russia", "Passport", 35, 45, 31.5, 36),
    ];

    /// <summary>A size chosen by hand, with the head the usual share of it.</summary>
    public static IdPhotoSpec Sized(double widthMm, double heightMm) =>
        Custom with { WidthMm = widthMm, HeightMm = heightMm, HeadMinMm = Math.Round(heightMm * 0.7, 1), HeadMaxMm = Math.Round(heightMm * 0.8, 1) };
}
