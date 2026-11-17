using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Core.Formats;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Engines.Documents;
using Petal.Recipes.Cloud;
using Petal.Recipes.Questions;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Study;

/// <summary>A passage given to the model at once: its words, and the places they are from, in order.</summary>
public sealed record StudyUnit(string Text, IReadOnlyList<PlacedText> Places)
{
    public PlacedText First => Places[0];

    public PlacedText Last => Places[^1];
}

/// <summary>
/// A part of the study file to be: a section of the file (the chapter it is in, its heading; "" when the file gives
/// none) and the passages it is explained in. <see cref="Suggested"/> is false for what there is nothing to learn
/// from: contents, an index, references.
/// </summary>
public sealed record StudyPartPlan(int Number, string Chapter, string Title, IReadOnlyList<StudyUnit> Units, bool Suggested = true)
{
    public PlacedText First => Units[0].First;

    public PlacedText Last => Units[^1].Last;
}

/// <summary>What a file comes to before it is explained: its parts, whether they are the file's own (or only a few pages at a time), its language and the title it gives itself.</summary>
public sealed record StudyPlan(IReadOnlyList<StudyPartPlan> Parts, bool Structured, string Language, string? Title, bool Recording);

/// <summary>A passage explained: its title, where it is, its key ideas, the explanation and a worked example ("" for none); or, when it couldn't be explained, the file's own words.</summary>
public sealed record StudyExplained(string Title, string Where, IReadOnlyList<string> Ideas, string Explanation, string Example, string Original = "");

/// <summary>A term of the glossary: what it means, where in the file, and the part it is explained in.</summary>
public sealed record StudyTerm(string Term, string Meaning, string Where, int Part);

/// <summary>A part of the study file as it is written.</summary>
public sealed record StudyPart(int Number, string Chapter, string Title, string Where, IReadOnlyList<StudyExplained> Passages, IReadOnlyList<StudyCard> Cards, IReadOnlyList<StudyQuestion> Quiz)
{
    /// <summary>Its diagrams: steps, a cycle, dates, an idea and its branches, a table that compares.</summary>
    public IReadOnlyList<StudyVisual> Visuals { get; init; } = [];

    /// <summary>Its activities: to put in order, match, sort into groups, fill in, or say true or false.</summary>
    public IReadOnlyList<StudyActivity> Activities { get; init; } = [];
}

/// <summary>A chapter: its title, what it comes to ("" when it is too short to need it), and its parts' numbers.</summary>
public sealed record StudyChapter(string Title, string Summary, IReadOnlyList<int> Parts);

/// <summary>Everything the study file says.</summary>
public sealed record StudyBook(
    string Title, string Source, string Language, string SourceLanguage, string Overview, IReadOnlyList<string> Goals,
    IReadOnlyList<StudyChapter> Chapters, IReadOnlyList<StudyPart> Parts, IReadOnlyList<StudyTerm> Terms,
    string Writer, DateTime Made, bool Recording)
{
    /// <summary>A sample: one part made to see how the study file will look, before the whole of it.</summary>
    public bool Sample { get; init; }
}

/// <summary>How far the making is: passages done of all, the part being explained, and about how long the rest will take (once that can be told).</summary>
public sealed record StudyProgress(int Done, int Total, string Part, TimeSpan? Left);

/// <summary>
/// An interactive study file from a book, course material, lecture notes or a recorded lecture. The file is read with
/// its own structure (<see cref="DocumentOutline"/>: a PDF's bookmarks or headings, a document's headings, a
/// presentation's sections; a recording a few minutes at a time) and each part explained a passage at a time, as a
/// teacher would: its key ideas, a fuller and simpler explanation, a worked example, its terms, cards and questions, all
/// from the file's own words and each saying where in the file it comes from. Chapters and the whole are then summed
/// up. What the model wrote for a passage is kept, so a long book stopped half way carries on where it was.
/// </summary>
public static partial class StudyFile
{
    /// <summary>The most characters of a file given at once: on this PC the model holds a few pages, and a cloud model a chapter.</summary>
    internal const int LocalStretch = 2800;

    internal const int CloudStretch = 9000;

    /// <summary>A section shorter than this is joined to the one beside it: explaining a few lines alone is no help.</summary>
    internal const int ShortestPart = 700;

    private const int Version = 1;

    /// <summary>Where what the model wrote is kept.</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "study");

    /// <summary>Passages kept at most; the oldest are let go.</summary>
    private const int Remembered = 6000;

    /// <summary>For tests: what writes in place of the model (instruction, text, the start of the reply) → the reply.</summary>
    internal static Func<string, string, string, Task<string>>? TestWriter { get; set; }

    public static IReadOnlyList<ModelInfo> Models => StudyMaker.Models;

    public static bool Ready => StudyMaker.Ready;

    /// <summary>Who writes: the cloud model on the user's key, or the model on this PC (its file's name, so another model writes afresh).</summary>
    public static string WriterId => CloudAi.CanWrite ? $"{CloudAi.Writing!.Provider}:{CloudAi.Writing.Model}" : Path.GetFileName(ModelStore.Find(Models[1]) ?? Models[1].FileName);

    /// <summary>The writer by name, for the study file to say who wrote it.</summary>
    public static string WriterName => CloudAi.CanWrite ? $"{CloudAi.Name(CloudAi.Writing!.Provider)} ({CloudAi.Writing.Model})" : Models[1].Name;

    public static bool CanRead(FileFormat format) =>
        AskFile.IsRecording(format) || format == Formats.Pdf || format == Formats.Pptx || format == Formats.Ppt
        || format.Family is FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook && DocumentPlaces.CanRead(format);

    // ───────────── reading ─────────────

    /// <summary>
    /// A file read for studying: a document with its parts; a recording heard with <paramref name="speechModel"/>
    /// (kept, as Ask a File keeps it) and parted a few minutes at a time.
    /// </summary>
    public static async Task<StudyPlan> ReadAsync(string path, string? speechModel, bool cloud, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var format = Formats.FromPath(path) ?? throw new InvalidOperationException("DropWheel doesn't know this kind of file.");
        if (AskFile.IsRecording(format))
        {
            var heard = await AskFile.WordsAsync(path, speechModel ?? throw new InvalidOperationException("The speech model isn't downloaded yet."), progress, cancellationToken).ConfigureAwait(false);
            if (heard.Count == 0)
                throw new InvalidOperationException("No speech was found in this file.");
            return Plan(new DocumentReading(heard, null, null), cloud, recording: true);
        }
        return Plan(await ReadDocumentAsync(path, progress, cancellationToken).ConfigureAwait(false), cloud);
    }

