using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Petal.Shell;

namespace Petal.Tests;

/// <summary>
/// File Explorer's thumbnails for design files: the preview each kind of file carries is found and read
/// at the size asked for, and Windows itself, through its thumbnail process, gets a picture from the
/// .NET Framework 4.8 handler Petal registers.
/// </summary>
public sealed class ExplorerThumbnailTests
{
    private static string Data(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "testdata", name);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>A picture in two colours, left half red and right half blue, so a reader that mixes it up shows.</summary>
    private static Bitmap Sample(int w = 120, int h = 80)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.FillRectangle(Brushes.Red, 0, 0, w / 2, h);
        g.FillRectangle(Brushes.Blue, w / 2, 0, w - w / 2, h);
        return bitmap;
    }

    private static byte[] Encode(Image image, ImageFormat format)
    {
        using var stream = new MemoryStream();
        image.Save(stream, format);
        return stream.ToArray();
    }

    private static void LooksLikeSample(Bitmap picture)
    {
        var left = picture.GetPixel(picture.Width / 4, picture.Height / 2);
        var right = picture.GetPixel(picture.Width * 3 / 4, picture.Height / 2);
        Assert.True(left.R > 180 && left.B < 90, left.ToString());
        Assert.True(right.B > 180 && right.R < 90, right.ToString());
    }

    [Fact]
    public void A_Photoshop_file_gives_its_merged_picture_at_the_size_asked()
    {
        using var stream = File.OpenRead(Data("layers.psd"));
        using var picture = EmbeddedPreview.Read(stream, 96);
        Assert.NotNull(picture);
        Assert.True(Math.Max(picture.Width, picture.Height) <= 96, $"{picture.Width}×{picture.Height}");
        // Not a blank: the picture has more than one colour in it.
        var colours = new HashSet<int>();
        for (int y = 0; y < picture.Height; y += 4)
            for (int x = 0; x < picture.Width; x += 4)
                colours.Add(picture.GetPixel(x, y).ToArgb());
        Assert.True(colours.Count > 3, colours.Count.ToString());
    }

    [Fact]
    public void Photoshop_run_lengths_unpack()
    {
        // 3 literal bytes (count 2), then 0xAA repeated 4 times (count -3), then one literal.
        byte[] packed = [2, 1, 2, 3, unchecked((byte)-3), 0xAA, 0, 9];
        var row = new byte[8];
        EmbeddedPreview.UnpackBits(packed, packed.Length, row);
        Assert.Equal(new byte[] { 1, 2, 3, 0xAA, 0xAA, 0xAA, 0xAA, 9 }, row);
        // A short or broken run never writes past the row.
        var tiny = new byte[2];
        EmbeddedPreview.UnpackBits([unchecked((byte)-100), 7], 2, tiny);
        Assert.Equal(new byte[] { 7, 7 }, tiny);
    }

    [Fact]
    public void Illustrator_and_InDesign_previews_come_from_their_XMP()
    {
        using var sample = Sample();
        var base64 = Convert.ToBase64String(Encode(sample, ImageFormat.Jpeg));
        // Written the way Illustrator does: wrapped lines with &#xA; between them.
        var wrapped = string.Join("&#xA;", Enumerable.Range(0, (base64.Length + 75) / 76).Select(i => base64.Substring(i * 76, Math.Min(76, base64.Length - i * 76))));
        var file = "%PDF-1.6\n%stuff\n<x:xmpmeta><rdf:li><xmpGImg:width>120</xmpGImg:width><xmpGImg:image>" + wrapped + "</xmpGImg:image></rdf:li></x:xmpmeta>\n%%EOF";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(file));
        using var picture = EmbeddedPreview.Read(stream, 256);
        Assert.NotNull(picture);
        Assert.Equal((120, 80), (picture.Width, picture.Height));
        LooksLikeSample(picture);
        // The attribute form, as older files write it.
        Assert.NotNull(EmbeddedPreview.XmpThumbnail("<rdf:li xapGImg:image=\"" + base64 + "\"/>"));
        Assert.Null(EmbeddedPreview.XmpThumbnail("<xmpGImg:image>not base64!</xmpGImg:image>"));
    }

    [Fact]
    public void An_EPS_file_shows_its_TIFF_preview()
    {
        using var sample = Sample();
        var tiff = Encode(sample, ImageFormat.Tiff);
        var postScript = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 120 80\n");
        var header = new byte[30];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(header, 0);
        BitConverter.GetBytes(30).CopyTo(header, 4);
        BitConverter.GetBytes(postScript.Length).CopyTo(header, 8);
        BitConverter.GetBytes(30 + postScript.Length).CopyTo(header, 20);
        BitConverter.GetBytes(tiff.Length).CopyTo(header, 24);
        header[28] = header[29] = 0xFF;
        using var stream = new MemoryStream([.. header, .. postScript, .. tiff]);
        using var picture = EmbeddedPreview.Read(stream, 64);
        Assert.NotNull(picture);
        Assert.Equal(64, picture.Width);
        LooksLikeSample(picture);
    }

    [Fact]
    public void Krita_and_OpenRaster_files_show_their_merged_image()
    {
        using var sample = Sample();
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = zip.CreateEntry("mimetype");
            using (var w = new StreamWriter(mimetype.Open()))
                w.Write("application/x-krita");
            var merged = zip.CreateEntry("mergedimage.png");
            using var s = merged.Open();
            s.Write(Encode(sample, ImageFormat.Png));
        }
        zipped.Position = 0;
        using var picture = EmbeddedPreview.Read(zipped, 256);
        Assert.NotNull(picture);
        LooksLikeSample(picture);
    }

    [Fact]
    public void Files_without_a_preview_give_nothing()
    {
        Assert.Null(EmbeddedPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes("just some words, no pictures at all")), 128));
        Assert.Null(EmbeddedPreview.Read(new MemoryStream([0x38, 0x42, 0x50, 0x53, 0, 1]), 128));
        Assert.Null(EmbeddedPreview.Read(new MemoryStream([]), 128));
    }

    /// <summary>
    /// The handler's place is registered the way .NET Framework writes it itself, nothing escaped: the
    /// runtime reads "%20" as those three characters, not as a space.
    /// </summary>
    [Fact]
    public void The_handlers_place_is_written_with_nothing_escaped()
    {
        Assert.Equal("file:///C:/Users/Ayşe Gül/AppData/Local/Programs/Petal/shell/Petal.Shell.dll",
            Petal.App.Shell.ExplorerThumbnails.CodeBase(@"C:\Users\Ayşe Gül\AppData\Local\Programs\Petal\shell\Petal.Shell.dll"));
        Assert.Equal("file:///D:/100% #1/يحيى/Petal.Shell.dll", Petal.App.Shell.ExplorerThumbnails.CodeBase(@"D:\100% #1\يحيى\Petal.Shell.dll"));
        Assert.Equal("file://server/share/Petal/shell/Petal.Shell.dll", Petal.App.Shell.ExplorerThumbnails.CodeBase(@"\\server\share\Petal\shell\Petal.Shell.dll"));
    }

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>A class of the test's own: what DropWheel registered on this PC for its thumbnails is never touched.</summary>
    private const string TestClassId = "{BC5AF004-CA5F-489B-B057-D27F7E048259}";

    // How Windows' thumbnail cache is asked (WTS_FLAGS): the handler is run now, and its picture isn't kept.
    private const int Afresh = 0x4 /* WTS_FORCEEXTRACTION */ | 0x20 /* WTS_EXTRACTDONOTCACHE */;
    private const int InThisProcess = 0x100; // WTS_EXTRACTINPROC

    /// <summary>
    /// Windows' thumbnail cache, the one File Explorer asks, is asked for the picture of a file whose type
    /// and class are registered for the test only, by the code that registers DropWheel's own: Windows finds
    /// the handler, runs it in its thumbnail process, and gets the Photoshop file's picture. The handler sits
    /// in a folder named as a user's own can be, with spaces, Arabic and Turkish letters, a # and a %.
    /// </summary>
    /// <summary>The files the test asks Windows for: a Photoshop file, a PDF, an SVG and a 3D model.</summary>
    private static byte[] WindowsSample(string kind) => kind switch
    {
        "psd" => File.ReadAllBytes(Data("layers.psd")),
        "pdf" => File.ReadAllBytes(Data("album.pdf")),
        "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"80\"><rect width=\"60\" height=\"80\" fill=\"red\"/><rect x=\"60\" width=\"60\" height=\"80\" fill=\"blue\"/></svg>"u8.ToArray(),
        _ => BinaryStl(),
    };

    [Fact]
    public void Windows_gets_the_picture_from_the_handler()
    {
        var built = Path.Combine(Repo(), "src", "Petal.Shell", "bin", Configuration, "net48", "Petal.Shell.dll");
        Assert.True(File.Exists(built), built);
        const string ext = ".petalthumbtest";
        var clsidPath = $@"Software\Classes\CLSID\{TestClassId}";
        const string approvals = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached";
        // What an earlier run couldn't delete, Windows' thumbnail process still holding the handler.
        foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "petal-thumb-*"))
            if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                Delete(old);
        var folder = Path.Combine(Path.GetTempPath(), "petal-thumb-" + Guid.NewGuid().ToString("N")[..8]);
        var dll = Path.Combine(folder, "يحيى Ayşe Gül #1 100%", "Petal.Shell.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        foreach (var library in Directory.GetFiles(Path.GetDirectoryName(built)!, "*.dll"))
            File.Copy(library, Path.Combine(Path.GetDirectoryName(dll)!, Path.GetFileName(library)));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "pdfium.dll"), Path.Combine(folder, "pdfium.dll"));
        try
        {
            Petal.App.Shell.ExplorerThumbnails.RegisterClass(Registry.CurrentUser, TestClassId, dll);
            using (var handler = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}"))
                handler.SetValue(null, TestClassId);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            // Started from a packaged app (the Claude app is one), what this process writes to the registry
            // goes to a copy that is the app's own, and Windows' thumbnail process never sees the class:
            // there Windows runs the handler in this process instead. All the rest is the same.
            bool inThisProcess = !WindowsSees(clsidPath + @"\InprocServer32");
            int flags = Afresh | (inThisProcess ? InThisProcess : 0);
            // An SVG needs Svg.NET, which this process's own folder holds in its .NET 10 build: run here, the handler
            // would be given that one. Windows' process has no such folder; Svg_through_a_process_like_Windows_own covers it.
            foreach (var kind in inThisProcess ? ["psd", "pdf", "stl"] : new[] { "psd", "pdf", "svg", "stl" })
            {
                var file = Path.Combine(folder, kind + ext);
                File.WriteAllBytes(file, WindowsSample(kind));
                using var got = OnSta(() =>
                {
                    var iid = typeof(IShellItem).GUID;
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out var item));
                    var cache = (IThumbnailCache)new LocalThumbnailCache();
                    Marshal.ThrowExceptionForHR(cache.GetThumbnail(item, 256, flags, out var shared, IntPtr.Zero, IntPtr.Zero));
                    // The bitmap stays the cache's own: it is copied here, not deleted.
                    Marshal.ThrowExceptionForHR(shared!.GetSharedBitmap(out var hbitmap));
                    return Image.FromHbitmap(hbitmap);
                });
                // The file's own picture, as the reader gives it, came through Windows: not just any bitmap.
                using var stream = new MemoryStream(WindowsSample(kind));
                using var own = Previews.Read(stream, 256);
                Assert.NotNull(own);
                Assert.True(own.Width > 32 && own.Height > 32, $"{kind}: {own.Width}×{own.Height}");
                Assert.Equal((own.Width, own.Height), (got.Width, got.Height));
                for (int y = 2; y < own.Height; y += 8)
                    for (int x = 2; x < own.Width; x += 8)
                    {
                        // Windows gives the colours multiplied by their opacity.
                        var (a, b) = (own.GetPixel(x, y), got.GetPixel(x, y));
                        int R(int c) => c * a.A / 255;
                        Assert.True(Math.Abs(R(a.R) - b.R) <= 2 && Math.Abs(R(a.G) - b.G) <= 2 && Math.Abs(R(a.B) - b.B) <= 2, $"{kind} at {x},{y}: {a} in the file, {b} from Windows");
                    }
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false);
            // Windows notes each handler it let in; the test's goes with the rest.
            using (var noted = Registry.CurrentUser.OpenSubKey(approvals, writable: true))
                noted?.DeleteValue($"{TestClassId} {{E357FCCD-A995-4576-B01F-234630154E96}} 0xFFFF", throwOnMissingValue: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            Delete(folder);
        }
    }

    // ───────────── PDFs, models, books, comics, SVGs and Office files ─────────────

    /// <summary>A cube of twelve triangles, its corners from 0 to 10 on each side.</summary>
    private static (float X, float Y, float Z)[] CubeCorners =>
    [
        (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0), (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10),
    ];

    private static readonly int[] CubeFaces = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3];

    private static byte[] BinaryStl()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(new byte[80]);
        w.Write((uint)(CubeFaces.Length / 3));
        for (int t = 0; t < CubeFaces.Length; t += 3)
        {
            w.Write(0f); w.Write(0f); w.Write(0f);
            for (int k = 0; k < 3; k++)
            {
                var c = CubeCorners[CubeFaces[t + k]];
                w.Write(c.X); w.Write(c.Y); w.Write(c.Z);
            }
            w.Write((ushort)0);
        }
        return stream.ToArray();
    }

    private static string TextStl()
    {
        var sb = new StringBuilder("solid cube\n");
        for (int t = 0; t < CubeFaces.Length; t += 3)
        {
            sb.Append(" facet normal 0 0 0\n  outer loop\n");
            for (int k = 0; k < 3; k++)
            {
                var c = CubeCorners[CubeFaces[t + k]];
                sb.Append(FormattableString.Invariant($"   vertex {c.X} {c.Y} {c.Z}\n"));
            }
            sb.Append("  endloop\n endfacet\n");
        }
        return sb.Append("endsolid cube\n").ToString();
    }

    private static string Obj() =>
        "# a cube\no cube\n" + string.Concat(CubeCorners.Select(c => FormattableString.Invariant($"v {c.X} {c.Y} {c.Z}\n")))
        + "f 1/1/1 2/2/2 3/3/3 4/4/4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n";

    private static byte[] BinaryPly()
    {
        using var stream = new MemoryStream();
        var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {CubeCorners.Length}\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nelement face {CubeFaces.Length / 3}\nproperty list uchar int vertex_indices\nend_header\n";
        stream.Write(Encoding.ASCII.GetBytes(header));
        using var w = new BinaryWriter(stream);
        foreach (var c in CubeCorners)
        {
            w.Write(c.X); w.Write(c.Y); w.Write(c.Z); w.Write((byte)200);
        }
        for (int t = 0; t < CubeFaces.Length; t += 3)
        {
            w.Write((byte)3); w.Write(CubeFaces[t]); w.Write(CubeFaces[t + 1]); w.Write(CubeFaces[t + 2]);
        }
        return stream.ToArray();
    }

    private static byte[] ThreeMf(bool thumbnail)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var model = new StringBuilder("<?xml version=\"1.0\"?><model unit=\"millimeter\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"1\" type=\"model\"><mesh><vertices>");
            foreach (var c in CubeCorners)
                model.Append(FormattableString.Invariant($"<vertex x=\"{c.X}\" y=\"{c.Y}\" z=\"{c.Z}\"/>"));
            model.Append("</vertices><triangles>");
            for (int t = 0; t < CubeFaces.Length; t += 3)
                model.Append($"<triangle v1=\"{CubeFaces[t]}\" v2=\"{CubeFaces[t + 1]}\" v3=\"{CubeFaces[t + 2]}\"/>");
            model.Append("</triangles></mesh></object></resources><build><item objectid=\"1\"/></build></model>");
            using (var w = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open()))
                w.Write(model);
            if (thumbnail)
            {
                using var sample = Sample();
                using var e = zip.CreateEntry("Metadata/thumbnail.png").Open();
                e.Write(Encode(sample, ImageFormat.Png));
            }
        }
        return stream.ToArray();
    }

    private static byte[] Glb()
    {
        var positions = CubeCorners.SelectMany(c => new[] { c.X, c.Y, c.Z }).SelectMany(BitConverter.GetBytes).ToArray();
        var indices = CubeFaces.SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray();
        var bin = positions.Concat(indices).ToList();
        while (bin.Count % 4 != 0)
            bin.Add(0);
        // A node that moves the cube, so the reader must place it by its transform.
        var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0,\"translation\":[5,0,0],\"scale\":[2,2,2]}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],"
            + $"\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":{CubeCorners.Length},\"type\":\"VEC3\"}},{{\"bufferView\":1,\"componentType\":5123,\"count\":{CubeFaces.Length},\"type\":\"SCALAR\"}}],"
            + $"\"bufferViews\":[{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{positions.Length}}},{{\"buffer\":0,\"byteOffset\":{positions.Length},\"byteLength\":{indices.Length}}}],"
            + $"\"buffers\":[{{\"byteLength\":{bin.Count}}}]}}";
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        while (jsonBytes.Count % 4 != 0)
            jsonBytes.Add((byte)' ');
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Count + 8 + bin.Count));
        w.Write((uint)jsonBytes.Count); w.Write(0x4E4F534Au); w.Write(jsonBytes.ToArray());
        w.Write((uint)bin.Count); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return stream.ToArray();
    }

    public static IEnumerable<object[]> Models() =>
    [
        ["binary STL", BinaryStl()],
        ["text STL", Encoding.ASCII.GetBytes(TextStl())],
        ["OBJ", Encoding.ASCII.GetBytes(Obj())],
        ["binary PLY", BinaryPly()],
        ["3MF", ThreeMf(thumbnail: false)],
        ["GLB", Glb()],
    ];

    [Theory]
    [MemberData(nameof(Models))]
    public void A_3D_model_is_read_and_drawn_standing_in_the_middle_on_a_clear_background(string kind, byte[] bytes)
    {
        var mesh = Mesh.Read(bytes);
        Assert.True(mesh is not null, kind);
        Assert.Equal(12, mesh!.TriangleCount);
        using var stream = new MemoryStream(bytes);
        using var picture = Previews.Read(stream, 128);
        Assert.NotNull(picture);
        Assert.Equal(128, Math.Max(picture!.Width, picture.Height));
        // Drawn in the middle, nothing in the corners.
        Assert.True(picture.GetPixel(picture.Width / 2, picture.Height / 2).A > 200, kind);
        Assert.Equal(0, picture.GetPixel(1, 1).A);
        Assert.Equal(0, picture.GetPixel(picture.Width - 2, picture.Height - 2).A);
        // Lit: faces seen from different sides aren't all one shade.
        var shades = new HashSet<int>();
        for (int y = 0; y < picture.Height; y += 4)
            for (int x = 0; x < picture.Width; x += 4)
                if (picture.GetPixel(x, y) is { A: > 250 } c)
                    shades.Add(c.R / 8);
        Assert.True(shades.Count >= 2, $"{kind}: {shades.Count} shades");
    }

    /// <summary>
    /// Thumbnails and previews registered under a key of the test's own (standing for the user's): each type that no
    /// other program handles gets DropWheel's, the preview runs in Windows' preview process and is in its list, and
    /// turning it off leaves nothing behind.
    /// </summary>
    [Fact]
    public void Thumbnails_and_previews_are_registered_and_removed_whole()
    {
        const string test = @"Software\PetalRegistrationTest";
        Registry.CurrentUser.DeleteSubKeyTree(test, throwOnMissingSubKey: false);
        using var root = Registry.CurrentUser.CreateSubKey(test);
        try
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "shell", "Petal.Shell.dll");
            Petal.App.Shell.ExplorerThumbnails.Register(root, dll);
            var thumbnails = Petal.App.Shell.ExplorerThumbnails.Covered(root);
            var previews = Petal.App.Shell.ExplorerThumbnails.Previewed(root);
            // Every kind that no other program on this PC handles (a PC with others gives some up, as it should).
            Assert.NotEmpty(thumbnails);
            Assert.Subset(Petal.App.Shell.ExplorerThumbnails.Extensions.ToHashSet(), thumbnails.ToHashSet());
            Assert.Subset(Petal.App.Shell.ExplorerThumbnails.PreviewExtensions.ToHashSet(), previews.ToHashSet());
            Assert.NotEmpty(previews);
            using (var clsid = root.OpenSubKey(@"Software\Classes\CLSID\" + Petal.App.Shell.ExplorerThumbnails.PreviewClassId)!)
            {
                Assert.Equal("{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}", clsid.GetValue("AppID"));
                using var server = clsid.OpenSubKey("InprocServer32")!;
                Assert.Equal("Petal.Shell.PreviewHandler", server.GetValue("Class"));
                Assert.Equal("Apartment", server.GetValue("ThreadingModel"));
            }
            using (var list = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers")!)
                Assert.Equal("DropWheel previews", list.GetValue(Petal.App.Shell.ExplorerThumbnails.PreviewClassId));

            Petal.App.Shell.ExplorerThumbnails.Unregister(root);
            Assert.Empty(Petal.App.Shell.ExplorerThumbnails.Covered(root));
            Assert.Empty(Petal.App.Shell.ExplorerThumbnails.Previewed(root));
            using var classes = root.OpenSubKey(@"Software\Classes");
            Assert.True(classes is null || classes.GetSubKeyNames().All(n => n == "CLSID") && (classes.OpenSubKey("CLSID")?.SubKeyCount ?? 0) == 0, string.Join(", ", classes?.GetSubKeyNames() ?? []));
            using var left = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers");
            Assert.True(left is null || left.GetValue(Petal.App.Shell.ExplorerThumbnails.PreviewClassId) is null);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(test, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_Photoshop_files_layers_are_listed_top_first()
    {
        using var stream = File.OpenRead(Data("layers.psd"));
        var layers = PsdLayers.Read(stream);
        Assert.Equal(["Red box", "L1"], layers.Select(l => l.Name));
        Assert.All(layers, l => Assert.True(l.Visible));
        Assert.Empty(PsdLayers.Read(new MemoryStream(Encoding.ASCII.GetBytes("not a photoshop file at all"))));
    }

    [Fact]
    public void A_glTF_nodes_transform_places_its_mesh()
    {
        var mesh = Mesh.Read(Glb())!;
        // Scaled twice and moved 5 along X: from 5 to 25.
        var xs = Enumerable.Range(0, mesh.Points.Length / 3).Select(i => mesh.Points[i * 3]).ToList();
        Assert.Equal(5, xs.Min(), 3);
        Assert.Equal(25, xs.Max(), 3);
    }

    [Fact]
    public void Other_files_are_not_taken_for_models()
    {
        Assert.Null(Mesh.Read(Encoding.ASCII.GetBytes("Hello, this is a letter and not a model.\nRegards\n")));
        var coff = new byte[200];
        coff[0] = 0x64; coff[1] = 0x86;
        Assert.Null(Mesh.Read(coff));
        Assert.False(Mesh.LooksLikeModel(coff, coff.Length, coff.Length));
    }

    [Fact]
    public void A_3MF_model_shows_its_own_thumbnail_when_it_has_one()
    {
        using var stream = new MemoryStream(ThreeMf(thumbnail: true));
        using var picture = Previews.Read(stream, 256);
        LooksLikeSample(picture!);
    }

    [Fact]
    public void A_PDF_shows_its_first_page()
    {
        var bytes = File.ReadAllBytes(Data("album.pdf"));
        using var page = PdfPages.Render(bytes, 0, 300, out int pages);
        Assert.NotNull(page);
        Assert.Equal(3, pages);
        Assert.Equal(300, Math.Max(page!.Width, page.Height));
        // The first page of the album is coloured bars, not a blank sheet.
        var colours = new HashSet<int>();
        for (int x = 5; x < page.Width; x += 10)
            colours.Add(page.GetPixel(x, page.Height / 2).ToArgb());
        Assert.True(colours.Count > 3, $"{colours.Count} colours");
        using var stream = File.OpenRead(Data("album.pdf"));
        using var thumbnail = Previews.Read(stream, 128);
        Assert.Equal(128, Math.Max(thumbnail!.Width, thumbnail.Height));
        Assert.Null(PdfPages.Render(bytes, 7, 100));
        Assert.Null(PdfPages.Render(Encoding.ASCII.GetBytes("%PDF-1.7 not really"), 0, 100));
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var e = zip.CreateEntry(name).Open();
                e.Write(bytes);
            }
        return stream.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_EPUB_shows_the_cover_its_package_names(bool epub3)
    {
        using var sample = Sample();
        using var other = new Bitmap(50, 50);
        var opf = epub3
            ? "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><manifest><item id=\"p\" href=\"images/page.png\" media-type=\"image/png\"/><item id=\"c\" href=\"images/the%20cover.png\" media-type=\"image/png\" properties=\"cover-image\"/></manifest></package>"
            : "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\"><metadata><meta name=\"cover\" content=\"c\"/></metadata><manifest><item id=\"p\" href=\"images/page.png\" media-type=\"image/png\"/><item id=\"c\" href=\"text/../images/the cover.png\" media-type=\"image/png\"/></manifest></package>";
        var bytes = Zip(
            ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip")),
            ("META-INF/container.xml", Encoding.UTF8.GetBytes("<container><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>")),
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf)),
            ("OEBPS/images/page.png", Png(other)),
            ("OEBPS/images/the cover.png", Encode(sample, ImageFormat.Png)));
        using var stream = new MemoryStream(bytes);
        using var picture = Previews.Read(stream, 256);
        LooksLikeSample(picture!);
    }

    private static byte[] Png(Image image) => Encode(image, ImageFormat.Png);

    [Fact]
    public void A_comic_book_shows_its_first_page_in_numbered_order()
    {
        using var sample = Sample();
        using var other = new Bitmap(40, 60);
        var bytes = Zip(("page10.png", Png(other)), ("page2.png", Png(other)), ("page1.png", Encode(sample, ImageFormat.Png)), ("ComicInfo.xml", "<x/>"u8.ToArray()));
        using var stream = new MemoryStream(bytes);
        using var picture = Previews.Read(stream, 256);
        LooksLikeSample(picture!);
        // A ZIP of other things isn't a comic book.
        using var mixed = new MemoryStream(Zip(("a.txt", "a"u8.ToArray()), ("b.txt", "b"u8.ToArray()), ("c.txt", "c"u8.ToArray()), ("d.txt", "d"u8.ToArray()), ("x.png", Png(other))));
        Assert.Null(Previews.Read(mixed, 256));
    }

    [Fact]
    public void An_Office_file_saved_with_a_thumbnail_shows_it()
    {
        using var sample = Sample();
        var bytes = Zip(("[Content_Types].xml", "<Types/>"u8.ToArray()), ("word/document.xml", "<w:document/>"u8.ToArray()), ("docProps/thumbnail.jpeg", Encode(sample, ImageFormat.Jpeg)));
        using var stream = new MemoryStream(bytes);
        using var picture = Previews.Read(stream, 256);
        LooksLikeSample(picture!);
    }

    [Fact]
    public void An_SVG_is_drawn_in_its_proportions_and_reads_nothing_outside_itself()
    {
        var svg = "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"120\" height=\"80\" viewBox=\"0 0 120 80\">"
            + "<rect x=\"0\" y=\"0\" width=\"60\" height=\"80\" fill=\"red\"/><rect x=\"60\" y=\"0\" width=\"60\" height=\"80\" fill=\"blue\"/>"
            + "<image x=\"0\" y=\"0\" width=\"10\" height=\"10\" xlink:href=\"http://127.0.0.1:9/never.png\"/></svg>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var picture = Previews.Read(stream, 240);
        Assert.NotNull(picture);
        Assert.Equal((240, 160), (picture!.Width, picture.Height));
        LooksLikeSample(picture);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        Assert.Null(Previews.Read(new MemoryStream("<html><body>not a picture</body></html>"u8.ToArray()), 100));
    }

    /// <summary>
    /// The extension as Windows' thumbnail process runs it: loaded by a .NET Framework 4.8 program from the "shell"
    /// folder (PDFium beside it, a folder up, as in DropWheel's own folder), into a process whose folder has none of
    /// its libraries; Svg.NET and what it needs are found beside the extension.
    /// </summary>
    [Fact]
    public void Svg_and_pdf_through_a_process_like_Windows_own()
    {
        var built = Path.Combine(Repo(), "src", "Petal.Shell", "bin", Configuration, "net48");
        var host = Path.Combine(Repo(), "tests", "Petal.ShellHost", "bin", Configuration, "net48", "Petal.ShellHost.exe");
        Assert.True(File.Exists(host), host);
        var app = Path.Combine(Path.GetTempPath(), "petal-shellhost-" + Guid.NewGuid().ToString("N")[..8]);
        var shell = Path.Combine(app, "shell");
        Directory.CreateDirectory(shell);
        try
        {
            foreach (var library in Directory.GetFiles(built, "*.dll"))
                File.Copy(library, Path.Combine(shell, Path.GetFileName(library)));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "pdfium.dll"), Path.Combine(app, "pdfium.dll"));
            foreach (var kind in new[] { "svg", "pdf", "stl", "psd" })
            {
                var file = Path.Combine(app, "sample." + kind);
                File.WriteAllBytes(file, WindowsSample(kind));
                var png = Path.Combine(app, kind + ".png");
                var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(host)
                {
                    ArgumentList = { Path.Combine(shell, "Petal.Shell.dll"), file, "256", png },
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                })!;
                var error = run.StandardError.ReadToEnd();
                Assert.True(run.WaitForExit(60_000), kind);
                Assert.True(run.ExitCode == 0, $"{kind}: exit {run.ExitCode} {error}");
                using var got = new Bitmap(png);
                using var stream = new MemoryStream(WindowsSample(kind));
                using var own = Previews.Read(stream, 256)!;
                Assert.Equal((own.Width, own.Height), (got.Width, got.Height));
                if (kind == "svg")
                    LooksLikeSample(got);
            }
        }
        finally
        {
            Delete(app);
        }
    }

    /// <summary>
    /// Whether the rest of Windows sees a key this process wrote in the user's registry: WMI's registry
    /// provider is asked, which reads it in a process of Windows' own. Taken as seen when WMI can't say.
    /// </summary>
    private static bool WindowsSees(string key)
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic registry = locator.ConnectServer(".", @"root\default").Get("StdRegProv");
            dynamic asked = registry.Methods_.Item("EnumValues").InParameters.SpawnInstance_();
            asked.Properties_.Item("hDefKey").Value = unchecked((int)0x80000001); // HKEY_CURRENT_USER
            asked.Properties_.Item("sSubKeyName").Value = key;
            dynamic answer = registry.ExecMethod_("EnumValues", asked);
            return (int)answer.Properties_.Item("ReturnValue").Value == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whoever ran the handler can still hold its file: the rest goes now, and that with a later run.
    /// </summary>
    private static void Delete(string folder)
    {
        try
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("On the STA thread: " + error);
        return result;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport, Guid("f676c15d-596a-4ce2-8234-33996f445db1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailCache
    {
        [PreserveSig]
        int GetThumbnail(IShellItem item, uint size, int flags, out ISharedBitmap? bitmap, IntPtr cacheFlags, IntPtr id);
    }

    [ComImport, Guid("091162a4-bc96-411f-aae8-c5122cd03363"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISharedBitmap
    {
        [PreserveSig]
        int GetSharedBitmap(out IntPtr bitmap);
    }

    [ComImport, Guid("50ef4544-ac9f-4a8e-b21b-8a26180db13f")]
    private class LocalThumbnailCache
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
