using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Petal.Recipes.Study;

/// <summary>
/// Writes an Anki package (.apkg): a zip holding one SQLite collection in Anki's version 11 layout, which every Anki since
/// 2.1 imports. Two note types: question and answer, and a question with its choices. Text reading from the right is
/// laid out by the card, so Arabic, Persian and Urdu show properly.
/// </summary>
public static class AnkiDeck
{
    // Fixed, so a deck made again updates the same note types in Anki instead of adding new ones.
    private const long BasicModel = 1763100000001;
    private const long QuizModel = 1763100000002;

    private const string Style = ".card { font-family: 'Segoe UI', Arial, sans-serif; font-size: 22px; text-align: center; color: #1c1c1e; background: #fff; line-height: 1.5; } .night_mode .card, .card.nightMode { color: #f2f2f7; background: #1c1c1e; } .where { font-size: 14px; opacity: .6; margin-top: 14px; } .choices { display: inline-block; text-align: start; } .why { font-size: 18px; margin-top: 10px; }";

    private const string Schema = """
        CREATE TABLE col (id integer primary key, crt integer not null, mod integer not null, scm integer not null, ver integer not null, dty integer not null, usn integer not null, ls integer not null, conf text not null, models text not null, decks text not null, dconf text not null, tags text not null);
        CREATE TABLE notes (id integer primary key, guid text not null, mid integer not null, mod integer not null, usn integer not null, tags text not null, flds text not null, sfld integer not null, csum integer not null, flags integer not null, data text not null);
        CREATE TABLE cards (id integer primary key, nid integer not null, did integer not null, ord integer not null, mod integer not null, usn integer not null, type integer not null, queue integer not null, due integer not null, ivl integer not null, factor integer not null, reps integer not null, lapses integer not null, left integer not null, odue integer not null, odid integer not null, flags integer not null, data text not null);
        CREATE TABLE revlog (id integer primary key, cid integer not null, usn integer not null, ease integer not null, ivl integer not null, lastIvl integer not null, factor integer not null, time integer not null, type integer not null);
        CREATE TABLE graves (usn integer not null, oid integer not null, type integer not null);
        CREATE INDEX ix_notes_usn on notes (usn);
        CREATE INDEX ix_cards_usn on cards (usn);
        CREATE INDEX ix_revlog_usn on revlog (usn);
        CREATE INDEX ix_cards_nid on cards (nid);
        CREATE INDEX ix_cards_sched on cards (did, queue, due);
        CREATE INDEX ix_revlog_cid on revlog (cid);
        CREATE INDEX ix_notes_csum on notes (csum);
        """;

