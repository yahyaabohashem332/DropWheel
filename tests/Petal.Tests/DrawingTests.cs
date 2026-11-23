using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Files.Drawings;
using SkiaSharp;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class DrawingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-drawing-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> Convert(string input, FileFormat from, FileFormat to, string? output = null)
    {
        output ??= OutputNaming.ForConversion(input, to);
        var result = await new DrawingConverter().ConvertAsync(new ConversionJob(input, from, to, output, ConversionSettings.Default), null, default);
        Assert.True(File.Exists(result.OutputPaths[0]));
        return result.OutputPaths[0];
    }

    /// <summary>
    /// A small floor plan: walls on a red layer, a door block inserted mirrored, a bulged polyline (a half circle), a
    /// blue solid hatch, text in English and Arabic, a two-line MTEXT, and a line on a layer that is off, far away.
    /// </summary>
    private static CadDocument Plan()
    {
        var doc = new CadDocument();
        var walls = new Layer("Walls") { Color = new Color(1) };
        var off = new Layer("Notes") { IsOn = false };
        doc.Layers.Add(walls);
        doc.Layers.Add(off);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(120, 0, 0), Layer = walls });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(0, 100, 0), Layer = walls });
        doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(5001, 5000, 0), Layer = off });
        doc.Entities.Add(new Circle { Center = new XYZ(50, 50, 0), Radius = 10 });
        var arc = new LwPolyline();
        arc.Vertices.Add(new LwPolyline.Vertex(new XY(0, 60)) { Bulge = 1 });
        arc.Vertices.Add(new LwPolyline.Vertex(new XY(20, 60)));
        doc.Entities.Add(arc);
        var hatch = new Hatch { IsSolid = true, Color = new Color(5), Pattern = HatchPattern.Solid };
        var path = new Hatch.BoundaryPath();
        var square = new Hatch.BoundaryPath.Polyline { IsClosed = true };
        square.Vertices.AddRange([new XYZ(70, 10, 0), new XYZ(90, 10, 0), new XYZ(90, 30, 0), new XYZ(70, 30, 0)]);
        path.Edges.Add(square);
        hatch.Paths.Add(path);
        doc.Entities.Add(hatch);
        doc.Entities.Add(new TextEntity { Value = "Ground floor", InsertPoint = new XYZ(10, 80, 0), Height = 5 });
        doc.Entities.Add(new TextEntity { Value = "مخطط الطابق الأرضي", InsertPoint = new XYZ(10, 90, 0), Height = 5 });
        doc.Entities.Add(new MText { Value = "First line\\PSecond line", InsertPoint = new XYZ(60, 95, 0), Height = 3, AttachmentPoint = AttachmentPointType.TopLeft });
        var door = new BlockRecord("Door");
        door.Entities.Add(new Circle { Center = new XYZ(10, 0, 0), Radius = 2 });
        door.Entities.Add(new LwPolyline([new LwPolyline.Vertex(new XY(0, 0)), new LwPolyline.Vertex(new XY(10, 0))]));
        doc.BlockRecords.Add(door);
        doc.Entities.Add(new Insert(door) { InsertPoint = new XYZ(110, 50, 0), XScale = -1 });
        return doc;
    }

    private string Save(CadDocument doc, string name)
    {
        var path = Path.Combine(_dir, name);
        if (name.EndsWith(".dwg"))
            DwgWriter.Write(path, doc);
        else
            DxfWriter.Write(path, doc, false, null, null);
        return path;
    }

    private static bool Around(Stroke s, Pt center, double radius) =>
        s.Points.Length > 8 && s.Points.All(p => Math.Abs((p - center).Length - radius) < 0.01);

    [Fact]
    public void A_drawing_is_read_with_its_blocks_in_place()
    {
        var path = Save(Plan(), "plan.dxf");
        var drawing = Drawing.Read(Drawing.Load(path, dwg: false), path);
        var strokes = drawing.Marks.OfType<Stroke>().ToList();
        Assert.Contains(strokes, s => Around(s, new Pt(50, 50), 10));
        // The door, mirrored: its circle at 10 along the block's x lands 10 to the left of the insert.
        Assert.Contains(strokes, s => Around(s, new Pt(100, 50), 2));
        Assert.Contains(strokes, s => s.Points.Length == 2 && s.Points.Any(p => Math.Abs(p.X - 100) < 1e-9) && s.Points.Any(p => Math.Abs(p.X - 110) < 1e-9));
        // A bulge of 1 is half a circle: from (0,60) to (20,60) through (10,50) or (10,70).
        Assert.Contains(strokes, s => s.Points.Length > 8 && s.Points.All(p => Math.Abs((p - new Pt(10, 60)).Length - 10) < 0.01));
        // The walls' layer is red; the layer that is off isn't drawn.
        Assert.Contains(strokes, s => s.Color == SKColors.Red && s.Points.Length == 2);
        Assert.DoesNotContain(strokes, s => s.Points.Any(p => p.X > 1000));
        Assert.Contains(drawing.Marks.OfType<Area>(), a => a.Color == SKColors.Blue);
        Assert.Contains(drawing.Marks.OfType<Words>(), w => w.Lines.SequenceEqual(["First line", "Second line"]));
    }

    [Fact]
    public async Task A_drawing_becomes_a_pdf_with_its_text_as_text()
    {
        var path = Save(Plan(), "plan.dxf");
        var pdf = await Convert(path, F.Dxf, F.Pdf);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        var text = string.Concat(page.Letters.Select(l => l.Value));
        Assert.Contains("Ground floor", text.Replace("  ", " "));
        Assert.Contains("First line", text);
        Assert.Contains("Second line", text);
        // Vector: the lines are paths, not a picture.
        Assert.Empty(page.GetImages());
        Assert.NotEmpty(page.Paths);
        // A sheet no larger than A3: 420 mm is 1190.55 points.
        Assert.InRange(Math.Max(page.Width, page.Height), 1100, 1191);
    }

    [Fact]
    public async Task A_drawing_becomes_an_svg_and_a_png()
    {
        var path = Save(Plan(), "plan.dxf");
        var svg = await Convert(path, F.Dxf, F.Svg);
        var xml = System.Xml.Linq.XDocument.Load(svg);
        Assert.Equal("svg", xml.Root!.Name.LocalName);
        Assert.True(xml.Descendants().Count(e => e.Name.LocalName is "path" or "line" or "polyline") > 5);
        // English stays text to search; Arabic goes as its joined letters' shapes, which an SVG's text can't keep.
        var texts = xml.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value.Trim()).ToList();
        Assert.Contains("Ground floor", texts);
        Assert.DoesNotContain(texts, t => DrawingPicture.NeedsShaping(t));

        var png = await Convert(path, F.Dxf, F.Png);
        if (Environment.GetEnvironmentVariable("PETAL_DRAWINGS_OUT") is { Length: > 0 } outFolder)
            File.Copy(png, Path.Combine(outFolder, "plan.png"), overwrite: true);
        using var picture = SKBitmap.Decode(png);
        Assert.InRange(Math.Max(picture.Width, picture.Height), 3900, 4000);
        Assert.Equal(SKColors.White, picture.GetPixel(2, 2));
        int red = 0, blue = 0, dark = 0;
        for (var y = 0; y < picture.Height; y += 2)
            for (var x = 0; x < picture.Width; x += 2)
            {
                var c = picture.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 60 && c.Blue < 60) red++;
                if (c.Blue > 200 && c.Red < 60 && c.Green < 60) blue++;
                if (c.Red < 60 && c.Green < 60 && c.Blue < 60) dark++;
            }
        Assert.True(red > 500 && blue > 20_000 && dark > 2_000, $"red {red}, blue {blue}, dark {dark}");
    }

    [Fact]
    public async Task A_dwg_becomes_a_dxf_with_everything_in_it()
    {
        var dwg = Save(Plan(), "plan.dwg");
        var dxf = await Convert(dwg, F.Dwg, F.Dxf);
        var original = Drawing.Read(Drawing.Load(dwg, dwg: true), dwg);
        var copy = Drawing.Read(Drawing.Load(dxf, dwg: false), dxf);
        Assert.Equal(original.Marks.Select(m => m.GetType().Name), copy.Marks.Select(m => m.GetType().Name));
        Assert.Contains(copy.Marks.OfType<Words>(), w => w.Lines[0] == "مخطط الطابق الأرضي");
        Assert.StartsWith("  0", File.ReadLines(dxf).First());
        await Convert(dwg, F.Dwg, F.Png, Path.Combine(_dir, "from dwg.png"));
    }

    [Fact]
    public async Task A_drawing_with_nothing_in_its_model_space_shows_its_sheet()
    {
        var doc = new CadDocument();
        doc.PaperSpace.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(297, 210, 0) });
        var path = Save(doc, "sheet.dxf");
        var drawing = Drawing.Read(Drawing.Load(path, dwg: false), path);
        Assert.Single(drawing.Marks);
        await Convert(path, F.Dxf, F.Png);
    }

    [Fact]
    public async Task What_isnt_a_drawing_says_so()
    {
        var fake = Path.Combine(_dir, "fake.dwg");
        File.WriteAllText(fake, "AC1032 this is not a drawing at all");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Convert(fake, F.Dwg, F.Pdf));
        Assert.Contains("R14", ex.Message);

        var empty = Save(new CadDocument(), "empty.dxf");
        await Assert.ThrowsAsync<InvalidDataException>(() => Convert(empty, F.Dxf, F.Png));
    }

    [Fact]
    public void Text_codes_become_their_signs()
    {
        Assert.Equal("Ø20 ±0.5 90° 100%", Drawing.Plain("%%c20 %%p0.5 90%%d 100%%%"));
        Assert.Equal("underlined", Drawing.Plain("%%uunderlined%%u"));
    }

    /// <summary>
    /// Drawings a CAD program made (PETAL_DRAWINGS: a folder of DXF and DWG files, such as ACadSharp's samples in
    /// D:\.dropwheel-sdk\cad\samples), each converted to every format, beside a copy in PETAL_DRAWINGS_OUT.
    /// </summary>
    [Fact]
    public async Task Real_drawings_convert_to_every_format()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_DRAWINGS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;
        var outFolder = Environment.GetEnvironmentVariable("PETAL_DRAWINGS_OUT") ?? Path.Combine(_dir, "out");
        Directory.CreateDirectory(outFolder);
        var report = new StringBuilder();
        foreach (var file in Directory.GetFiles(folder).Order())
        {
            if (F.FromPath(file) is not { Family: FormatFamily.Drawing } format)
                continue;
            var stem = Path.GetFileName(file).Replace('.', '_');
            foreach (var to in ConversionMatrix.TargetsFor(format))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var output = Path.Combine(outFolder, $"{stem}.{to.WriteExtension}");
                File.Delete(output);
                try
                {
                    await Convert(file, format, to, output);
                    report.AppendLine($"{Path.GetFileName(file)} -> {to.Label}: {new FileInfo(output).Length / 1024} KB in {watch.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"{Path.GetFileName(file)} -> {to.Label}: FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }
            var doc = Drawing.Load(file, format == F.Dwg);
            var drawing = Drawing.Read(doc, file);
            if (format == F.Dwg)
            {
                // The DXF written from it draws the same.
                var dxf = Path.Combine(outFolder, $"{stem}.dxf");
                var copy = Drawing.Read(Drawing.Load(dxf, dwg: false), dxf);
                string Kinds(Drawing d) => string.Join(", ", d.Marks.GroupBy(m => m.GetType().Name).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
                var same = Kinds(drawing) == Kinds(copy);
                report.AppendLine($"  its DXF: {Kinds(copy)} of {Kinds(drawing)}: {(same ? "the same" : "FAILED: not the same")}");
            }
            report.AppendLine($"  {doc.Header.Version}: {doc.ModelSpace.Entities.Count()} entities, kinds {string.Join(", ", doc.ModelSpace.Entities.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}; " +
                $"marks {string.Join(", ", drawing.Marks.GroupBy(m => m.GetType().Name).Select(g => $"{g.Key} {g.Count()}"))}");
        }
        File.WriteAllText(Path.Combine(outFolder, "report.txt"), report.ToString());
        Assert.DoesNotContain("FAILED", report.ToString());
    }
}

