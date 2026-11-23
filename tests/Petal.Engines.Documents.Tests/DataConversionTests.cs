using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Data;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// JSON, JSON Lines, YAML, TOML, XML and vCard, on files of the kinds people have: a package.json, a
/// docker-compose.yml, a pyproject.toml, an RSS feed, contacts exported by Android and by an iPhone.
/// </summary>
public sealed class DataConversionTests
{
    private const string PackageJson = """
        {
          "name": "petal-site",
          "version": "2.4.0",
          "private": true,
          "description": "موقع الزهرة",
          "scripts": { "dev": "vite", "build": "vite build" },
          "dependencies": { "react": "^19.1.0", "zod": "3.24.2" },
          "engines": { "node": ">=20" },
          "keywords": ["site", "petal"],
          "port": 5173,
          "ratio": 1.50,
          "homepage": null
        }
        """;

    private const string Compose = """
        # docker-compose for the shop
        x-common: &common
          restart: unless-stopped
          environment:
            TZ: Europe/Istanbul
        services:
          web:
            <<: *common
            image: nginx:1.27
            ports:
              - "8080:80"
            healthcheck:
              test: ["CMD", "curl", "-f", "http://localhost"]
              interval: 30s
          db:
            <<: *common
            image: postgres:16
            environment:
              POSTGRES_PASSWORD: "123456"
              POSTGRES_DB: shop
            volumes: [db:/var/lib/postgresql/data]
        volumes:
          db: {}
        version: "3.9"
        enabled: yes
        replicas: 3
        """;

