using Petal.Core.Formats;
using Petal.Core.Jobs;
using Xunit.Abstractions;

namespace Petal.Tests;

public class EngineCoverageTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_conversion_in_the_matrix_has_an_engine()
    {
        var registry = Petal.App.EngineSetup.CreateConverters();
        var missing = ConversionMatrix.AllPaths().Where(p => !registry.CanConvert(p.From, p.To)).ToList();
        foreach (var (from, to) in missing)
            output.WriteLine($"no engine: {from} → {to}");
        // Slides need PowerPoint or LibreOffice; HEIC output needs an HEVC encoder (graphics card or Windows HEVC extension); everything else is built in.
        bool canHeic = registry.CanConvert(Formats.Jpg, Formats.Heic);
        Assert.All(missing, p => Assert.True(p.From.Family == FormatFamily.Slides || (!canHeic && p.To == Formats.Heic)));
        output.WriteLine($"{ConversionMatrix.TotalPaths - missing.Count} of {ConversionMatrix.TotalPaths} paths available on this PC");
    }
}
