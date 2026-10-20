using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Petal.Engines.Media.Tests;

/// <summary>Changes the process's temp folder, so it never runs alongside other tests.</summary>
[CollectionDefinition("Temp folder", DisableParallelization = true)]
public class TempFolderCollection;

[Collection("Temp folder")]
public class WatermarkTests
{
    [Fact]
    public async Task Overlay_covers_its_corner_on_every_frame_and_keeps_the_audio()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "marked.mp4");
        (int W, int H) asked = default;
        await VideoTools.WatermarkAsync(input, output, new WatermarkVideoOptions((w, h) =>
        {
            asked = (w, h);
            return CornerPng(w, h, 80, 60);
        }));

        Assert.Equal((320, 240), asked);
        var info = await MediaAssert.ValidAsync(output, 5.0);
        Assert.True(info.HasAudio);
        foreach (var t in new[] { 0.5, 4.0 })
        {
            var frame = await Fixtures.RgbFrameAsync(output, t);
            var (r, g, b) = Pixel(frame, 20, 20);
            Assert.True(r > 235 && g > 235 && b > 235, $"white under the mark at {t}s, got {r},{g},{b}");
        }
        // Outside the mark the picture is the original.
        var original = await Fixtures.RgbFrameAsync(input, 2.0);
        var marked = await Fixtures.RgbFrameAsync(output, 2.0);
        Assert.True(MeanDifference(original, marked, (x, y) => x > 120 || y > 100) < 6);
    }

    [Fact]
    public async Task Works_when_the_temp_folder_has_awkward_characters()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var awkward = Directory.CreateDirectory(Path.Combine(Fixtures.NewDir(), "tmp [x],y;z's")).FullName;
        var previous = Environment.GetEnvironmentVariable("TMP");
        Environment.SetEnvironmentVariable("TMP", awkward);
        try
        {
            Assert.StartsWith(awkward, Path.GetTempPath().TrimEnd('\\'));
            var output = Path.Combine(Fixtures.NewDir(), "marked.mp4");
            await VideoTools.WatermarkAsync(input, output, new WatermarkVideoOptions((w, h) => CornerPng(w, h, 40, 40)));
            var (r, g, b) = Pixel(await Fixtures.RgbFrameAsync(output, 1.0), 10, 10);
            Assert.True(r > 235 && g > 235 && b > 235, $"got {r},{g},{b}");
            Assert.Empty(Directory.GetFiles(awkward, "petal-watermark-*"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", previous);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\A\x.png", @"C\\:/Users/A/x.png")]
    [InlineData(@"C:\Users\O'Brien\[1],a;b.png", @"C\\:/Users/O\\\'Brien/\[1\]\,a\;b.png")]
    public void Filter_paths_are_escaped_twice(string path, string expected) =>
        Assert.Equal(expected, VideoTools.FilterPath(path));

    private static (int R, int G, int B) Pixel((byte[] Rgb, int Width, int Height) frame, int x, int y)
    {
        var i = (y * frame.Width + x) * 3;
        return (frame.Rgb[i], frame.Rgb[i + 1], frame.Rgb[i + 2]);
    }

    private static double MeanDifference((byte[] Rgb, int Width, int Height) a, (byte[] Rgb, int Width, int Height) b, Func<int, int, bool> region)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                if (!region(x, y)) continue;
                int i = (y * a.Width + x) * 3;
                sum += Math.Abs(a.Rgb[i] - b.Rgb[i]) + Math.Abs(a.Rgb[i + 1] - b.Rgb[i + 1]) + Math.Abs(a.Rgb[i + 2] - b.Rgb[i + 2]);
                n += 3;
            }
        return sum / Math.Max(1, n);
    }

    /// <summary>A transparent PNG with an opaque white block in its top-left corner.</summary>
    private static byte[] CornerPng(int width, int height, int blockW, int blockH)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < blockW && y < blockH; x++)
                raw.AsSpan(y * (width * 4 + 1) + 1 + x * 4, 4).Fill(255);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        Chunk(png, "IHDR", header);
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw);
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
        s.Write(len);
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