    private const string PyProject = """
        [project]
        name = "petal-tools"
        version = "0.3.1"
        requires-python = ">=3.11"
        dependencies = ["httpx>=0.27", "rich"]
        authors = [{ name = "Yahya", email = "dev@example.com" }]

        [project.scripts]
        petal = "petal_tools.cli:main"

        [tool.ruff]
        line-length = 100
        select = ["E", "F"]

        [[tool.mypy.overrides]]
        module = "httpx.*"
        ignore_missing_imports = true

        [build-system]
        requires = ["hatchling"]
        released = 2026-11-23T10:00:00Z
        """;

    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
          <channel>
            <title>DropWheel news</title>
            <atom:link href="https://example.com/feed.xml" rel="self" type="application/rss+xml"/>
            <item><title>1.24 is out</title><link>https://example.com/1-24</link><category>release</category><category>data</category></item>
            <item><title>تحديث مهم</title><link>https://example.com/ar</link></item>
          </channel>
        </rss>
        """;

    // Android's export (vCard 2.1): names in quoted-printable UTF-8, a folded soft line break, a photo.
    private const string AndroidContacts =
        "BEGIN:VCARD\r\nVERSION:2.1\r\n" +
        "N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=D8=A7=D9=84=D8=AE=D8=B7=D9=8A=D8=A8;=D8=A3=D8=AD=D9=85=D8=AF;;;\r\n" +
        "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=D8=A3=D8=AD=D9=85=D8=AF =D8=A7=D9=84=D8=AE=\r\n=D8=B7=D9=8A=D8=A8\r\n" +
        "TEL;CELL;PREF:0555 123 45 67\r\nTEL;HOME:+90 212 000 00 00\r\nEMAIL;HOME:ahmad@example.com\r\n" +
        "PHOTO;ENCODING=BASE64;JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD\r\n AAAAAAAAAAAAAAAAAAA\r\n\r\n" +
        "END:VCARD\r\n" +
        "BEGIN:VCARD\r\nVERSION:2.1\r\nN:Kaya;Elif;;;\r\nFN:Elif Kaya\r\nTEL;WORK:0212 555 00 11\r\nORG:Petal Ltd;Design\r\nBDAY:19900517\r\nEND:VCARD\r\n";

    // An iPhone's export (vCard 3.0): folded lines, groups, escaped commas and semicolons.
    private const string IPhoneContacts =
        "BEGIN:VCARD\nVERSION:3.0\nPRODID:-//Apple Inc.//iPhone OS 18.1//EN\nN:Martin;Claire;;;\nFN:Claire Martin\n" +
        "ORG:Atelier Claire\\, SARL;\nTITLE:Directrice\nitem1.EMAIL;type=INTERNET;type=pref:claire@example.fr\n" +
        "TEL;type=CELL;type=VOICE;type=pref:+33 6 12 34 56 78\nitem2.ADR;type=WORK:;;12 rue de la Paix;Paris;;75002;Fra\n nce\n" +
        "NOTE:Rencontrée au salon\\, à Lyon\\nRappeler en mars\nURL;type=pref:https://atelier.example.fr\nEND:VCARD\n";

    private static readonly DataConverter Converter = new();

    private static async Task<string> Convert(string text, FileFormat from, FileFormat to, string name = "sample")
    {
        var dir = Fixtures.Instance.NewOutputDir($"data-{from.Id}-{to.Id}");
        var input = Path.Combine(dir, $"{name}.{from.WriteExtension}");
        File.WriteAllText(input, text, new UTF8Encoding(false));
        var output = Path.Combine(dir, $"{name} out.{to.WriteExtension}");
        Assert.True(Converter.CanConvert(from, to), $"{from} → {to}");
        var result = await Converter.ConvertAsync(new ConversionJob(input, from, to, output, ConversionSettings.Default), null, CancellationToken.None);
        Assert.Equal(output, result.Primary);
        Assert.Empty(Directory.GetFiles(dir, "*.petal-tmp"));
        var bytes = File.ReadAllBytes(output);
        // Data files carry no byte order mark (CSV keeps one, as before, so Excel reads its Arabic).
        if (to.Family == FormatFamily.Data)
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "No byte order mark");
        return output;
    }

    private static JsonNode? Tree(string path, FileFormat format) => DataConverter.ReadTree(path, format);

    [Fact]
    public void Every_data_path_in_the_matrix_has_a_converter()
    {
        var registry = new ConverterRegistry();
        DocumentEngine.Register(registry, new DocumentEngineOptions { UseMicrosoftOffice = false, UseLibreOffice = false });
        var missing = ConversionMatrix.AllPaths().Where(p => p.From.Family == FormatFamily.Data || p.To.Family == FormatFamily.Data)
            .Where(p => registry.Find(p.From, p.To) is null).Select(p => $"{p.From} → {p.To}").ToList();
        Assert.Empty(missing);
        // 29 since workbooks with macros and templates (XLSM, XLTX) also become JSON.
        Assert.Equal(29, ConversionMatrix.AllPaths().Count(p => p.From.Family == FormatFamily.Data || p.To.Family == FormatFamily.Data));
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("toml")]
    [InlineData("xml")]
    public async Task Json_goes_out_and_comes_back_the_same(string via)
    {
        var format = F.FromId(via)!;
        var there = await Convert(PackageJson, F.Json, format);
        var back = await Convert(File.ReadAllText(there), format, F.Json);
        var original = JsonNode.Parse(PackageJson)!.AsObject();
        var returned = Tree(back, F.Json)!;
        if (via == "xml")
        {
            // XML has only text: the values come back as strings, under the root it was given.
            var root = returned["root"]!;
            Assert.Equal("petal-site", (string?)root["name"]);
            Assert.Equal("موقع الزهرة", (string?)root["description"]);
            Assert.Equal("5173", (string?)root["port"]);
            Assert.Equal(["site", "petal"], root["keywords"]!.AsArray().Select(k => (string?)k));
            return;
        }
        if (via == "toml")
        {
            // TOML has no null: "homepage" is left out; every other value comes back as it was.
            original.Remove("homepage");
            Assert.Equal(1.5, (double)returned["ratio"]!);
            original.Remove("ratio");
            returned.AsObject().Remove("ratio");
        }
        Assert.True(JsonNode.DeepEquals(original, returned), returned.ToJsonString());
        if (via == "yaml")
        {
            var yaml = File.ReadAllText(there);
            Assert.Contains("ratio: 1.50", yaml);              // its own digits
            Assert.Contains("version: 2.4.0", yaml);              // not a number in YAML 1.2, so it reads back as text
            Assert.Contains("private: true", yaml);
            Assert.Contains("description: موقع الزهرة", yaml);
        }
    }

    [Fact]
    public async Task Yaml_reads_as_yaml_1_2_merges_and_quotes_keep_text()
    {
        var json = await Convert(Compose, F.Yaml, F.Json, "docker-compose");
        var tree = Tree(json, F.Json)!;
        var web = tree["services"]!["web"]!;
        // "<<: *common" brings the shared keys in.
        Assert.Equal("unless-stopped", (string?)web["restart"]);
        Assert.Equal("Europe/Istanbul", (string?)web["environment"]!["TZ"]);
        // The service's own keys win over merged ones.
        Assert.Equal("shop", (string?)tree["services"]!["db"]!["environment"]!["POSTGRES_DB"]);
        Assert.Equal("123456", (string?)tree["services"]!["db"]!["environment"]!["POSTGRES_PASSWORD"]); // quoted: text
        Assert.Equal("3.9", (string?)tree["version"]);   // quoted: text
        Assert.Equal("yes", (string?)tree["enabled"]);   // YAML 1.2: "yes" is a word, not true
        Assert.Equal(3, (int)tree["replicas"]!);
        Assert.Equal("8080:80", (string?)web["ports"]![0]);
        Assert.Equal("30s", (string?)web["healthcheck"]!["interval"]);
        Assert.Empty(tree["volumes"]!["db"]!.AsObject());

        // And back: what YAML would read otherwise is quoted.
        var yaml = File.ReadAllText(await Convert(File.ReadAllText(json), F.Json, F.Yaml));
        Assert.Contains("POSTGRES_PASSWORD: \"123456\"", yaml);
        Assert.Contains("version: \"3.9\"", yaml);
        Assert.Contains("enabled: \"yes\"", yaml);
        Assert.Contains("replicas: 3", yaml);
        Assert.True(JsonNode.DeepEquals(tree, DataTree.ReadYaml(yaml)));
    }

    [Fact]
    public async Task Toml_tables_arrays_of_tables_and_dates()
    {
        var json = await Convert(PyProject, F.Toml, F.Json, "pyproject");
        var tree = Tree(json, F.Json)!;
        Assert.Equal("petal-tools", (string?)tree["project"]!["name"]);
        Assert.Equal("Yahya", (string?)tree["project"]!["authors"]![0]!["name"]);
        Assert.Equal("petal_tools.cli:main", (string?)tree["project"]!["scripts"]!["petal"]);
        Assert.Equal(100, (int)tree["tool"]!["ruff"]!["line-length"]!);
        Assert.True((bool)tree["tool"]!["mypy"]!["overrides"]![0]!["ignore_missing_imports"]!);
        Assert.StartsWith("2026-11-23T10:00:00", (string?)tree["build-system"]!["released"]);

        var yaml = await Convert(File.ReadAllText(json), F.Json, F.Yaml);
        var toml = await Convert(File.ReadAllText(yaml), F.Yaml, F.Toml);
        var again = DataTree.ReadToml(File.ReadAllText(toml))!;
        Assert.Equal("httpx.*", (string?)again["tool"]!["mypy"]!["overrides"]![0]!["module"]);
        Assert.Equal(["httpx>=0.27", "rich"], again["project"]!["dependencies"]!.AsArray().Select(d => (string?)d));
    }

    [Fact]
    public async Task Xml_attributes_repeats_and_text_and_back()
    {
        var json = await Convert(Rss, F.Xml, F.Json, "feed");
        var tree = Tree(json, F.Json)!;
        var channel = tree["rss"]!["channel"]!;
        Assert.Equal("2.0", (string?)tree["rss"]!["@version"]);
        Assert.Equal("https://example.com/feed.xml", (string?)channel["atom:link"]!["@href"]);
        var items = channel["item"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(["release", "data"], items[0]!["category"]!.AsArray().Select(c => (string?)c));
        Assert.Equal("تحديث مهم", (string?)items[1]!["title"]);

        var xml = File.ReadAllText(await Convert(File.ReadAllText(json), F.Json, F.Xml));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var again = DataTree.ReadXml(xml)!;
        Assert.Equal(2, again["rss"]!["channel"]!["item"]!.AsArray().Count);
        Assert.Equal("2.0", (string?)again["rss"]!["@version"]);
        Assert.Equal("تحديث مهم", (string?)again["rss"]!["channel"]!["item"]![1]!["title"]);

        var yaml = File.ReadAllText(await Convert(Rss, F.Xml, F.Yaml));
        Assert.Contains("\"@version\": \"2.0\"", yaml);
    }

    [Fact]
    public async Task Json_lines_both_ways_and_into_a_table()
    {
        const string log = "{\"time\":\"10:00\",\"level\":\"info\",\"msg\":\"started\"}\n\n{\"time\":\"10:01\",\"level\":\"warn\",\"msg\":\"slow\",\"ms\":812}\n";
        var json = await Convert(log, F.Jsonl, F.Json, "app");
        var array = Tree(json, F.Json)!.AsArray();
        Assert.Equal(2, array.Count);
        Assert.Equal(812, (int)array[1]!["ms"]!);
        var lines = File.ReadAllText(await Convert(File.ReadAllText(json), F.Json, F.Jsonl)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"time\":\"10:01\",\"level\":\"warn\",\"msg\":\"slow\",\"ms\":812}", lines[1]);

        var csv = File.ReadAllText(await Convert(log, F.Jsonl, F.Csv));
        Assert.Equal("time,level,msg,ms\n10:00,info,started,\n10:01,warn,slow,812\n", csv.Replace("\r\n", "\n"));

        var bad = await Assert.ThrowsAsync<DataShapeException>(() => Convert("{\"a\":1}\n{oops\n", F.Jsonl, F.Json));
        Assert.Contains("Line 2", bad.Message);
    }

    [Fact]
    public async Task Tables_become_records_and_records_tables()
    {
        const string csv = "id,name,price,in stock,code\r\n1,Mug,12.5,TRUE,007\r\n2,\"Plate, large\",8,FALSE,0042\r\n";
        var json = await Convert(csv, F.Csv, F.Json, "products");
        var records = Tree(json, F.Json)!.AsArray();
        Assert.Equal(2, records.Count);
        Assert.Equal(1, (int)records[0]!["id"]!);
        Assert.Equal(12.5, (double)records[0]!["price"]!);
        Assert.Equal("Plate, large", (string?)records[1]!["name"]);
        Assert.Equal("007", (string?)records[0]!["code"]);  // leading zeros: text

        var xlsx = await Convert(File.ReadAllText(json), F.Json, F.Xlsx, "products");
        using (var workbook = new XLWorkbook(xlsx))
        {
            var sheet = workbook.Worksheets.First();
            Assert.Equal("price", sheet.Cell(1, 3).GetString());
            Assert.Equal(12.5, sheet.Cell(2, 3).GetDouble());
            Assert.Equal("0042", sheet.Cell(3, 5).GetString());
        }
        // And back to CSV: as it was.
        var back = File.ReadAllText(await Convert(File.ReadAllText(json), F.Json, F.Csv)).Replace("\r\n", "\n");
        Assert.Equal(csv.Replace("\r\n", "\n"), back);

        // Nested values stay in their cell as JSON; a YAML of one list of records gives that list.
        var nested = File.ReadAllText(await Convert("{\"orders\":[{\"id\":1,\"items\":[\"a\",\"b\"]},{\"id\":2,\"note\":\"x\"}]}", F.Json, F.Csv)).Replace("\r\n", "\n");
        Assert.Equal("id,items,note\n1,\"[\"\"a\"\",\"\"b\"\"]\",\n2,,x\n", nested);
        var shape = await Assert.ThrowsAsync<DataShapeException>(() => Convert("42", F.Json, F.Csv));
        Assert.Contains("single value", shape.Message);
        var toml = await Assert.ThrowsAsync<DataShapeException>(() => Convert("[1,2]", F.Json, F.Toml));
        Assert.Contains("TOML needs named values", toml.Message);
    }

    [Fact]
    public async Task Contacts_from_android_and_from_an_iphone()
    {
        var json = Tree(await Convert(AndroidContacts + IPhoneContacts, F.Vcf, F.Json, "contacts"), F.Json)!.AsArray();
        Assert.Equal(3, json.Count);
        var ahmad = json[0]!;
        Assert.Equal("أحمد الخطيب", (string?)ahmad["name"]);
        Assert.Equal("أحمد", (string?)ahmad["firstName"]);
        Assert.Equal("الخطيب", (string?)ahmad["lastName"]);
        Assert.Equal("0555 123 45 67", (string?)ahmad["phones"]![0]!["number"]);
        Assert.Equal("mobile", (string?)ahmad["phones"]![0]!["type"]);
        Assert.Equal("home", (string?)ahmad["phones"]![1]!["type"]);
        Assert.Equal("ahmad@example.com", (string?)ahmad["emails"]![0]!["address"]);
        Assert.Null(ahmad["photo"]);
        var elif = json[1]!;
        Assert.Equal("Petal Ltd, Design", (string?)elif["organization"]);
        Assert.Equal("1990-05-17", (string?)elif["birthday"]);
        var claire = json[2]!;
        Assert.Equal("Atelier Claire, SARL", (string?)claire["organization"]);
        Assert.Equal("12 rue de la Paix, Paris, 75002, France", (string?)claire["addresses"]![0]);
        Assert.Equal("Rencontrée au salon, à Lyon\nRappeler en mars", (string?)claire["note"]);
        Assert.Equal("claire@example.fr", (string?)claire["emails"]![0]!["address"]);

        var csv = File.ReadAllText(await Convert(AndroidContacts + IPhoneContacts, F.Vcf, F.Csv, "contacts"), Encoding.UTF8);
        var header = csv.Split('\n')[0].Trim();
        Assert.Equal("Name,First name,Last name,Phone,Phone type,Phone 2,Phone 2 type,E-mail,Organization,Title,Address,Birthday,Note,Website", header);
        Assert.Contains("أحمد الخطيب,أحمد,الخطيب,0555 123 45 67,mobile,+90 212 000 00 00,home,ahmad@example.com", csv);

        var xlsx = await Convert(AndroidContacts, F.Vcf, F.Xlsx, "contacts");
        using var workbook = new XLWorkbook(xlsx);
        Assert.Equal("0555 123 45 67", workbook.Worksheets.First().Cell(2, 4).GetString());

        var none = await Assert.ThrowsAsync<DataShapeException>(() => Convert("hello", F.Vcf, F.Json));
        Assert.Contains("no contacts", none.Message);
    }

    [Theory]
    [InlineData("json", "{\"a\": [1, 2,", "valid JSON")]
    [InlineData("yaml", "a: [1, 2\nb: c", "valid YAML")]
    [InlineData("toml", "a = [1, 2\n", "valid TOML")]
    [InlineData("xml", "<a><b></a>", "valid XML")]
    public async Task Broken_files_say_so(string id, string text, string message)
    {
        var from = F.FromId(id)!;
        var to = id == "json" ? F.Yaml : F.Json;
        var error = await Assert.ThrowsAsync<DataShapeException>(() => Convert(text, from, to));
        Assert.Contains(message, error.Message);
    }
}
