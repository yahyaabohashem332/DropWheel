using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Petal.App.Overlay;
using Petal.Core.Util;

namespace Petal.Tests;

/// <summary>
/// On a PC whose region writes 3.4 as "3,4" DropWheel didn't start: the wheel's drawing was built from text
/// with numbers in the region's format, WPF refused it, and start-up stopped before the tray icon and the
/// keys. Numbers are now written the same way everywhere, and the region keeps its dates, sorting and code page.
/// </summary>
public sealed class PlainNumbersTests
{
    // Comma decimals, Unicode minus signs, Arabic decimal separators, and plain English.
    public static TheoryData<string> Regions => ["de-DE", "fr-FR", "tr-TR", "id-ID", "es-ES", "ru-RU", "sv-SE", "fi-FI", "fa-IR", "ar-SA", "ar-EG", "en-US"];

    [Theory]
    [MemberData(nameof(Regions))]
    public void NumbersAreWrittenAndReadPlainly(string region)
    {
        var plain = PlainNumbers.Of(CultureInfo.GetCultureInfo(region));
        Assert.Equal("3.4", 3.4.ToString(plain));
        Assert.Equal("-1.5", (-1.5).ToString(plain));
        Assert.Equal("1,234.5", 1234.5.ToString("N1", plain));
        Assert.Equal(3.4, double.Parse("3.4", plain));
        Assert.Equal(-2.25, double.Parse("-2.25", plain));
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void TheRegionKeepsEverythingElse(string region)
    {
        var original = CultureInfo.GetCultureInfo(region);
        var plain = PlainNumbers.Of(original);
        Assert.Equal(original.Name, plain.Name);
        Assert.Equal(original.DateTimeFormat.ShortDatePattern, plain.DateTimeFormat.ShortDatePattern);
        Assert.Equal(original.DateTimeFormat.ShortTimePattern, plain.DateTimeFormat.ShortTimePattern);
        Assert.Equal(original.DateTimeFormat.MonthNames, plain.DateTimeFormat.MonthNames);
        Assert.Equal(original.TextInfo.ANSICodePage, plain.TextInfo.ANSICodePage);
        Assert.Equal(original.CompareInfo.Name, plain.CompareInfo.Name);
        Assert.Equal(original.NumberFormat.CurrencySymbol, plain.NumberFormat.CurrencySymbol);
        // The culture Windows gave is left as it was.
        Assert.NotSame(original.NumberFormat, plain.NumberFormat);
    }

    [Fact]
    public void AGeometryWrittenWithTheRegionsNumbersParses()
    {
        // The drawing that stopped start-up on a German PC: "M 8,3,4000000000000004 L 8,9,2" before.
        OnSta(() =>
        {
            var plain = PlainNumbers.Of(CultureInfo.GetCultureInfo("de-DE"));
            var geometry = Geometry.Parse(string.Format(plain, "M {0},{1} L {0},{2}", 8.0, 8 - 4.6, 8 + 1.2));
            Assert.Equal(5.8, geometry.Bounds.Height, 6);
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    [InlineData("fa-IR")]
    public void TheWheelIsBuiltInAnyRegion(string region)
    {
        // Exactly what start-up does, on a PC set to this region, before PlainNumbers is applied.
        OnSta(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
            using var sound = new FeedbackSound { Enabled = false };
            var wheel = new WheelWindow(sound);
            Assert.NotNull(wheel.Content);
        });
    }

    [Fact]
    public void NoDrawingIsParsedFromTextWithTheRegionsNumbers()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // An interpolated or formatted string handed to a WPF parser must say it is invariant.
                if (Regex.IsMatch(lines[i], @"\b(Geometry|PathGeometry|PathFigureCollection|PointCollection|KeySpline)\.Parse\((\$|string\.Format)"))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void StartUpMakesNumbersPlainFirst()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Petal.App", "App.xaml.cs"));
        int start = app.IndexOf("protected override void OnStartup(StartupEventArgs e)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int apply = app.IndexOf("PlainNumbers.Apply();", start, StringComparison.Ordinal);
        int baseCall = app.IndexOf("base.OnStartup(e);", start, StringComparison.Ordinal);
        Assert.True(apply > start && apply < baseCall, "PlainNumbers.Apply() must be the first thing OnStartup does.");
    }

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Petal's sources were not found above the test folder.");
    }
}