    /// <summary>
    /// A document read for studying, with its parts. A document Word, PowerPoint or LibreOffice lays out is kept as a PDF
    /// beside what the model wrote, so reading it again is quick.
    /// </summary>
    public static async Task<DocumentReading> ReadDocumentAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var format = Formats.FromPath(path) ?? throw new InvalidOperationException("DropWheel doesn't know this kind of file.");
        Directory.CreateDirectory(CacheFolder);
        var laidOut = Path.Combine(CacheFolder, AskFile.Key(path, null) + ".pdf");
        var reading = await DocumentOutline.ReadAsync(path, format, laidOut, progress, cancellationToken).ConfigureAwait(false);
        if (reading.Places.Count == 0)
            throw new InvalidOperationException("There are no words to read in this file.");
        return reading;
    }

    /// <summary>The parts a reading comes to: its sections (the short ones joined), each in passages a model can hold; or, with none, passages that are parts of their own.</summary>
    public static StudyPlan Plan(DocumentReading reading, bool cloud, bool recording = false)
    {
        int stretch = cloud ? CloudStretch : LocalStretch;
        var parts = new List<StudyPartPlan>();
        bool structured = reading.Sections is { Count: >= 2 };
        if (structured)
        {
            foreach (var section in Joined(reading.Sections!))
            {
                var units = Units(section.Places, stretch);
                if (units.Count > 0)
                    parts.Add(new StudyPartPlan(parts.Count + 1, section.Chapter, section.Title, units, Worth(section, parts.Count == 0)));
            }
        }
        if (parts.Count < 2)
        {
            structured = false;
            parts = Units(reading.Places, stretch).Select((u, i) => new StudyPartPlan(i + 1, "", "", [u])).ToList();
        }
        var language = StudyMaker.LanguageOf(reading.Places);
        return new StudyPlan(parts, structured, language, reading.Title, recording);
    }

    /// <summary>
    /// Whether a part is worth explaining, to tick it at first: not a book's front and back matter (contents, preface,
    /// index, references) nor the end of a chapter that only lists, sums up or asks (key terms, its summary, review
    /// questions), which the study file has its own of; not the pages before a book's first heading (cover, title, copyright).
    /// </summary>
    internal static bool Worth(DocumentSection section, bool first) =>
        !(first && section.Title.Length == 0 && section.Chapter.Length == 0) && !NothingToLearn().IsMatch(section.Title) && !NothingToLearn().IsMatch(section.Chapter);

    [GeneratedRegex(@"^\W*(?:(?:table of )?contents|index|references|bibliography|works cited|further reading|acknowledg(?:e)?ments?|dedication|copyright|about the authors?|glossary|preface|foreword|about this book|how to use this book|key (?:terms|words|concepts)|chapter (?:outline|summary|review)|(?:review|critical thinking|visual connection|end[- ]of[- ]chapter|self[- ]check) questions|answer key|answers to (?:the )?\w+(?: \w+)?|المحتويات|فهرس المحتويات|الفهرس|فهرس|المراجع|المصادر|المصادر والمراجع|قائمة المراجع|شكر وتقدير|الإهداء|حقوق النشر|عن المؤلف|مسرد المصطلحات|تمهيد|تصدير|المصطلحات الرئيسية|المفاهيم الرئيسية|ملخص الفصل|أسئلة المراجعة|أسئلة الفصل|مفتاح الإجابات|İçindekiler|Kaynakça|Kaynaklar|Dizin|Teşekkür|Sözlük|Önsöz|Anahtar (?:Terimler|Kavramlar)|Bölüm Özeti|(?:Değerlendirme|Tekrar) Soruları|Cevap Anahtarı|Table des matières|Sommaire|Bibliographie|Références|Remerciements|Glossaire|Préface|Avant-propos|Termes clés|Résumé du chapitre|Questions de révision|Corrigés?|Índice|Bibliografía|Referencias|Agradecimientos|Glosario|Prefacio|Prólogo|Términos clave|Resumen del capítulo|Preguntas de repaso|Solucionario|Daftar Isi|Daftar Pustaka|Indeks|Ucapan Terima Kasih|Glosarium|Kata Pengantar|Prakata|Istilah Kunci|Ringkasan Bab|Soal Latihan|Kunci Jawaban|فهرست|فهرست مطالب|منابع|کتابنامه|سپاسگزاری|واژه‌نامه|پیشگفتار|پیش‌گفتار|واژگان کلیدی|خلاصه فصل|پاسخنامه|فہرست|فہرست مضامین|حوالہ جات|کتابیات|دیباچہ|پیش لفظ|کلیدی اصطلاحات|باب کا خلاصہ|جوابات)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex NothingToLearn();

    /// <summary>
    /// Sections with the short ones joined to a neighbour in the same chapter: a heading alone (a part's title page)
    /// to the section after it, a few lines to the one before. Sections with no words go.
    /// </summary>
    internal static List<DocumentSection> Joined(IReadOnlyList<DocumentSection> sections)
    {
        var list = sections.Where(s => s.Places.Count > 0).Select(s => (s.Chapter, s.Title, Places: s.Places.ToList())).ToList();
        static int Length(List<PlacedText> places) => places.Sum(p => p.Text.Length);
        for (int i = 0; i < list.Count && list.Count > 1;)
        {
            var (chapter, title, places) = list[i];
            int length = Length(places);
            if (length >= ShortestPart)
            {
                i++;
                continue;
            }
            bool before = i > 0 && list[i - 1].Chapter == chapter;
            bool after = i + 1 < list.Count && list[i + 1].Chapter == chapter;
            // A heading with hardly anything under it opens what follows; a short ending closes what came before.
            if (after && (length <= 160 || !before))
            {
                var next = list[i + 1];
                list[i + 1] = (next.Chapter, next.Title.Length > 0 ? next.Title : title, [.. places, .. next.Places]);
                list.RemoveAt(i);
            }
            else if (before)
            {
                list[i - 1].Places.AddRange(places);
                list.RemoveAt(i);
            }
            else
                i++;
        }
        return list.Select(s => new DocumentSection(s.Chapter, s.Title, s.Places)).ToList();
    }

    /// <summary>Places in passages of at most <paramref name="most"/> characters, cut between places where it can be and at a sentence where it can't.</summary>
    internal static List<StudyUnit> Units(IReadOnlyList<PlacedText> places, int most)
    {
        var units = new List<StudyUnit>();
        var text = new StringBuilder();
        var held = new List<PlacedText>();
        void Close()
        {
            if (held.Count > 0 && text.ToString().Trim() is { Length: > 0 } words)
                units.Add(new StudyUnit(words, held.ToList()));
            text.Clear();
            held.Clear();
        }
        foreach (var place in places)
        {
            var words = place.Text.Trim();
            if (words.Length == 0)
                continue;
            foreach (var piece in GemmaTranslation.Parts(words, most))
            {
                if (text.Length > 0 && text.Length + piece.Length + 1 > most)
                    Close();
                held.Add(piece.Length == words.Length ? place : place with { Text = piece });
                text.Append(piece).Append('\n');
            }
        }
        Close();
        // A last passage of a few lines is joined to the one before rather than explained alone.
        if (units.Count >= 2 && units[^1].Text.Length < ShortestPart && units[^2].Text.Length + units[^1].Text.Length <= most + ShortestPart)
        {
            var last = units[^1];
            units.RemoveAt(units.Count - 1);
            units[^1] = new StudyUnit(units[^1].Text + "\n" + last.Text, [.. units[^1].Places, .. last.Places]);
        }
        return units;
    }

    // ───────────── what the model is asked ─────────────

    private static (int Cards, int Questions) PerPassage(StudyAmount amount) => amount switch
    {
        StudyAmount.Fewer => (2, 1),
        StudyAmount.More => (6, 3),
        _ => (4, 2),
    };

    /// <summary>
    /// What the person asked for, as the model is told it with every passage: it shapes the level, the tone, what is
    /// stressed and how much, never the form of the reply, its language or where the facts come from. "" for no request.
    /// </summary>
    internal static string Requested(string request, string into)
    {
        var asked = Regex.Replace(request ?? "", @"\s+", " ").Trim();
        if (asked.Length == 0)
            return "";
        if (asked.Length > StudyRequest.Longest)
            asked = asked[..StudyRequest.Longest];
        asked = asked.Replace("<|", "< |").Replace("|>", "| >");
        return $"The person who will use this study file asked for this, in their own words: «{asked}»\n"
            + $"Follow the request wherever it fits the passage and these rules: it may set the reader's level, the tone, what to stress, how long each section is and how many items to give. It doesn't change the form of the reply, nor its language (write in {Translator.EnglishName(into)}), and the facts still come only from the passage.\n";
    }

    internal static string Instruction(string into, string from, StudyAmount amount, string request = "")
    {
        var asked = Requested(request, into);
        var instruction = Instruction(into, from, amount);
        return asked.Length == 0 ? instruction : instruction + "\n\n" + asked.TrimEnd();
    }

    internal static string Instruction(string into, string from, StudyAmount amount)
    {
        var (cards, questions) = PerPassage(amount);
        var language = Translator.EnglishName(into);
        var translating = from != into && Translator.Languages.Any(l => l.Code == from)
            ? $", though the passage is in {Translator.EnglishName(from)}: the first time a technical term comes, put the passage's own word for it in brackets after yours"
            : "";
        return "You are a patient, experienced teacher. The user gives you a passage from a book, a course or a lecture; turn it into study material for a student meeting the subject for the first time.\n"
            + $"Write everything in {language}{translating}.\n"
            + "Keep to the passage: every fact, name, number, rule and date you write must come from it, and you add none of your own. Only an example may be made up, to show an idea at work, and it must agree with the passage.\n"
            + "Write about the subject itself, as a teacher explains it: never about the passage, the book, its sections or its learning objectives (no \"this passage discusses\", no \"the section introduces\").\n"
            + "Write formulas in plain text (x² + 3x = 10, a/b), with no LaTeX and no tables.\n"
            + "If the passage has nothing to learn from (a table of contents, an index, a list of references, a title or copyright page, thanks), reply with the one word NOTHING.\n\n"
            + "Reply in exactly this form, with the headings in English as they are here:\n\n"
            + "TITLE: what the passage is about, in a few words\n"
            + "IDEAS:\n- a key idea, in one sentence\n"
            + "EXPLANATION:\nThe passage explained more fully and more simply than it is written, in short paragraphs parted by blank lines: plain words, every new term made clear, and the reasons behind the facts where the passage gives them.\n"
            + "EXAMPLE:\nA worked example, step by step on numbered lines, that shows the main idea or method at work; or NONE when no example fits.\n"
            + "TERMS:\n- term = what it is, in one sentence: a definition, not what the passage says about it\n"
            + "CARDS:\n- a question worth remembering = its short answer\n"
            + "QUIZ:\nQ: a question that checks understanding\nA) a choice\nB) a choice\nC) a choice\nD) a choice\nRIGHT: the letter of the right choice\nWHY: one sentence saying why it is right, from the passage\n\n"
            + $"Give 3 to 5 IDEAS, up to 5 TERMS (the technical terms a student must learn, only ones the passage itself uses), up to {cards} CARDS and up to {questions} QUIZ question{(questions == 1 ? "" : "s")}. "
            + "In each question exactly one choice is right, and no other choice may also be true; the other three are believable but wrong. Ask about what matters, never about the passage's wording.";
    }

    /// <summary>What the model is given with a passage: where in the file it is, so a passage in the middle of a chapter isn't read as if it began one.</summary>
    internal static string Given(StudyPartPlan part, int index)
    {
        var where = part.Chapter.Length > 0 && part.Title.Length > 0 && part.Chapter != part.Title ? $"From the chapter \"{part.Chapter}\", the part \"{part.Title}\""
            : part.Title.Length > 0 ? $"From the part \"{part.Title}\""
            : part.Chapter.Length > 0 ? $"From the chapter \"{part.Chapter}\""
            : "";
        if (where.Length > 0 && part.Units.Count > 1)
            where += $" (passage {index + 1} of {part.Units.Count})";
        return (where.Length > 0 ? where + ":\n\n" : "") + part.Units[index].Text;
    }

    /// <summary>What a passage came to, read from the model's reply; <see cref="Nothing"/> when it said there is nothing to learn.</summary>
    internal sealed record Reply(string Title, List<string> Ideas, string Explanation, string Example, List<(string Term, string Meaning)> Terms,
        List<(string Front, string Back)> Cards, List<(string Question, List<string> Options, int Correct, string Why)> Quiz, bool Nothing);

    private static readonly string[] Headings = ["TITLE", "IDEAS", "KEY IDEAS", "EXPLANATION", "EXAMPLE", "WORKED EXAMPLE", "TERMS", "CARDS", "QUIZ"];

    [GeneratedRegex(@"^[\s#*_>]*(TITLE|KEY IDEAS|IDEAS|EXPLANATION|WORKED EXAMPLES?|EXAMPLES?|TERMS|CARDS|QUIZ)\b[\s*_]*:?[\s*_]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*(?:[-•*·▪–]|\d+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>The model's reply read by its headings; null when it isn't in the form it was asked for.</summary>
    internal static Reply? Read(string reply)
    {
        var text = reply.Replace("\r\n", "\n").Trim();
        if (Regex.IsMatch(text, @"^\W*NOTHING\W*$", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"^TITLE:\s*NOTHING\W*$", RegexOptions.IgnoreCase))
            return new Reply("", [], "", "", [], [], [], true);
        var parts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var match = HeadingLine().Match(raw);
            // A heading is one in capitals, or a word alone on its line: "Example: …" inside an explanation isn't one.
            if (match.Success && (match.Groups[1].Value == match.Groups[1].Value.ToUpperInvariant() || match.Groups[2].Value.Trim().Length == 0))
            {
                current = match.Groups[1].Value.ToUpperInvariant() switch
                {
                    "KEY IDEAS" => "IDEAS",
                    "WORKED EXAMPLE" or "WORKED EXAMPLES" or "EXAMPLES" => "EXAMPLE",
                    var name => name,
                };
                if (!parts.ContainsKey(current))
                    parts[current] = new StringBuilder();
                if (match.Groups[2].Value.Trim() is { Length: > 0 } rest)
                    parts[current].Append(rest).Append('\n');
                continue;
            }
            if (current is not null)
                parts[current].Append(raw.TrimEnd()).Append('\n');
        }
        string Part(string name) => parts.TryGetValue(name, out var sb) ? sb.ToString().Trim() : "";
        if (parts.Count == 0)
            return null;
        var title = Plainly(Part("TITLE").Split('\n')[0]);
        var explanation = Part("EXPLANATION");
        var ideas = Items(Part("IDEAS")).Select(Plainly).Where(i => i.Length > 3).Take(8).ToList();
        if (explanation.Length == 0 && ideas.Count == 0)
            return Regex.IsMatch(title, @"^\W*NOTHING\W*$", RegexOptions.IgnoreCase) ? new Reply("", [], "", "", [], [], [], true) : null;
        var example = Part("EXAMPLE");
        if (NoExample().IsMatch(example) || example.Count(char.IsLetterOrDigit) < 25)
            example = "";
        var terms = Items(Part("TERMS"), pairs: true).Select(Pair).Where(p => p.Left.Length is > 0 and <= 80 && p.Right.Length > 2).Select(p => (p.Left, p.Right)).ToList();
        var cards = Items(Part("CARDS"), pairs: true).Select(Pair).Where(p => p.Left.Length > 2 && p.Right.Length > 0).Select(p => (p.Left, p.Right)).ToList();
        return new Reply(title, ideas, explanation, example.Trim(), terms, cards, Quiz(Part("QUIZ")), false);
    }

    [GeneratedRegex(@"^\W*(?:none|no example|n/a|-+|لا يوجد|لا ينطبق|لا شيء|yok|aucun|ninguno|tidak ada|هیچ|ندارد|کوئی نہیں|نہیں)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoExample();

    /// <summary>
    /// Lines of a list, each without its bullet. In a list with bullets a line without one carries on the item before
    /// it (unless it is a pair of its own, "term = meaning"); in a list without any, each line is an item.
    /// </summary>
    private static List<string> Items(string block, bool pairs = false)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        bool bulleted = lines.Any(l => Bullet().IsMatch(l));
        var items = new List<string>();
        foreach (var line in lines)
        {
            if (!bulleted || Bullet().IsMatch(line) || items.Count == 0 || pairs && line.Contains('='))
                items.Add(Bullet().Replace(line, ""));
            else
                items[^1] += " " + line;
        }
        return items.Where(i => i.Length > 0).ToList();
    }

    /// <summary>
    /// A "term = meaning" line in two, at its first equals sign, else at a dash or a colon; a card written as
    /// "question? answer" at its question mark, which stays with the question.
    /// </summary>
    internal static (string Left, string Right) Pair(string item)
    {
        int at = item.IndexOf('=');
        int length = 1;
        if (at < 0)
        {
            var dash = Regex.Match(item, @"\s[—–-]\s");
            var asked = Regex.Match(item, @"[?؟](?=\s+\S)");
            if (dash.Success)
                (at, length) = (dash.Index, dash.Length);
            else if (asked.Success)
                return (Plainly(item[..(asked.Index + 1)]), Plainly(item[(asked.Index + 1)..]));
            else if (item.IndexOf(':') is var colon and > 0 and < 90)
                at = colon;
        }
        if (at <= 0)
            return ("", "");
        return (Plainly(item[..at]), Plainly(item[(at + length)..]));
    }

    /// <summary>Text without the marks a model decorates it with: bold, quotes around the whole, a full stop alone.</summary>
    internal static string Plainly(string text)
    {
        var t = text.Replace("**", "").Replace("__", "").Trim();
        if (t.Length > 2 && (t[0] == '"' && t[^1] == '"' || t[0] == '“' && t[^1] == '”' || t[0] == '«' && t[^1] == '»'))
            t = t[1..^1].Trim();
        return t.Trim(' ', '\t', '*', '_', '`');
    }

    [GeneratedRegex(@"^\s*(?:\*\*)?(?:Q(?:uestion)?\s*\d*|\d+)\s*[:.)]\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionLine();

    /// <summary>A choice: "A) …", "(b) …", "أ) …", or as Persian and Urdu letter them, "الف) …".</summary>
    [GeneratedRegex(@"^\s*(?:\*\*)?\(?(الف|[A-Da-dأابجد])\s*(?:\*\*)?\s*[).:\-–]\s*(.+)$")]
    private static partial Regex ChoiceLine();

    [GeneratedRegex(@"^\s*(?:\*\*)?(?:RIGHT|ANSWER|CORRECT(?: ANSWER)?)\s*(?:\*\*)?\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RightLine();

    [GeneratedRegex(@"^\s*(?:\*\*)?(?:WHY|REASON|EXPLANATION)\s*(?:\*\*)?\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WhyLine();

    /// <summary>The questions of a reply: each with four different choices and one right one, or it is left out.</summary>
    private static List<(string Question, List<string> Options, int Correct, string Why)> Quiz(string block)
    {
        var quiz = new List<(string, List<string>, int, string)>();
        string question = "", right = "", why = "";
        var options = new List<string>();
        void Close()
        {
            if (question.Length > 0 && Chosen(options, right) is int correct and >= 0
                && options.Count == 4 && options.All(o => o.Length > 0) && options.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4)
                quiz.Add((Plainly(question), options.Select(Plainly).ToList(), correct, Plainly(why)));
            question = right = why = "";
            options.Clear();
        }
        string last = "";
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (RightLine().Match(line) is { Success: true } r)
                (right, last) = (r.Groups[1].Value.Trim(), "right");
            else if (WhyLine().Match(line) is { Success: true } w)
                (why, last) = (w.Groups[1].Value.Trim(), "why");
            else if (question.Length > 0 && options.Count < 4 && ChoiceLine().Match(line) is { Success: true } c)
                (options, last) = ([.. options, c.Groups[2].Value.Trim()], "option");
            else if (QuestionLine().Match(line) is { Success: true } q && (question.Length == 0 || options.Count > 0))
            {
                Close();
                (question, last) = (q.Groups[1].Value.Trim(), "question");
            }
            else if (last == "why")
                why += " " + line;
            else if (last == "question")
                question += " " + line;
            else if (question.Length == 0)
                (question, last) = (line, "question");
        }
        Close();
        return quiz;
    }

    /// <summary>Which choice the reply says is right: by its letter (A–D, أ ب ج د, or الف for the first), or by its words.</summary>
    private static int Chosen(List<string> options, string right)
    {
        var said = Plainly(right).Trim('(', ')', '.', ' ');
        if (said.Length == 0)
            return -1;
        if (said == "الف" || said.StartsWith("الف)", StringComparison.Ordinal) || said.StartsWith("الف ", StringComparison.Ordinal))
            return 0;
        int letter = said[0] switch
        {
            'A' or 'a' or 'أ' or 'ا' => 0,
            'B' or 'b' or 'ب' => 1,
            'C' or 'c' or 'ج' => 2,
            'D' or 'd' or 'د' => 3,
            _ => -1,
        };
        // "B", "B)", "B. Leaves": a letter standing alone, not the first letter of a word.
        if (letter >= 0 && (said.Length == 1 || !char.IsLetter(said[1])))
            return letter;
        return options.FindIndex(o => string.Equals(Plainly(o), said, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether what was written is in the language asked for: in its script (Arabic letters for Arabic, Persian and Urdu,
    /// a few words in brackets aside), and, where the letters tell, that language and not another of the eight.
    /// </summary>
    internal static bool InLanguage(string text, string into)
    {
        int letters = 0, arabic = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (c is >= (char)0x0600 and <= (char)0x06FF or >= (char)0x0750 and <= (char)0x077F or >= (char)0xFB50 and <= (char)0xFDFF or >= (char)0xFE70 and <= (char)0xFEFF)
                arabic++;
        }
        if (letters < 20)
            return true;
        bool rightToLeft = into is "ar" or "fa" or "ur";
        if (rightToLeft ? arabic * 2 < letters : arabic * 5 > letters)
            return false;
        // Words in brackets are the original's terms, in its language.
        var guess = LanguageGuess.Of(Regex.Replace(text, @"\([^)]*\)", " "));
        return guess is null || guess == into || !Translator.Languages.Any(l => l.Code == guess);
    }

    // ───────────── making ─────────────

    /// <summary>The settings a study file is made with: the language it is written in, how many cards and questions, and the file's own language.</summary>
    /// <param name="Request">What the person asked the study file to be, in their own words ("" for nothing particular).</param>
    /// <param name="Activities">How many diagrams and activities each passage gives besides its explanation.</param>
    public sealed record Options(string Into, StudyAmount Amount, string From, string Request = "", StudyActivities Activities = StudyActivities.Some);

    private static string ExtrasInstruction(Options options) => StudyExtrasReader.Instruction(options.Into, options.From, options.Activities, options.Request);

    /// <summary>How many of the chosen parts' passages were explained before with these settings and this writer: those take no time.</summary>
    public static int Made(StudyPlan plan, IReadOnlySet<int> chosen, Options options)
    {
        var instruction = Instruction(options.Into, options.From, options.Amount, options.Request);
        var extras = options.Activities == StudyActivities.None ? null : ExtrasInstruction(options);
        var writer = WriterId;
        return plan.Parts.Where(p => chosen.Contains(p.Number)).Sum(p => Enumerable.Range(0, p.Units.Count).Count(i =>
            File.Exists(CachePath(instruction, Given(p, i), writer)) && (extras is null || File.Exists(CachePath(extras, Given(p, i), writer)))));
    }

    public static int Passages(StudyPlan plan, IReadOnlySet<int> chosen) => plan.Parts.Where(p => chosen.Contains(p.Number)).Sum(p => p.Units.Count);

    /// <summary>
    /// The part a sample is made of: the first chosen part that isn't only a chapter's opening words, unless there is no
    /// other; null when nothing is chosen.
    /// </summary>
    public static int? SamplePart(StudyPlan plan, IReadOnlySet<int> chosen)
    {
        var parts = plan.Parts.Where(p => chosen.Contains(p.Number)).ToList();
        return (parts.FirstOrDefault(p => !(p.Chapter.Length > 0 && p.Title == p.Chapter)) ?? parts.FirstOrDefault())?.Number;
    }

    /// <summary>Where a sample is kept: apart from the file, so trying doesn't leave pages beside it.</summary>
    public static string SamplePath(string source) =>
        Path.Combine(CacheFolder, "samples", Path.GetFileNameWithoutExtension(source) + ".html");

    /// <summary>
    /// Makes the study file's content from the chosen parts. <paramref name="placeName"/> says a place in the study
    /// file's language ("Page 3", "Pages 4–6"). A <paramref name="sample"/> is one part, to see how it will look: it has
    /// no overview of the whole, and what it made is kept for the whole file, so nothing is made twice.
    /// </summary>
    public static async Task<StudyBook> MakeAsync(StudyPlan plan, IReadOnlySet<int> chosen, Options options, string title, string source,
        Func<PlacedText, PlacedText, string> placeName, IProgress<StudyProgress>? progress = null, CancellationToken cancellationToken = default, bool sample = false)
    {
        var parts = plan.Parts.Where(p => chosen.Contains(p.Number)).ToList();
        if (parts.Count == 0)
            throw new InvalidOperationException("Choose at least one part of the file.");
        var instruction = Instruction(options.Into, options.From, options.Amount, options.Request);
        var extrasInstruction = options.Activities == StudyActivities.None ? null : ExtrasInstruction(options);
        bool sameLanguage = options.Into == options.From;
        int total = parts.Sum(p => p.Units.Count), done = 0, timed = 0;
        var clock = new Stopwatch();
        double spent = 0;
        var made = new List<StudyPart>();
        var seenCards = new HashSet<string>(StringComparer.Ordinal);
        var seenQuestions = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<StudyTerm>();
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in parts)
            {
                var passages = new List<StudyExplained>();
                var cards = new List<StudyCard>();
                var quiz = new List<StudyQuestion>();
                var visuals = new List<StudyVisual>();
                var activities = new List<StudyActivity>();
                for (int i = 0; i < part.Units.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var unit = part.Units[i];
                    var where = placeName(unit.First, unit.Last);
                    progress?.Report(new StudyProgress(done, total, part.Title, timed > 0 ? TimeSpan.FromSeconds(spent / timed * (total - done)) : null));
                    var given = Given(part, i);
                    var cached = CachePath(instruction, given);
                    var extrasCached = extrasInstruction is null ? null : CachePath(extrasInstruction, given);
                    bool fresh = !File.Exists(cached) || extrasCached is not null && !File.Exists(extrasCached);
                    clock.Restart();
                    var reply = await PassageAsync(instruction, given, unit, options.Into, cached, cancellationToken).ConfigureAwait(false);
                    // The diagrams and activities, asked apart: with the explanation they wouldn't fit what the model holds.
                    var extras = reply is { Nothing: false } && extrasInstruction is not null
                        ? await ExtrasAsync(extrasInstruction, given, options.Into, where, extrasCached!, cancellationToken).ConfigureAwait(false)
                        : StudyExtras.Empty;
                    // A passage with nothing to learn has no activities either: kept as made.
                    if (reply is { Nothing: true } && extrasCached is not null && !File.Exists(extrasCached))
                        Keep(extrasCached, "NONE");
                    if (fresh)
                    {
                        spent += clock.Elapsed.TotalSeconds;
                        timed++;
                    }
                    done++;
                    if (reply is null)
                    {
                        // Couldn't be explained: the file's own words, so nothing is missing.
                        passages.Add(new StudyExplained("", where, [], "", "", unit.Text));
                        continue;
                    }
                    if (reply.Nothing)
                        continue;
                    passages.Add(new StudyExplained(reply.Title, where, reply.Ideas, reply.Explanation, reply.Example));
                    visuals.AddRange(extras.Visuals);
                    activities.AddRange(extras.Activities);
                    var known = Fold(unit.Text + "\n" + reply.Explanation + "\n" + reply.Example);
                    foreach (var (term, meaning) in reply.Terms)
                    {
                        // A term the passage (or its explanation) doesn't use isn't one of the file's.
                        var folded = Fold(Bare(term));
                        var original = Fold(Bracketed(term));
                        if (folded.Trim().Length == 0 || !(known.Contains(folded, StringComparison.Ordinal) || original.Length > 0 && known.Contains(original, StringComparison.Ordinal)))
                            continue;
                        // The same term again, in the plural or by its abbreviation, is the one already there.
                        var keys = new[] { TermKey(Bare(term)), TermKey(Bracketed(term)) }.Where(k => k.Length > 0).ToList();
                        if (keys.Count == 0 || keys.Any(seenTerms.Contains))
                            continue;
                        seenTerms.UnionWith(keys);
                        terms.Add(new StudyTerm(term, meaning, Locate(unit, term + " " + meaning, sameLanguage, placeName).Where, part.Number));
                    }
                    foreach (var (front, back) in reply.Cards)
                    {
                        if (!seenCards.Add(Key(front)))
                            continue;
                        var (at, quote) = Locate(unit, front + " " + back, sameLanguage, placeName, back);
                        cards.Add(new StudyCard(front, back, at) { Quote = quote });
                    }
                    foreach (var (question, choices, correct, why) in reply.Quiz)
                    {
                        if (!seenQuestions.Add(Key(question)))
                            continue;
                        var (at, quote) = Locate(unit, question + " " + choices[correct] + " " + why, sameLanguage, placeName, choices[correct] + " " + why);
                        // A model puts the right choice in the same place again and again: each question's own order, the same every time.
                        var (shuffled, right) = Shuffled(question, choices, correct);
                        quiz.Add(new StudyQuestion(question, shuffled, right, why, at) { Quote = quote });
                    }
                }
                if (passages.Count == 0)
                    continue;
                var heading = part.Title.Length > 0 ? part.Title : passages.FirstOrDefault(p => p.Title.Length > 0)?.Title ?? "";
                made.Add(new StudyPart(part.Number, part.Chapter, heading, placeName(part.First, part.Last), passages, cards, quiz) { Visuals = visuals, Activities = activities });
            }
            if (made.Count == 0)
                throw new InvalidOperationException("The model didn't find anything to make notes from in this file.");
            progress?.Report(new StudyProgress(done, total, "", null));

            // Chapters, each summed up when it has more than one part.
            var chapters = new List<StudyChapter>();
            foreach (var run in Runs(made))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = "";
                if (run.Count >= 2)
                {
                    // A long chapter's parts each bring fewer ideas, so all of them are in what the model reads.
                    int per = Math.Clamp(48 / run.Count, 2, 8);
                    var outline = string.Join("\n", run.Select(p => "## " + p.Title + "\n" + string.Join("\n", p.Passages.SelectMany(x => x.Ideas).Take(per).Select(i => "- " + i))));
                    summary = await SummaryAsync(ChapterInstruction(options.Into, plan.Recording), Clip(outline), cancellationToken).ConfigureAwait(false);
                }
                chapters.Add(new StudyChapter(run[0].Chapter, summary, run.Select(p => p.Number).ToList()));
            }
            // The whole: what it is about and what a student will learn, from as much of it as the model can read at once.
            // A sample is one part: no overview of a whole it hasn't read.
            var overview = sample ? "" : await SummaryAsync(OverviewInstruction(options.Into, plan.Recording), Clip(Outline(made, chapters)), cancellationToken).ConfigureAwait(false);
            var (about, goals) = sample ? ("", (IReadOnlyList<string>)[]) : Overview(overview);
            return new StudyBook(title, source, options.Into, options.From, about, goals, chapters, made, terms, WriterName, DateTime.Now, plan.Recording) { Sample = sample };
        }
        finally
        {
            GemmaTranslation.Finished();
        }
    }

    /// <summary>Runs of parts in the same chapter, in order (a part that stands on its own is a run of one, with no chapter).</summary>
    internal static List<List<StudyPart>> Runs(IReadOnlyList<StudyPart> parts)
    {
        var runs = new List<List<StudyPart>>();
        foreach (var part in parts)
        {
            if (part.Chapter.Length > 0 && runs.Count > 0 && runs[^1][0].Chapter == part.Chapter)
                runs[^1].Add(part);
            else
                runs.Add([part]);
        }
        return runs.Where(r => r[0].Chapter.Length > 0).ToList();
    }

    private const int Readable = 7000;

    private static string Clip(string text) => text.Length > Readable ? text[..Readable] : text;

    /// <summary>
    /// The whole file in outline, for its overview: each chapter by its summary and each part on its own by its ideas;
    /// briefer (a summary's first sentence, a part's first idea, then the titles alone) until all of it fits.
    /// </summary>
    internal static string Outline(IReadOnlyList<StudyPart> parts, IReadOnlyList<StudyChapter> chapters)
    {
        string Write(int detail)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                var chapter = part.Chapter.Length > 0 ? chapters.FirstOrDefault(c => c.Parts.Contains(part.Number)) : null;
                if (chapter is not null && chapter.Parts[0] == part.Number)
                {
                    sb.Append("# ").AppendLine(chapter.Title);
                    if (chapter.Summary.Length > 0 && detail > 0)
                        sb.AppendLine(detail > 1 ? chapter.Summary : FirstSentence(chapter.Summary));
                }
                if (chapter is null || chapter.Summary.Length == 0)
                {
                    sb.Append("## ").AppendLine(part.Title);
                    foreach (var idea in part.Passages.SelectMany(p => p.Ideas).Take(detail > 1 ? 4 : detail))
                        sb.Append("- ").AppendLine(idea);
                }
            }
            return sb.ToString();
        }
        var text = Write(2);
        if (text.Length > Readable)
            text = Write(1);
        if (text.Length > Readable)
            text = Write(0);
        return text;
    }

    private static string FirstSentence(string text) =>
        Regex.Match(text, @"^.*?[.!?؟۔](?=\s|$)", RegexOptions.Singleline) is { Success: true } m ? m.Value : text;

    private static string ChapterInstruction(string into, bool lecture) =>
        $"Below are the titles and key ideas of the parts of one {(lecture ? "stretch of a lecture" : "chapter")}, in order. Write a short summary of it in {Translator.EnglishName(into)}: three to five sentences that say what it teaches and how its ideas connect. Keep to these ideas and add nothing of your own. Reply with the summary only.";

    private static string OverviewInstruction(string into, bool lecture) =>
        $"Below is an outline of a {(lecture ? "recorded lecture" : "book or course document")}: its chapters or parts with their key ideas, in order. For a student about to study it, write in {Translator.EnglishName(into)}, in exactly this form, with the headings in English:\n"
        + "OVERVIEW:\nA short paragraph: what it is about and how it is built.\nGOALS:\n- something the student will understand or be able to do after studying it\n\n"
        + "Give 3 to 6 goals. Keep to the outline and add nothing of your own.";

    /// <summary>The overview's paragraph and goals.</summary>
    internal static (string About, List<string> Goals) Overview(string reply)
    {
        var text = reply.Replace("\r\n", "\n");
        var goalsAt = Regex.Match(text, @"^[\s#*_]*GOALS[\s*_]*:?[\s*_]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var about = goalsAt.Success ? text[..goalsAt.Index] : text;
        about = Regex.Replace(about, @"^[\s#*_]*OVERVIEW[\s*_]*:?[\s*_]*", "", RegexOptions.IgnoreCase).Trim();
        var goals = goalsAt.Success ? Items(text[(goalsAt.Index + goalsAt.Length)..]).Select(Plainly).Where(g => g.Length > 3).Take(8).ToList() : [];
        return (about, goals);
    }

    /// <summary>What the model wrote for a passage, from what was kept or asked anew: once more when it isn't in the form or the language asked for. Null when it never was.</summary>
    private static async Task<Reply?> PassageAsync(string instruction, string given, StudyUnit unit, string into, string cached, CancellationToken cancellationToken)
    {
        if (File.Exists(cached) && Read(await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false)) is { } kept)
            return kept;
        Reply? best = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reply = await WriteAsync(instruction, given, CloudAi.CanWrite ? 6000 : 2000, attempt == 0 ? 0.2 : 0.5, cancellationToken).ConfigureAwait(false);
            var read = Read(reply);
            if (read is { Nothing: true } || read is not null && read.Explanation.Length >= 80 && InLanguage(read.Explanation, into))
            {
                Keep(cached, reply);
                return read;
            }
            if (read is not null && read.Explanation.Length > (best?.Explanation.Length ?? -1) && InLanguage(read.Explanation + " " + string.Join(' ', read.Ideas), into))
                best = read;
        }
        return best;
    }

    /// <summary>
    /// A passage's diagrams and activities, from what was kept or asked anew: once more when the reply has none in the form
    /// asked for (and doesn't say there are none) or isn't in the language asked for. What came the second time is kept
    /// whatever it is, so making the file again doesn't ask a third time.
    /// </summary>
    private static async Task<StudyExtras> ExtrasAsync(string instruction, string given, string into, string where, string cached, CancellationToken cancellationToken)
    {
        if (File.Exists(cached))
            return StudyExtrasReader.Read(await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false), where);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reply = await WriteAsync(instruction, given, CloudAi.CanWrite ? 3000 : 1300, attempt == 0 ? 0.2 : 0.5, cancellationToken).ConfigureAwait(false);
            var read = StudyExtrasReader.Read(reply, where);
            bool none = read.IsEmpty && Regex.IsMatch(reply.Trim(), @"^\W*NONE\W*$", RegexOptions.IgnoreCase);
            bool inLanguage = read.IsEmpty || InLanguage(Said(read), into);
            if (none || !read.IsEmpty && inLanguage || attempt == 1)
            {
                Keep(cached, inLanguage ? reply : "NONE");
                return inLanguage ? read : StudyExtras.Empty;
            }
        }
        return StudyExtras.Empty;
    }

    /// <summary>The words of a passage's diagrams and activities, to tell their language by.</summary>
    internal static string Said(StudyExtras extras) => string.Join(" ",
        extras.Visuals.SelectMany(v => v.Items.SelectMany(i => new[] { i.Text, i.Answer }.Concat(i.Details)).Prepend(v.Title))
            .Concat(extras.Activities.SelectMany(a => a.Items.SelectMany(i => new[] { i.Text, i.Note, a.Kind == StudyActivityKind.Match ? i.Answer : "" }).Prepend(a.Prompt))));

    private static async Task<string> SummaryAsync(string instruction, string text, CancellationToken cancellationToken)
    {
        var cached = CachePath(instruction, text);
        if (File.Exists(cached))
            return await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false);
        var reply = (await WriteAsync(instruction, text, CloudAi.CanWrite ? 3000 : 700, 0.2, cancellationToken).ConfigureAwait(false)).Trim();
        if (reply.Length > 0)
            Keep(cached, reply);
        return reply;
    }

    private static async Task<string> WriteAsync(string instruction, string text, int most, double temperature, CancellationToken cancellationToken)
    {
        if (TestWriter is { } test)
            return await test(instruction, text, "").ConfigureAwait(false);
        if (CloudAi.CanWrite)
            return await AskPetal.WriteAsync(instruction, text, null, most, cancellationToken, temperature).ConfigureAwait(false);
        var clean = text.Replace("<|", "< |").Replace("|>", "| >").Trim();
        var prompt = $"<|turn>system\n{instruction}<turn|>\n<|turn>user\n{clean}<turn|>\n<|turn>model\n";
        return GemmaTranslation.Reply(await GemmaServer.CompleteAsync(Models[1], prompt, most, temperature, 1.05, cancellationToken).ConfigureAwait(false));
    }

    // ───────────── where in the file ─────────────

    /// <summary>
    /// Where an item is: the place of its passage that says most of what it does, and the sentence there that says it,
    /// when that is clear (the study file in the file's own language, three words or more in common); otherwise the
    /// passage's places and no sentence.
    /// </summary>
    internal static (string Where, string Quote) Locate(StudyUnit unit, string said, bool sameLanguage, Func<PlacedText, PlacedText, string> placeName, string? answer = null)
    {
        var range = placeName(unit.First, unit.Last);
        if (!sameLanguage)
            return (range, "");
        var wanted = WordIndex.Terms(said).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return (range, "");
        PlacedText? best = null;
        int bestHits = 0;
        foreach (var place in unit.Places)
        {
            int hits = WordIndex.Terms(place.Text).Distinct().Count(wanted.Contains);
            if (hits > bestHits)
                (best, bestHits) = (place, hits);
        }
        if (best is null || bestHits < 3 || bestHits * 3 < wanted.Count)
            return (range, "");
        // The sentence there that says most of the answer itself (a question's own words are in many sentences).
        var answered = WordIndex.Terms(answer ?? said).ToHashSet(StringComparer.Ordinal);
        var quote = Regex.Split(best.Text, @"(?<=[.!?؟۔])\s+|\n+")
            .Select(s => s.Trim())
            .Where(s => s.Length is >= 20 and <= 320 && !s.Contains('|'))
            .Select(s => (Sentence: s, Hits: WordIndex.Terms(s).Distinct().Count(answered.Contains)))
            .OrderByDescending(s => s.Hits).ThenBy(s => s.Sentence.Length)
            .FirstOrDefault();
        return (placeName(best, best), quote.Sentence is { } sentence && quote.Hits >= Math.Min(3, Math.Max(2, answered.Count / 2)) ? sentence : "");
    }

    /// <summary>A question's choices in an order of its own, fixed by its words: the right one isn't always the same letter.</summary>
    internal static (List<string> Options, int Correct) Shuffled(string question, IReadOnlyList<string> options, int correct)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(question)));
        var order = Enumerable.Range(0, options.Count).ToArray();
        var random = new Random(seed);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return (order.Select(i => options[i]).ToList(), Array.IndexOf(order, correct));
    }

    /// <summary>A term as one: its words stemmed and the little ones left out, so "organelles" is "organelle".</summary>
    internal static string TermKey(string term) => string.Join(' ', WordIndex.Terms(term));

    // ───────────── kept ─────────────

    private static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Text as compared for terms: folded, with spaces between words.</summary>
    internal static string Fold(string text) => " " + Regex.Replace(AskPetal.Fold(text), @"[^\p{L}\p{N}]+", " ").Trim() + " ";

    /// <summary>A term without the original's word in brackets after it.</summary>
    internal static string Bare(string term) => Regex.Replace(term, @"\s*[(（][^)）]*[)）]", "").Trim();

    /// <summary>The original's word for a term, in brackets after it ("" when there is none).</summary>
    internal static string Bracketed(string term) => Regex.Match(term, @"[(（]([^)）]*)[)）]") is { Success: true } m ? m.Groups[1].Value.Trim() : "";

    /// <summary>Where what <paramref name="writer"/> (the one writing now, when not given) wrote for a passage is kept.</summary>
    internal static string CachePath(string instruction, string given, string? writer = null)
    {
        var identity = $"{Version}\n{writer ?? WriterId}\n{instruction}\n{given}";
        return Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant() + ".txt");
    }

    private static void Keep(string path, string reply)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(path, reply, new UTF8Encoding(false));
            // Now and then, the oldest let go: what the model wrote, and documents as they were laid out.
            if (Random.Shared.Next(50) == 0)
            {
                var folder = new DirectoryInfo(CacheFolder);
                var old = folder.GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Remembered)
                    .Concat(folder.GetFiles("*.pdf").OrderByDescending(f => f.LastWriteTimeUtc).Skip(30)).ToList();
                foreach (var file in old)
                    file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