    /// <summary>Writes the pack's cards and questions as a deck named <paramref name="deckName"/>.</summary>
    public static void Write(string path, string deckName, IReadOnlyList<StudyCard> cards, IReadOnlyList<StudyQuestion> quiz)
    {
        var work = Path.Combine(Path.GetTempPath(), "Petal", "anki-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var database = Path.Combine(work, "collection.anki2");
            WriteCollection(database, deckName, cards, quiz);
            var temporary = path + ".tmp";
            File.Delete(temporary);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(database, "collection.anki2");
                var media = zip.CreateEntry("media");
                using var stream = media.Open();
                stream.Write("{}"u8);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static void WriteCollection(string database, string deckName, IReadOnlyList<StudyCard> cards, IReadOnlyList<StudyQuestion> quiz)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long ms = now * 1000;
        long deck = DeckId(deckName);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        void Run(string sql, params object[] values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (int i = 0; i < values.Length; i++)
                command.Parameters.AddWithValue("$" + i, values[i]);
            command.ExecuteNonQuery();
        }
        Run(Schema);
        Run("INSERT INTO col VALUES(1,$0,$1,$2,11,0,0,0,$3,$4,$5,$6,'{}')", now, ms, ms, Conf(deck), Models(deck, now), Decks(deck, deckName, now), DeckOptions());

        long id = ms;
        int due = 1;
        void Note(long model, params string[] fields)
        {
            long note = ++id;
            long card = ++id;
            Run("INSERT INTO notes VALUES($0,$1,$2,$3,-1,'',$4,$5,$6,0,'')", note, NoteGuid(fields), model, now, string.Join((char)0x1f, fields), Sort(fields[0]), Checksum(fields[0]));
            Run("INSERT INTO cards VALUES($0,$1,$2,0,$3,-1,0,0,$4,0,0,0,0,0,0,0,0,'')", card, note, deck, now, due++);
        }
        foreach (var c in cards)
            Note(BasicModel, Html(c.Front), Html(c.Back), Html(c.Where));
        foreach (var q in quiz)
            Note(QuizModel, Html(q.Question), Choices(q), Html(q.Options[q.Correct]), Html(q.Why), Html(q.Where));
        transaction.Commit();
    }

    /// <summary>Text as a card field: HTML is what a field holds, so what the text says stays what it says.</summary>
    internal static string Html(string text) => WebUtility.HtmlEncode(text ?? "").Replace("\r\n", "<br>").Replace("\n", "<br>");

    private static string Choices(StudyQuestion q)
    {
        var sb = new StringBuilder("<ol class=\"choices\" type=\"A\" dir=\"auto\">");
        foreach (var option in q.Options)
            sb.Append("<li>").Append(Html(option)).Append("</li>");
        return sb.Append("</ol>").ToString();
    }

    /// <summary>What Anki sorts and looks for duplicates by: the text of the first field.</summary>
    private static string Sort(string field) => WebUtility.HtmlDecode(field).Replace("<br>", " ");

    private static long Checksum(string field)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Sort(field)));
        return long.Parse(Convert.ToHexString(hash)[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>The same note made again is the same note to Anki, and isn't added twice.</summary>
    private static string NoteGuid(string[] fields) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join((char)0x1f, fields))))[..12];

    private static long DeckId(string name) =>
        1_000_000_000_000 + (long)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name))) % 900_000_000_000UL);

    private static string Conf(long deck) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["activeDecks"] = new[] { deck },
        ["addToCur"] = true,
        ["collapseTime"] = 1200,
        ["curDeck"] = deck,
        ["curModel"] = BasicModel.ToString(CultureInfo.InvariantCulture),
        ["dueCounts"] = true,
        ["estTimes"] = true,
        ["newBury"] = true,
        ["newSpread"] = 0,
        ["nextPos"] = 1,
        ["sortBackwards"] = false,
        ["sortType"] = "noteFld",
        ["timeLim"] = 0,
    });

    private static Dictionary<string, object> Deck(long id, string name, long now) => new()
    {
        ["collapsed"] = false,
        ["conf"] = 1,
        ["desc"] = "",
        ["dyn"] = 0,
        ["extendNew"] = 10,
        ["extendRev"] = 50,
        ["id"] = id,
        ["lrnToday"] = new[] { 0, 0 },
        ["mod"] = now,
        ["name"] = name,
        ["newToday"] = new[] { 0, 0 },
        ["revToday"] = new[] { 0, 0 },
        ["timeToday"] = new[] { 0, 0 },
        ["usn"] = -1,
    };

    private static string Decks(long id, string name, long now) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["1"] = Deck(1, "Default", now),
        [id.ToString(CultureInfo.InvariantCulture)] = Deck(id, name, now),
    });

    private static string DeckOptions() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["1"] = new Dictionary<string, object>
        {
            ["autoplay"] = true,
            ["id"] = 1,
            ["lapse"] = new Dictionary<string, object> { ["delays"] = new[] { 10 }, ["leechAction"] = 0, ["leechFails"] = 8, ["minInt"] = 1, ["mult"] = 0 },
            ["maxTaken"] = 60,
            ["mod"] = 0,
            ["name"] = "Default",
            ["new"] = new Dictionary<string, object> { ["bury"] = true, ["delays"] = new[] { 1, 10 }, ["initialFactor"] = 2500, ["ints"] = new[] { 1, 4, 7 }, ["order"] = 1, ["perDay"] = 20, ["separate"] = true },
            ["replayq"] = true,
            ["rev"] = new Dictionary<string, object> { ["bury"] = true, ["ease4"] = 1.3, ["fuzz"] = 0.05, ["ivlFct"] = 1, ["maxIvl"] = 36500, ["minSpace"] = 1, ["perDay"] = 100 },
            ["timer"] = 0,
            ["usn"] = 0,
        },
    });

    private static Dictionary<string, object?> Field(string name, int order) => new()
    {
        ["name"] = name,
        ["ord"] = order,
        ["font"] = "Arial",
        ["media"] = Array.Empty<string>(),
        ["rtl"] = false,
        ["size"] = 20,
        ["sticky"] = false,
    };

    private static Dictionary<string, object?> Template(string front, string back) => new()
    {
        ["name"] = "Card 1",
        ["qfmt"] = front,
        ["afmt"] = back,
        ["ord"] = 0,
        ["bafmt"] = "",
        ["bqfmt"] = "",
        ["bfont"] = "",
        ["bsize"] = 0,
        ["did"] = null,
    };

    private static Dictionary<string, object?> Model(long id, string name, long deck, long now, string[] fields, string front, string back) => new()
    {
        ["css"] = Style,
        ["did"] = deck,
        ["flds"] = fields.Select(Field).ToList(),
        ["id"] = id.ToString(CultureInfo.InvariantCulture),
        ["latexPost"] = "\\end{document}",
        ["latexPre"] = "\\documentclass[12pt]{article}\n\\special{papersize=3in,5in}\n\\usepackage[utf8]{inputenc}\n\\usepackage{amssymb,amsmath}\n\\pagestyle{empty}\n\\setlength{\\parindent}{0in}\n\\begin{document}\n",
        ["latexsvg"] = false,
        ["mod"] = now,
        ["name"] = name,
        ["req"] = new object[] { new object[] { 0, "all", new[] { 0 } } },
        ["sortf"] = 0,
        ["tags"] = Array.Empty<string>(),
        ["tmpls"] = new[] { Template(front, back) },
        ["type"] = 0,
        ["usn"] = -1,
        ["vers"] = Array.Empty<int>(),
    };

    private static string Models(long deck, long now) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        [BasicModel.ToString(CultureInfo.InvariantCulture)] = Model(BasicModel, "DropWheel Card", deck, now, ["Front", "Back", "Where"],
            "<div dir=\"auto\">{{Front}}</div>",
            "<div dir=\"auto\">{{FrontSide}}<hr id=\"answer\">{{Back}}<div class=\"where\">{{Where}}</div></div>"),
        [QuizModel.ToString(CultureInfo.InvariantCulture)] = Model(QuizModel, "DropWheel Question", deck, now, ["Question", "Choices", "Answer", "Why", "Where"],
            "<div dir=\"auto\">{{Question}}<br><br>{{Choices}}</div>",
            "<div dir=\"auto\">{{FrontSide}}<hr id=\"answer\"><b>{{Answer}}</b><div class=\"why\">{{Why}}</div><div class=\"where\">{{Where}}</div></div>"),
    });
}
