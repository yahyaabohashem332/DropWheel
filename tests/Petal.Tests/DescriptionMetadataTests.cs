using Petal.Engines.Imaging;
using SkiaSharp;

namespace Petal.Tests;

/// <summary>A picture's description kept in its own details, in any language, without touching its pixels.</summary>
public sealed class DescriptionMetadataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-describe-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("A red kite over a green field.")]
    [InlineData("طائرة ورقية حمراء فوق حقل أخضر.")]
    public async Task A_description_is_written_and_read_back(string text)
    {
        var picture = Path.Combine(_dir, "kite.jpg");
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Green);
            using var stream = File.Create(picture);
            bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, 90);
        }
        var output = Path.Combine(_dir, "kite described.jpg");
        await ImageMetadata.ApplyAsync(picture, output, [new MetadataChange.Set(MetadataIds.ImageDescription, text)]);
        var report = await ImageMetadata.ReadAsync(output);
        Assert.Contains(report.Entries, e => e.Id == MetadataIds.ImageDescription && e.Value == text);
    }
}
