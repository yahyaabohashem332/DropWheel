using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;
using Petal.Core.Jobs;
using Petal.Engines.Files.Design;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public sealed class DesignDocumentTests(ITestOutputHelper output)
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-design-").FullName;

    private string Picture(string name)
    {
        var path = Path.Combine(_dir, "assets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.Orange, 40, 30);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static string Thumbnail()
    {
        using var image = new MagickImage(MagickColors.SkyBlue, 120, 160);
        return Convert.ToBase64String(image.ToByteArray(MagickFormat.Jpeg));
    }

    /// <summary>XMP as InDesign writes it: application, pages and size, fonts, linked files and a thumbnail.</summary>
    private static string InDesignXmp(string linked, string missing) => $"""
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:xmpGImg="http://ns.adobe.com/xap/1.0/g/img/"
            xmlns:xmpTPg="http://ns.adobe.com/xap/1.0/t/pg/"
            xmlns:stDim="http://ns.adobe.com/xap/1.0/sType/Dimensions#"
            xmlns:stFnt="http://ns.adobe.com/xap/1.0/sType/Font#"
            xmlns:xmpG="http://ns.adobe.com/xap/1.0/g/"
            xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"
            xmlns:stMfs="http://ns.adobe.com/xap/1.0/sType/ManifestItem#"
            xmlns:stRef="http://ns.adobe.com/xap/1.0/sType/ResourceRef#">
           <xmp:CreatorTool>Adobe InDesign 19.0 (Windows)</xmp:CreatorTool>
           <xmp:Thumbnails><rdf:Alt><rdf:li rdf:parseType="Resource">
             <xmpGImg:format>JPEG</xmpGImg:format><xmpGImg:width>120</xmpGImg:width><xmpGImg:height>160</xmpGImg:height>
             <xmpGImg:image>{Thumbnail()}</xmpGImg:image>
           </rdf:li></rdf:Alt></xmp:Thumbnails>
           <xmpTPg:NPages>4</xmpTPg:NPages>
           <xmpTPg:MaxPageSize rdf:parseType="Resource"><stDim:w>595.275591</stDim:w><stDim:h>841.889764</stDim:h><stDim:unit>Points</stDim:unit></xmpTPg:MaxPageSize>
           <xmpTPg:Fonts><rdf:Bag>
             <rdf:li rdf:parseType="Resource"><stFnt:fontName>Arial-BoldMT</stFnt:fontName><stFnt:fontFamily>Arial</stFnt:fontFamily><stFnt:fontFace>Bold</stFnt:fontFace><stFnt:fontType>Open Type</stFnt:fontType><stFnt:fontFileName>arialbd.ttf</stFnt:fontFileName></rdf:li>
             <rdf:li rdf:parseType="Resource"><stFnt:fontName>PetalImaginary-Regular</stFnt:fontName><stFnt:fontFamily>Petal Imaginary</stFnt:fontFamily><stFnt:fontFace>Regular</stFnt:fontFace><stFnt:fontType>Open Type</stFnt:fontType><stFnt:fontFileName>PetalImaginary.otf</stFnt:fontFileName></rdf:li>
           </rdf:Bag></xmpTPg:Fonts>
           <xmpTPg:PlateNames><rdf:Seq><rdf:li>Cyan</rdf:li><rdf:li>Magenta</rdf:li><rdf:li>Yellow</rdf:li><rdf:li>Black</rdf:li></rdf:Seq></xmpTPg:PlateNames>
           <xmpMM:Manifest><rdf:Seq>
             <rdf:li rdf:parseType="Resource"><stMfs:linkForm>ReferenceStream</stMfs:linkForm><stMfs:reference rdf:parseType="Resource"><stRef:filePath>file:///{linked.Replace('\\', '/')}</stRef:filePath></stMfs:reference></rdf:li>
             <rdf:li rdf:parseType="Resource"><stMfs:linkForm>ReferenceStream</stMfs:linkForm><stMfs:reference rdf:parseType="Resource"><stRef:filePath>{missing}</stRef:filePath></stMfs:reference></rdf:li>
           </rdf:Seq></xmpMM:Manifest>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    /// <summary>An InDesign file is binary; Petal reads only its XMP, which follows any a placed picture brought in.</summary>
    private string WriteIndd(string linked, string missing)
    {
        var placedPictureXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:CreatorTool=\"Adobe Photoshop 25.0\"/></rdf:RDF></x:xmpmeta>";
        var noise = new byte[300_000];
        new Random(4).NextBytes(noise);
        var path = Path.Combine(_dir, "Brochure.indd");
        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("\u0006\u0006\u00ED\u00F5\u00D8\u001D\u0046\u00E5DOCUMENT"), .. noise, .. Encoding.UTF8.GetBytes(placedPictureXmp), .. noise, .. Encoding.UTF8.GetBytes(InDesignXmp(linked, missing)), .. noise[..1000]]);
        return path;
    }

    [Fact]
    public void An_InDesign_document_tells_its_pages_fonts_links_and_preview()
    {
        var linked = Picture("photo.jpg");
        var missing = @"C:\Studio\Gone\logo.ai";
        var details = DesignDocuments.Read(WriteIndd(linked, missing));
        Assert.Equal("InDesign document", details.Kind);
        Assert.Equal("Adobe InDesign 19.0 (Windows)", details.Application);
        Assert.Contains(("Pages", "4"), details.Facts);
        Assert.Contains(("Page size", "210 × 297 mm"), details.Facts);
        Assert.Contains(("Colour plates", "Cyan, Magenta, Yellow, Black"), details.Facts);
        Assert.Equal(["Arial Bold", "Petal Imaginary"], details.Fonts.Select(f => f.Name));
        Assert.Equal([linked, missing], details.Links);
        using var preview = new MagickImage(details.Preview!);
        Assert.Equal((120u, 160u), (preview.Width, preview.Height));

        // Arial Bold is on every Windows PC; the made-up font isn't.
        Assert.NotNull(FontIndex.Find(details.Fonts[0]));
        Assert.Null(FontIndex.Find(details.Fonts[1]));
    }

    [Fact]
    public void Save_Preview_writes_the_picture_an_InDesign_document_carries()
    {
        var indd = WriteIndd(Picture("photo.jpg"), @"C:\Gone\x.ai");
        var output = Path.Combine(_dir, "Brochure Preview.png");
        Petal.App.Tools.Windows.AdobeToolSetup.SavePreview(indd, output);
        using var image = new MagickImage(output);
        Assert.Equal((120u, 160u), (image.Width, image.Height));
        // A file without one says so.
        var plain = Path.Combine(_dir, "empty.indd");
        File.WriteAllBytes(plain, new byte[2000]);
        var ex = Assert.Throws<InvalidOperationException>(() => Petal.App.Tools.Windows.AdobeToolSetup.SavePreview(plain, Path.Combine(_dir, "none.png")));
        Assert.Equal("This file has no preview picture.", ex.Message);
    }

    [Fact]
    public void Installed_fonts_are_found_however_a_document_names_them()
    {
        Assert.Contains(FontIndex.Installed, f => f.PostScriptName == "ArialMT");
        Assert.NotNull(FontIndex.Find(new DesignFont("Arial Bold", null, null, "Arial-BoldMT", null)));
        Assert.NotNull(FontIndex.Find(new DesignFont("x", "Arial", "Bold Italic", null, null)));
        Assert.NotNull(FontIndex.Find(new DesignFont("Times New Roman", null, null, null, null)));
        Assert.Null(FontIndex.Find(new DesignFont("Nope Sans", "Nope Sans", "Regular", "NopeSans-Regular", "nope.otf")));
    }

    [Fact]
    public async Task Collecting_an_InDesign_document_gathers_links_and_fonts()
    {
        var linked = Picture("photo.jpg");
        var plan = ProjectCollector.Plan(WriteIndd(linked, @"C:\Studio\Gone\logo.ai"));
        Assert.Equal(1, plan.Missing);
        Assert.Equal(linked, plan.Files[0].Found);
        var folder = Path.Combine(_dir, "Brochure Collected");
        var result = await ProjectCollector.CollectAsync(plan, folder, new CollectOptions());
        Assert.Equal((1, 1, 1, false), (result.Copied, result.Missing, result.Fonts, result.Relinked));
        Assert.True(File.Exists(Path.Combine(folder, "Brochure.indd")));
        Assert.True(File.Exists(Path.Combine(folder, "Links", "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "Document fonts", "arialbd.ttf")));
        var report = File.ReadAllText(Path.Combine(folder, "Collected files.txt"));
        Assert.Contains(@"Missing  C:\Studio\Gone\logo.ai", report);
        Assert.Contains("Font not on this PC: Petal Imaginary", report);
    }

    // ─────────────── IDML ───────────────

    private string WriteIdml(string linked)
    {
        var path = Path.Combine(_dir, "Leaflet.idml");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var s = zip.CreateEntry(name, level).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        Add("mimetype", "application/vnd.adobe.indesign-idml-package", CompressionLevel.NoCompression);
        Add("designmap.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <?aid style="50" type="document" readerVersion="6.0" featureSet="257" product="19.0(55)" ?>
            <Document xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging" DOMVersion="19.0" Self="d">
             <idPkg:Graphic src="Resources/Graphic.xml" />
             <idPkg:Fonts src="Resources/Fonts.xml" />
             <idPkg:Spread src="Spreads/Spread_ua1.xml" />
             <idPkg:Spread src="Spreads/Spread_ua2.xml" />
             <idPkg:Story src="Stories/Story_u100.xml" />
             <idPkg:Story src="Stories/Story_u200.xml" />
            </Document>
            """);
        Add("Spreads/Spread_ua1.xml", $"""
            <idPkg:Spread xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><Spread Self="ua1">
             <Page Self="p1" Name="1" />
             <TextFrame Self="t1" ParentStory="u200" />
             <Rectangle Self="r1"><Image Self="i1"><Link Self="l1" LinkResourceURI="file:{linked.Replace('\\', '/')}" StoredState="Normal" /></Image></Rectangle>
            </Spread></idPkg:Spread>
            """);
        Add("Spreads/Spread_ua2.xml", """
            <idPkg:Spread xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><Spread Self="ua2">
             <Page Self="p2" Name="2" />
             <TextFrame Self="t2" ParentStory="u100" />
            </Spread></idPkg:Spread>
            """);
        Add("Stories/Story_u200.xml", """
            <idPkg:Story xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><Story Self="u200">
             <ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/Heading 1">
              <CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content>Summer Market</Content></CharacterStyleRange>
             </ParagraphStyleRange>
             <ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/$ID/NormalParagraphStyle">
              <CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content>Every Saturday, </Content></CharacterStyleRange>
              <CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]" FontStyle="Bold"><Content>9 to 1</Content><Br /></CharacterStyleRange>
              <CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content>سوق الصيف</Content></CharacterStyleRange>
             </ParagraphStyleRange>
            </Story></idPkg:Story>
            """);
        Add("Stories/Story_u100.xml", """
            <idPkg:Story xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><Story Self="u100">
             <ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/$ID/NormalParagraphStyle">
              <CharacterStyleRange><Content>Prices</Content><Br /></CharacterStyleRange>
              <CharacterStyleRange><Table Self="tb" HeaderRowCount="1" BodyRowCount="1" ColumnCount="2">
               <Cell Name="0:0"><ParagraphStyleRange><CharacterStyleRange><Content>Item</Content></CharacterStyleRange></ParagraphStyleRange></Cell>
               <Cell Name="1:0"><ParagraphStyleRange><CharacterStyleRange><Content>Price</Content></CharacterStyleRange></ParagraphStyleRange></Cell>
               <Cell Name="0:1"><ParagraphStyleRange><CharacterStyleRange><Content>Honey</Content></CharacterStyleRange></ParagraphStyleRange></Cell>
               <Cell Name="1:1"><ParagraphStyleRange><CharacterStyleRange><Content>8</Content></CharacterStyleRange></ParagraphStyleRange></Cell>
              </Table></CharacterStyleRange>
             </ParagraphStyleRange>
            </Story></idPkg:Story>
            """);
        Add("Resources/Fonts.xml", """
            <idPkg:Fonts xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging">
             <FontFamily Self="f1" Name="Arial"><Font Self="f1a" FontFamily="Arial" Name="Arial Bold" PostScriptName="Arial-BoldMT" Status="Installed" FontStyleName="Bold" FontType="OpenTypeTT" /></FontFamily>
             <FontFamily Self="f2" Name="Minion Pro"><Font Self="f2a" FontFamily="Minion Pro" Name="Minion Pro Regular" PostScriptName="MinionPro-Regular" Status="NotAvailable" FontStyleName="Regular" FontType="OpenTypeCFF" /></FontFamily>
            </idPkg:Fonts>
            """);
        Add("Resources/Preferences.xml", """<idPkg:Preferences xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><DocumentPreference PageWidth="419.527559" PageHeight="595.275590" /></idPkg:Preferences>""");
        Add("Resources/Graphic.xml", """<idPkg:Graphic xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging"><Color Self="c1" Name="Market Green" /><Color Self="c2" Name="$ID/Black" /></idPkg:Graphic>""");
        return path;
    }

    [Fact]
    public void An_IDML_package_tells_its_pages_fonts_links_and_swatches()
    {
        var linked = Picture("market.jpg");
        var details = DesignDocuments.Read(WriteIdml(linked));
        Assert.Equal("Adobe InDesign 19.0", details.Application);
        Assert.Contains(("Pages", "2"), details.Facts);
        Assert.Contains(("Page size", "148 × 210 mm"), details.Facts);
        Assert.Equal(["Arial Bold", "Minion Pro Regular"], details.Fonts.Select(f => f.Name));
        Assert.Equal([linked], details.Links);
        Assert.Equal(["Market Green"], details.Swatches);
    }

    [Fact]
    public async Task An_IDML_package_becomes_a_document_in_reading_order()
    {
        var idml = WriteIdml(Picture("market.jpg"));
        var converters = Petal.App.EngineSetup.CreateConverters();
        var txt = Path.Combine(_dir, "Leaflet.txt");
        await converters.Find(F.Idml, F.Txt)!.ConvertAsync(new ConversionJob(idml, F.Idml, F.Txt, txt, new ConversionSettings()), null, CancellationToken.None);
        var text = File.ReadAllText(txt);
        output.WriteLine(text);
        // Page 1's story first, though it is listed second.
        Assert.True(text.IndexOf("Summer Market", StringComparison.Ordinal) < text.IndexOf("Prices", StringComparison.Ordinal));
        Assert.Contains("Every Saturday, 9 to 1", text);
        Assert.Contains("سوق الصيف", text);
        Assert.Contains("Honey", text);

        var docx = Path.Combine(_dir, "Leaflet.docx");
        await converters.Find(F.Idml, F.Docx)!.ConvertAsync(new ConversionJob(idml, F.Idml, F.Docx, docx, new ConversionSettings()), null, CancellationToken.None);
        using var package = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(package.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("Heading1", xml);   // the heading style
        Assert.Contains("<w:b", xml);        // bold "9 to 1"
        Assert.Contains("<w:tbl>", xml);     // the price table
    }

    [Fact]
    public async Task Collecting_an_IDML_package_points_it_at_the_collected_pictures()
    {
        var linked = Picture("market.jpg");
        var plan = ProjectCollector.Plan(WriteIdml(linked));
        var folder = Path.Combine(_dir, "Leaflet Collected");
        var result = await ProjectCollector.CollectAsync(plan, folder, new CollectOptions(CopyFonts: false));
        Assert.True(result.Relinked);
        var copied = Path.Combine(folder, "Links", "market.jpg");
        Assert.True(File.Exists(copied));
        var again = DesignDocuments.Read(result.Project);
        Assert.Equal([copied], again.Links);
        using var zip = ZipFile.OpenRead(result.Project);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength); // stored, as the format requires
    }

    // ─────────────── XD ───────────────

    [Fact]
    public void An_XD_document_shows_its_artboards_and_cover()
    {
        var path = Path.Combine(_dir, "App.xd");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
            Add("mimetype", "application/vnd.adobe.sparkler.project+dcxucf"u8.ToArray());
            Add("manifest", Encoding.UTF8.GetBytes("""
                {"name":"App","children":[{"name":"artwork","path":"artwork","children":[
                  {"name":"Home","path":"artboard-1a","uxdesign#bounds":{"width":375,"height":812}},
                  {"name":"الإعدادات","path":"artboard-2b","uxdesign#bounds":{"width":375,"height":812}}]}]}
                """));
            using var cover = new MagickImage(MagickColors.Purple, 300, 200);
            Add("preview.png", cover.ToByteArray(MagickFormat.Png));
            using var small = new MagickImage(MagickColors.Purple, 30, 20);
            Add("thumbnail.png", small.ToByteArray(MagickFormat.Png));
        }
        var details = DesignDocuments.Read(path);
        Assert.Contains(("Artboards", "2"), details.Facts);
        Assert.Contains(("Artboard names", "Home, الإعدادات"), details.Facts);
        using var preview = new MagickImage(details.Preview!);
        Assert.Equal(300u, preview.Width);
    }

    // ─────────────── Premiere Pro ───────────────

    private string WritePremiere(string clip, string missing)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <PremiereData Version="43">
             <Project ObjectRef="1" />
             <Sequence ObjectUID="s1" ClassID="6a15d903" Version="11"><Name>Wedding Edit</Name></Sequence>
             <Media ObjectUID="m1" ClassID="7a5c103e" Version="30"><Title>clip.mp4</Title><FilePath>{clip}</FilePath><ActualMediaFilePath>{clip}</ActualMediaFilePath><RelativePath>..\assets\clip.mp4</RelativePath><ImplementationID>1fa18bfa</ImplementationID></Media>
             <Media ObjectUID="m2" ClassID="7a5c103e" Version="30"><Title>music.wav</Title><FilePath>{missing}</FilePath><RelativePath>..\gone\music.wav</RelativePath></Media>
             <Media ObjectUID="m3" ClassID="7a5c103e" Version="30"><Title>Sequence</Title><ImplementationID>s1</ImplementationID></Media>
            </PremiereData>
            """;
        var path = Path.Combine(_dir, "Wedding.prproj");
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        gz.Write(Encoding.UTF8.GetBytes(xml));
        return path;
    }

    [Fact]
    public async Task A_Premiere_project_lists_its_media_and_its_collected_copy_points_at_them()
    {
        var clip = Path.Combine(_dir, "assets", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        File.WriteAllBytes(clip, new byte[5000]);
        var project = WritePremiere(clip, @"D:\Gone\music.wav");
        var details = DesignDocuments.Read(project);
        Assert.Contains(("Sequences", "1"), details.Facts);
        Assert.Contains(("Sequence names", "Wedding Edit"), details.Facts);
        Assert.Equal([clip, @"D:\Gone\music.wav"], details.Links);

        var plan = ProjectCollector.Plan(project);
        Assert.Equal(1, plan.Missing);
        var folder = Path.Combine(_dir, "Wedding Collected");
        var result = await ProjectCollector.CollectAsync(plan, folder, new CollectOptions());
        Assert.True(result.Relinked);
        var copied = Path.Combine(folder, "Media", "clip.mp4");
        Assert.True(File.Exists(copied));
        var xml = DesignDocuments.PremiereXml(result.Project);
        Assert.Contains($"<FilePath>{copied}</FilePath>", xml);
        Assert.Contains($"<ActualMediaFilePath>{copied}</ActualMediaFilePath>", xml);
        Assert.Contains(@"<RelativePath>Media\clip.mp4</RelativePath>", xml);
        // The missing file and everything else are left exactly as they were.
        Assert.Contains(@"<FilePath>D:\Gone\music.wav</FilePath>", xml);
        Assert.Contains(@"<RelativePath>..\gone\music.wav</RelativePath>", xml);
        Assert.Contains("<Name>Wedding Edit</Name>", xml);
    }

    // ─────────────── After Effects ───────────────

    private static byte[] Chunk(string id, byte[] data)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. size, .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
    }

    private static byte[] List(string type, params byte[][] children) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. children.SelectMany(c => c)]);

    private string WriteAfterEffects(string footage, string missing)
    {
        string alias(string p) => System.Text.Json.JsonSerializer.Serialize(new { fullpath = p, target_is_folder = false });
        var body = List("Fold",
            Chunk("Utf8", "Footage"u8.ToArray()),
            List("Item", Chunk("idta", new byte[17]), List("Pin ", List("Als2", Chunk("alas", Encoding.UTF8.GetBytes(alias(footage)))))),
            List("Item", Chunk("idta", new byte[17]), List("Pin ", List("Als2", Chunk("alas", Encoding.UTF8.GetBytes(alias(missing)))))),
            Chunk("cdta", new byte[33]));
        var root = Chunk("RIFX", [.. "Egg!"u8.ToArray(), .. Chunk("head", new byte[20]), .. body]);
        var path = Path.Combine(_dir, "Titles.aep");
        File.WriteAllBytes(path, root[..(root.Length - (root.Length % 2 == 1 ? 1 : 0))]);
        return path;
    }

    [Fact]
    public async Task An_After_Effects_project_reads_back_byte_for_byte_and_relinks()
    {
        var footage = Path.Combine(_dir, "assets", "plate.mov");
        Directory.CreateDirectory(Path.GetDirectoryName(footage)!);
        File.WriteAllBytes(footage, new byte[3000]);
        var project = WriteAfterEffects(footage, @"E:\Old\logo.psd");
        var bytes = File.ReadAllBytes(project);
        Assert.Equal(bytes, Rifx.Write(Rifx.Parse(bytes)!));

        var details = DesignDocuments.Read(project);
        Assert.Equal([footage, @"E:\Old\logo.psd"], details.Links);
        var plan = ProjectCollector.Plan(project);
        var folder = Path.Combine(_dir, "Titles Collected");
        var result = await ProjectCollector.CollectAsync(plan, folder, new CollectOptions());
        Assert.True(result.Relinked);
        var again = DesignDocuments.Read(result.Project);
        Assert.Equal([Path.Combine(folder, "Media", "plate.mov"), @"E:\Old\logo.psd"], again.Links);
        // Only the alias changed: the other chunks are untouched.
        var root = Rifx.Parse(File.ReadAllBytes(result.Project))!;
        Assert.Equal(33, root.Descendants().Single(c => c.Id == "cdta").Data.Length);
    }

    /// <summary>Real Photoshop and Illustrator files (copies): the words search indexes are found (counted, not shown).</summary>
    [Fact]
    public async Task Real_Photoshop_and_Illustrator_words_are_read_for_search()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ADOBE_SAMPLES");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_ADOBE_SAMPLES isn't set.");
            return;
        }
        var converters = Petal.App.EngineSetup.CreateConverters();
        foreach (var file in Directory.GetFiles(folder).Where(f => F.FromPath(f) is { } fmt && (fmt == F.Psd || fmt == F.Psb || fmt == F.Ai) && !Path.GetFileName(f).StartsWith("apple", StringComparison.Ordinal)))
        {
            var text = await Petal.Recipes.DocumentText.ReadAsync(file, converters, new ConversionSettings());
            int words = text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            // Files whose text was turned into outlines have no words left; those with fonts must give some.
            int fonts = F.FromPath(file) == F.Ai ? DesignDocuments.Read(file).Fonts.Count : Petal.Engines.Imaging.PsdFile.Read(file).Fonts.Count;
            output.WriteLine($"{Path.GetFileName(file)}: {words} words, {fonts} fonts");
            if (fonts > 0)
                Assert.True(words > 0, Path.GetFileName(file));
        }
    }

    /// <summary>Real Illustrator and EPS files (copies): their XMP gives application, artboards, fonts and links. Structure only.</summary>
    [Fact]
    public void Real_Illustrator_and_EPS_details()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ADOBE_SAMPLES");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_ADOBE_SAMPLES isn't set.");
            return;
        }
        foreach (var file in Directory.GetFiles(folder).Where(f => f.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".eps", StringComparison.OrdinalIgnoreCase)))
        {
            var d = DesignDocuments.Read(file);
            int installed = d.Fonts.Count(f => FontIndex.Find(f) is not null);
            output.WriteLine($"{Path.GetFileName(file)}: {d.Application ?? "?"}; {string.Join("; ", d.Facts.Select(f => $"{f.Label} {f.Value}"))}; fonts {d.Fonts.Count} ({installed} installed); links {d.Links.Count}; swatches {d.Swatches.Count}; preview {(d.Preview is null ? "no" : d.Preview.Length + " bytes")}");
            if (d.Preview is not null)
            {
                using var preview = new MagickImage(d.Preview);
                Assert.True(preview.Width > 0);
            }
        }
    }
}
