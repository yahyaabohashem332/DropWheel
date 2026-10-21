using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Petal.Engines.Imaging;

namespace Petal.Recipes.Search;

public enum IndexedKind
{
    Picture,
    Video,
    Document,
    Audio,
    Other,
}

/// <param name="ByMeaning">Found by what the picture or video shows, rather than by words in it or its name.</param>
/// <param name="Snippet">For word matches: the words around the match, with the match between « and ».</param>
public sealed record SearchHit(string Path, IndexedKind Kind, double Score, bool ByMeaning, string? Snippet = null);

public sealed record SearchResults(IReadOnlyList<SearchHit> ByMeaning, IReadOnlyList<SearchHit> ByWords);

/// <summary>Counts for the status line.</summary>
public readonly record struct IndexStatus(int Files, int Waiting, int Pictures);

/// <summary>
/// The search index, kept on this PC (%LOCALAPPDATA%\Petal\search.db): every file's name and the
/// text inside it in a full-text table, and for pictures and videos what they show, as a CLIP
/// vector. Nothing in it ever leaves the PC.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    private const int Pending = 0, Done = 1, Failed = 2;
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();
    private List<(long Id, float[] Vector)>? _vectors;

    public static string DefaultPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "search.db");

    public SearchIndex(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                size INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                state INTEGER NOT NULL,
                seen INTEGER NOT NULL DEFAULT 0,
                vector BLOB);
            CREATE INDEX IF NOT EXISTS files_state ON files(state);
            CREATE VIRTUAL TABLE IF NOT EXISTS words USING fts5(name, body, tokenize = 'unicode61 remove_diacritics 2');
            """);
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IndexStatus Status()
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT COUNT(*), SUM(state = 0), SUM(vector IS NOT NULL) FROM files";
            using var reader = command.ExecuteReader();
            reader.Read();
            return new IndexStatus(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
        }
    }

    /// <summary>
    /// Records the files found in a scan. New and changed files wait to be read; files from
    /// <paramref name="roots"/> that weren't found any more are forgotten.
    /// </summary>
    public void Update(IReadOnlyCollection<FileInfo> files, IReadOnlyCollection<string> roots)
    {
        lock (_gate)
        {
            long scan = DateTime.UtcNow.Ticks;
            using var transaction = _db.BeginTransaction();
            using var upsert = _db.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO files (path, size, modified, kind, state, seen) VALUES ($path, $size, $modified, $kind, 0, $scan)
                ON CONFLICT(path) DO UPDATE SET
                    state = CASE WHEN files.size = excluded.size AND files.modified = excluded.modified THEN files.state ELSE 0 END,
                    vector = CASE WHEN files.size = excluded.size AND files.modified = excluded.modified THEN files.vector ELSE NULL END,
                    size = excluded.size, modified = excluded.modified, kind = excluded.kind, seen = excluded.seen
                """;
            var path = upsert.Parameters.Add("$path", SqliteType.Text);
            var size = upsert.Parameters.Add("$size", SqliteType.Integer);
            var modified = upsert.Parameters.Add("$modified", SqliteType.Integer);
            var kind = upsert.Parameters.Add("$kind", SqliteType.Integer);
            upsert.Parameters.AddWithValue("$scan", scan);
            foreach (var file in files)
            {
                path.Value = file.FullName;
                size.Value = file.Length;
                modified.Value = file.LastWriteTimeUtc.Ticks;
                kind.Value = (int)KindOf(file.FullName);
                upsert.ExecuteNonQuery();
            }
            // Gone from the folders that were scanned: forget them (and their words).
            foreach (var root in roots)
            {
                using var forget = _db.CreateCommand();
                forget.Transaction = transaction;
                forget.CommandText = """
                    DELETE FROM words WHERE rowid IN (SELECT id FROM files WHERE seen <> $scan AND (path LIKE $prefix ESCAPE '\'));
                    DELETE FROM files WHERE seen <> $scan AND (path LIKE $prefix ESCAPE '\');
                    """;
                forget.Parameters.AddWithValue("$scan", scan);
                forget.Parameters.AddWithValue("$prefix", Like(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar) + "%");
                forget.ExecuteNonQuery();
            }
            transaction.Commit();
            _vectors = null;
        }
    }

    private static string Like(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public static IndexedKind KindOf(string path) => Petal.Core.Formats.Formats.FromPath(path)?.Family switch
    {
        Petal.Core.Formats.FormatFamily.Image or Petal.Core.Formats.FormatFamily.Vector => IndexedKind.Picture,
        Petal.Core.Formats.FormatFamily.Video => IndexedKind.Video,
        Petal.Core.Formats.FormatFamily.Audio => IndexedKind.Audio,
        Petal.Core.Formats.FormatFamily.Pdf or Petal.Core.Formats.FormatFamily.Document or Petal.Core.Formats.FormatFamily.Text
            or Petal.Core.Formats.FormatFamily.Ebook or Petal.Core.Formats.FormatFamily.Slides or Petal.Core.Formats.FormatFamily.Spreadsheet
            or Petal.Core.Formats.FormatFamily.Subtitle => IndexedKind.Document,
        _ => IndexedKind.Other,
    };

    /// <summary>Files waiting to be read: pictures first (quick, and what meaning search is for).</summary>
    public IReadOnlyList<(long Id, string Path, IndexedKind Kind)> Waiting(int count)
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT id, path, kind FROM files WHERE state = 0 ORDER BY kind = 0 DESC, modified DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            using var reader = command.ExecuteReader();
            var list = new List<(long, string, IndexedKind)>();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1), (IndexedKind)reader.GetInt32(2)));
            return list;
        }
    }

    /// <summary>Stores what was read from a file: the words in it and, for pictures and videos, what it shows.</summary>
    public void Store(long id, string path, string? body, float[]? vector, bool failed = false)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using (var update = _db.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE files SET state = $state, vector = $vector WHERE id = $id";
                update.Parameters.AddWithValue("$state", failed ? Failed : Done);
                update.Parameters.AddWithValue("$vector", vector is null ? DBNull.Value : MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }
            using (var words = _db.CreateCommand())
            {
                words.Transaction = transaction;
                words.CommandText = "DELETE FROM words WHERE rowid = $id; INSERT INTO words (rowid, name, body) VALUES ($id, $name, $body);";
                words.Parameters.AddWithValue("$id", id);
                words.Parameters.AddWithValue("$name", Words(NameWords(path)));
                words.Parameters.AddWithValue("$body", body is null ? "" : Words(body.Length > 200_000 ? body[..200_000] : body));
                words.ExecuteNonQuery();
            }
            transaction.Commit();
            if (vector is not null)
                _vectors?.Add((id, vector));
        }
    }

    /// <summary>A file's name and folder as words: "IMG_2041 Holiday-2024" → "IMG 2041 Holiday 2024".</summary>
    private static string NameWords(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        return $"{name} {folder} {Path.GetExtension(path).TrimStart('.')}";
    }

    /// <summary>Text as the index stores and searches it: Arabic letter forms folded, separators as spaces.</summary>
    internal static string Words(string text)
    {
        var folded = AskPetal.Fold(text);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString();
    }

    /// <summary>Pictures and videos read before the meaning models were here wait to be read again.</summary>
    public void ReadAgainWithoutMeaning()
    {
        lock (_gate)
            Execute("UPDATE files SET state = 0 WHERE kind IN (0, 1) AND vector IS NULL");
    }

    /// <summary>Starts over: every file is read again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Execute("DELETE FROM words; DELETE FROM files;");
            _vectors = null;
        }
    }

    public SearchResults Search(string query, float[]? meaning, int limit = 40) =>
        new(meaning is null ? [] : SearchMeaning(meaning, limit), SearchWords(query, limit));

    private List<SearchHit> SearchWords(string query, int limit)
    {
        var terms = Words(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(12).ToList();
        if (terms.Count == 0)
            return [];
        lock (_gate)
        {
            // Every word (as the start of a word), else any of them.
            foreach (var joiner in new[] { " AND ", " OR " })
            {
                var match = string.Join(joiner, terms.Select(t => $"\"{t}\"*"));
                using var command = _db.CreateCommand();
                command.CommandText = """
                    SELECT f.path, f.kind, bm25(words, 4.0, 1.0) AS rank, snippet(words, 1, '«', '»', '…', 10)
                    FROM words JOIN files f ON f.id = words.rowid
                    WHERE words MATCH $match ORDER BY rank LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$match", match);
                command.Parameters.AddWithValue("$limit", limit);
                using var reader = command.ExecuteReader();
                var hits = new List<SearchHit>();
                while (reader.Read())
                {
                    var snippet = reader.IsDBNull(3) ? null : reader.GetString(3);
                    hits.Add(new SearchHit(reader.GetString(0), (IndexedKind)reader.GetInt32(1), -reader.GetDouble(2), false,
                        snippet is { Length: > 0 } && snippet.Contains('«') ? snippet : null));
                }
                if (hits.Count > 0 || terms.Count == 1)
                    return hits.Where(h => File.Exists(h.Path)).ToList();
            }
            return [];
        }
    }

    /// <summary>
    /// Pictures and videos closest in meaning: above CLIP's chance level, and near the best match
    /// (a search for "cats" shows the cats, not every picture).
    /// </summary>
    private List<SearchHit> SearchMeaning(float[] meaning, int limit)
    {
        List<(long Id, float[] Vector)> vectors;
        lock (_gate)
            vectors = _vectors ??= LoadVectors();
        if (vectors.Count == 0)
            return [];
        var scored = vectors.Select(v => (v.Id, Score: Meaning.Similarity(meaning, v.Vector))).OrderByDescending(v => v.Score).Take(limit * 2).ToList();
        float best = scored[0].Score;
        var chosen = scored.Where(s => s.Score >= 0.24f && s.Score >= best - 0.045f).Take(limit).ToList();
        if (chosen.Count == 0)
            return [];
        var paths = new Dictionary<long, (string Path, IndexedKind Kind)>();
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT id, path, kind FROM files WHERE id IN ({string.Join(",", chosen.Select(c => c.Id))})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                paths[reader.GetInt64(0)] = (reader.GetString(1), (IndexedKind)reader.GetInt32(2));
        }
        return chosen.Where(c => paths.ContainsKey(c.Id) && File.Exists(paths[c.Id].Path))
            .Select(c => new SearchHit(paths[c.Id].Path, paths[c.Id].Kind, c.Score, true)).ToList();
    }

    private List<(long, float[])> LoadVectors()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT id, vector FROM files WHERE vector IS NOT NULL";
        using var reader = command.ExecuteReader();
        var list = new List<(long, float[])>();
        while (reader.Read())
        {
            var bytes = (byte[])reader[1];
            list.Add((reader.GetInt64(0), MemoryMarshal.Cast<byte, float>(bytes).ToArray()));
        }
        return list;
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }
}
