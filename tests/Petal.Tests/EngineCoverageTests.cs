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
        // Slides need PowerPoint or LibreOffice; everything else is built in.
        Assert.All(missing, p => Assert.Equal(FormatFamily.Slides, p.From.Family));
        output.WriteLine($"{ConversionMatrix.TotalPaths - missing.Count} of {ConversionMatrix.TotalPaths} paths available on this PC");
    }
}
