using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Petal.Recipes.Study;

/// <summary>
/// The words of a study file's own page, in the language it is written in (not the interface's: it is sent to
/// students), and its counted phrases ("12 terms") with that language's plural forms.
/// </summary>
public sealed class StudyFileWords(IReadOnlyDictionary<string, string> words, Func<string, int, string> count)
{
    /// <summary>The words the page uses, by key: every one must be given.</summary>
    public static readonly string[] Keys =
    [
        "home", "terms", "cards", "test", "progress", "contents", "search", "noResults", "smaller", "larger", "theme", "skip",
        "keyIdeas", "explained", "example", "termsHere", "check", "partCards", "markStudied", "studied", "previous", "next", "intro",
        "answer", "right", "wrong", "fromOriginal", "turn", "notYet", "knewIt", "shuffle", "startOver", "from", "wholeFile", "known",
        "cardsHint", "allKnown", "howMany", "all", "start", "questionOf", "score", "best", "mistakes", "tryAgain", "finish", "readPart",
        "partsStudied", "questionsRight", "cardsKnown", "reset", "resetSure", "kept", "print", "withAnswers", "madeFrom", "aiNote",
        "howTo", "step1", "step2", "step3", "begin", "goals", "chapterTest", "letters", "notExplained", "findTerm", "close", "redo",
        "noQuestions", "chapter", "needsScript", "resume",
        "seeIt", "tryIt", "activities", "kindOrder", "kindMatch", "kindSort", "kindBlanks", "kindTrueFalse", "checkIt", "showAnswer",
        "again", "allRight", "rightOf", "dragHint", "bank", "placeHere", "picked", "placed", "true", "false", "backToStart",
        "activitiesSolved", "practiceChapter", "activityOf", "solvedScore", "noActivities", "skipIt", "orderHint",
        "kindMapFill", "mapHint", "stepOf", "sample",
    ];

    /// <summary>The counted phrases the page uses: parts, terms, cards, questions and activities.</summary>
    public static readonly string[] Counted = ["parts", "terms", "cards", "questions", "activities"];

    public string this[string key] => words.TryGetValue(key, out var w) ? w : key;

    public string Count(string key, int n) => count(key, n);

    public IReadOnlyDictionary<string, string> All => words;
}

/// <summary>
/// The study file as one page: everything inline (its style, its script, its words), nothing loaded from anywhere, so it
/// opens the same in any browser on a computer or a phone, with no internet, and can be sent as it is. Without its
/// script it still reads from top to bottom, every part, card and answer shown; printed it is a handout with or without
/// the answers. The page's policy allows only its own style and the one script whose hash it carries.
/// </summary>
public static partial class StudyFileWriter
{
    public static void Write(string path, StudyBook book, StudyFileWords words)
    {
        var temporary = path + ".tmp";
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
            Directory.CreateDirectory(folder);
        File.WriteAllText(temporary, Html(book, words), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    public static string Html(StudyBook book, StudyFileWords words)
    {
        bool rtl = book.Language is "ar" or "fa" or "ur";
        _isolating = rtl;
        try
        {
            return Page(book, words, rtl);
        }
        finally
        {
            _isolating = false;
        }
    }

    /// <summary>Whether the page being written reads right to left, so runs of Latin letters in what the model wrote are isolated.</summary>
    [ThreadStatic]
    private static bool _isolating;

    private static string Page(StudyBook book, StudyFileWords words, bool rtl)
    {
        var linker = new TermLinker(book.Terms);
        var ids = new Dictionary<StudyTerm, string>(ReferenceEqualityComparer.Instance);
        for (int t = 0; t < book.Terms.Count; t++)
            ids[book.Terms[t]] = "t" + (t + 1);
        var numbers = new Dictionary<StudyChapter, int>(ReferenceEqualityComparer.Instance);
        for (int c = 0; c < book.Chapters.Count; c++)
            numbers[book.Chapters[c]] = c + 1;
        var chapterOf = new Dictionary<int, int>();
        for (int c = 0; c < book.Chapters.Count; c++)
            foreach (var n in book.Chapters[c].Parts)
                chapterOf[n] = c + 1;
        var letters = words["letters"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (letters.Length < 4)
            letters = ["A", "B", "C", "D"];
        string Letter(int i) => letters[Math.Clamp(i, 0, 3)];
        string partTitle(StudyPart p) => p.Title.Length > 0 ? p.Title : p.Where;
        string partLabel(StudyPart p) => Label(p, words);

        int cardCount = book.Parts.Sum(p => p.Cards.Count), questionCount = book.Parts.Sum(p => p.Quiz.Count);
        var activityIds = ActivityIds(book);
        int activityCount = activityIds.Count;
        var sb = new StringBuilder(256 * 1024);
        var script = Resource("StudyFile.js");
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        sb.Append("<!doctype html>\n<html lang=\"").Append(book.Language).Append("\" dir=\"").Append(rtl ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; script-src 'sha256-").Append(hash).Append("'; base-uri 'none'; form-action 'none'\">\n");
        sb.Append("<meta name=\"generator\" content=\"DropWheel\">\n<meta name=\"color-scheme\" content=\"light dark\">\n");
        sb.Append("<title>").Append(E(book.Title)).Append("</title>\n<style>\n").Append(Resource("StudyFile.css")).Append("\n</style>\n</head>\n");
        sb.Append("<body class=\"nojs\" data-id=\"").Append(Id(book)).Append("\">\n");
        sb.Append("<a class=\"skip\" href=\"#main\">").Append(E(words["skip"])).Append("</a>\n");

        // ── the bar along the top ──
        sb.Append("<header class=\"bar\">");
        sb.Append("<button type=\"button\" class=\"icon menu needs-js\" aria-controls=\"toc\" aria-expanded=\"false\" aria-label=\"").Append(A(words["contents"])).Append("\">").Append(Svg("menu")).Append("</button>");
        sb.Append("<a class=\"brand\" href=\"#home\"><bdi>").Append(E(book.Title)).Append("</bdi></a>");
        sb.Append("<div class=\"find needs-js\" role=\"search\"><span class=\"glass\">").Append(Svg("search")).Append("</span><input type=\"search\" id=\"q\" autocomplete=\"off\" placeholder=\"").Append(A(words["search"])).Append("\" aria-label=\"").Append(A(words["search"])).Append("\" aria-controls=\"hits\"><div class=\"hits\" id=\"hits\" role=\"listbox\" hidden></div></div>");
        sb.Append("<div class=\"prefs needs-js\"><button type=\"button\" class=\"icon size\" data-d=\"-1\" aria-label=\"").Append(A(words["smaller"])).Append("\"><span aria-hidden=\"true\">A−</span></button>");
        sb.Append("<button type=\"button\" class=\"icon size\" data-d=\"1\" aria-label=\"").Append(A(words["larger"])).Append("\"><span aria-hidden=\"true\">A+</span></button>");
        sb.Append("<button type=\"button\" class=\"icon theme\" aria-label=\"").Append(A(words["theme"])).Append("\">").Append(Svg("contrast")).Append("</button></div>");
        sb.Append("<a class=\"meter needs-js\" href=\"#progress\" aria-label=\"").Append(A(words["partsStudied"])).Append("\"><span class=\"ring\"><span></span></span><span class=\"num\"></span></a>");
        sb.Append("</header>\n<div class=\"shell\">\n");

        // ── contents ──
        sb.Append("<nav id=\"toc\" aria-label=\"").Append(A(words["contents"])).Append("\"><ul class=\"go\">");
        sb.Append("<li><a href=\"#home\">").Append(Svg("home")).Append(E(words["home"])).Append("</a></li>");
        if (book.Terms.Count > 0)
            sb.Append("<li><a href=\"#terms\">").Append(Svg("book")).Append(E(words["terms"])).Append("<span class=\"n\">").Append(book.Terms.Count).Append("</span></a></li>");
        if (cardCount > 0)
            sb.Append("<li class=\"needs-js\"><a href=\"#cards\">").Append(Svg("cards")).Append(E(words["cards"])).Append("<span class=\"n\">").Append(cardCount).Append("</span></a></li>");
        if (questionCount > 0)
            sb.Append("<li class=\"needs-js\"><a href=\"#test\">").Append(Svg("test")).Append(E(words["test"])).Append("</a></li>");
        if (activityCount > 0)
            sb.Append("<li class=\"needs-js\"><a href=\"#practice\">").Append(Svg("puzzle")).Append(E(words["activities"])).Append("<span class=\"n\">").Append(activityCount).Append("</span></a></li>");
        sb.Append("<li class=\"needs-js\"><a href=\"#progress\">").Append(Svg("chart")).Append(E(words["progress"])).Append("</a></li></ul><ol class=\"toc\">");
        foreach (var run in Groups(book))
        {
            if (run.Chapter is { } chapter)
            {
                int c = numbers[chapter];
                sb.Append("<li class=\"chap\"><a class=\"cl\" href=\"#c").Append(c).Append("\" data-c=\"").Append(c).Append("\"><bdi>").Append(E(chapter.Title)).Append("</bdi></a><ol>");
                foreach (var part in run.Parts)
                    sb.Append("<li><a href=\"#p").Append(part.Number).Append("\" data-p=\"").Append(part.Number).Append("\"><span class=\"tick\"></span><bdi>").Append(E(partLabel(part))).Append("</bdi></a></li>");
                sb.Append("</ol></li>");
            }
            else
                foreach (var part in run.Parts)
                    sb.Append("<li class=\"solo\"><a href=\"#p").Append(part.Number).Append("\" data-p=\"").Append(part.Number).Append("\"><span class=\"tick\"></span><bdi>").Append(E(partTitle(part))).Append("</bdi></a></li>");
        }
        sb.Append("</ol></nav>\n<main id=\"main\" tabindex=\"-1\">\n");

        // ── the overview ──
        var first = book.Parts[0];
        sb.Append("<section class=\"view home\" id=\"home\" aria-labelledby=\"h-home\">");
        sb.Append("<p class=\"kicker\">").Append(Svg("cap")).Append("<span>").Append(E(string.Format(CultureInfo.InvariantCulture, words["madeFrom"], book.Source))).Append("</span></p>");
        sb.Append("<h1 id=\"h-home\" tabindex=\"-1\"><bdi>").Append(E(book.Title)).Append("</bdi></h1>");
        if (book.Sample)
            sb.Append("<p class=\"sample\" role=\"note\">").Append(E(words["sample"])).Append("</p>");
        sb.Append("<ul class=\"stats\"><li>").Append(E(words.Count("parts", book.Parts.Count))).Append("</li>");
        if (book.Terms.Count > 0)
            sb.Append("<li>").Append(E(words.Count("terms", book.Terms.Count))).Append("</li>");
        if (cardCount > 0)
            sb.Append("<li>").Append(E(words.Count("cards", cardCount))).Append("</li>");
        if (questionCount > 0)
            sb.Append("<li>").Append(E(words.Count("questions", questionCount))).Append("</li>");
        if (activityCount > 0)
            sb.Append("<li>").Append(E(words.Count("activities", activityCount))).Append("</li>");
        sb.Append("</ul>");
        if (book.Overview.Length > 0)
            sb.Append("<div class=\"lead\">").Append(Rich(book.Overview, null)).Append("</div>");
        if (book.Goals.Count > 0)
        {
            sb.Append("<section class=\"box goals\"><h2>").Append(E(words["goals"])).Append("</h2><ul>");
            foreach (var goal in book.Goals)
                sb.Append("<li>").Append(Inline(goal, null, null)).Append("</li>");
            sb.Append("</ul></section>");
        }
        sb.Append("<section class=\"how needs-js\"><h2>").Append(E(words["howTo"])).Append("</h2><ol><li>").Append(E(words["step1"])).Append("</li><li>").Append(E(words["step2"])).Append("</li><li>").Append(E(words["step3"])).Append("</li></ol></section>");
        sb.Append("<p class=\"begin needs-js\"><a class=\"btn primary\" href=\"#p").Append(first.Number).Append("\">").Append(E(words["begin"])).Append(Svg("next")).Append("</a></p>");
        sb.Append("<section class=\"outline\"><h2>").Append(E(words["contents"])).Append("</h2><ol class=\"tiles\">");
        foreach (var run in Groups(book))
        {
            if (run.Chapter is { } chapter)
            {
                int c = numbers[chapter];
                sb.Append("<li><a class=\"tile\" href=\"#c").Append(c).Append("\" data-c=\"").Append(c).Append("\"><strong><bdi>").Append(E(chapter.Title)).Append("</bdi></strong>");
                if (chapter.Summary.Length > 0)
                    sb.Append("<span class=\"gist\">").Append(E(Clip(Flat(chapter.Summary), 220))).Append("</span>");
                sb.Append("<span class=\"small\">").Append(E(words.Count("parts", run.Parts.Count))).Append("</span><span class=\"bar\"><span></span></span></a></li>");
            }
            else
                foreach (var part in run.Parts)
                    sb.Append("<li><a class=\"tile\" href=\"#p").Append(part.Number).Append("\" data-p=\"").Append(part.Number).Append("\"><strong><bdi>").Append(E(partTitle(part))).Append("</bdi></strong><span class=\"gist\">")
                        .Append(E(Clip(part.Passages.SelectMany(x => x.Ideas).FirstOrDefault() ?? "", 220))).Append("</span><span class=\"small\">").Append(E(part.Where)).Append("</span><span class=\"bar\"><span></span></span></a></li>");
        }
        sb.Append("</ol></section>");
        sb.Append("</section>\n");

        // ── chapters ──
        for (int c = 0; c < book.Chapters.Count; c++)
        {
            var chapter = book.Chapters[c];
            var parts = book.Parts.Where(p => chapter.Parts.Contains(p.Number)).ToList();
            sb.Append("<section class=\"view chapter\" id=\"c").Append(c + 1).Append("\" aria-labelledby=\"h-c").Append(c + 1).Append("\">");
            sb.Append("<p class=\"kicker\">").Append(E(words["chapter"])).Append("</p><h1 id=\"h-c").Append(c + 1).Append("\" tabindex=\"-1\"><bdi>").Append(E(chapter.Title)).Append("</bdi></h1>");
            if (chapter.Summary.Length > 0)
                sb.Append("<div class=\"lead\">").Append(Rich(chapter.Summary, null)).Append("</div>");
            sb.Append("<ol class=\"rows\">");
            foreach (var part in parts)
            {
                sb.Append("<li><a href=\"#p").Append(part.Number).Append("\" data-p=\"").Append(part.Number).Append("\"><span class=\"tick\"></span><span class=\"t\"><bdi>").Append(E(partLabel(part))).Append("</bdi></span><span class=\"src\">").Append(E(part.Where)).Append("</span></a>");
                var ideas = part.Passages.SelectMany(x => x.Ideas).Take(2).ToList();
                if (ideas.Count > 0)
                    sb.Append("<ul class=\"mini\">").Append(string.Concat(ideas.Select(i => "<li>" + Inline(i, null, null) + "</li>"))).Append("</ul>");
                sb.Append("</li>");
            }
            sb.Append("</ol>");
            bool tested = parts.Sum(p => p.Quiz.Count) > 0, practised = parts.Sum(p => p.Activities.Count) > 0;
            if (tested || practised)
            {
                sb.Append("<p class=\"needs-js row\">");
                if (tested)
                    sb.Append("<a class=\"btn\" href=\"#test-c").Append(c + 1).Append("\">").Append(Svg("test")).Append(E(words["chapterTest"])).Append("</a>");
                if (practised)
                    sb.Append("<a class=\"btn\" href=\"#practice-c").Append(c + 1).Append("\">").Append(Svg("puzzle")).Append(E(words["practiceChapter"])).Append("</a>");
                sb.Append("</p>");
            }
            sb.Append("</section>\n");
        }

        // ── the parts ──
        for (int i = 0; i < book.Parts.Count; i++)
        {
            var part = book.Parts[i];
            var marked = new HashSet<int>();
            sb.Append("<section class=\"view part\" id=\"p").Append(part.Number).Append("\" data-p=\"").Append(part.Number).Append("\" aria-labelledby=\"h-p").Append(part.Number).Append("\">");
            if (chapterOf.TryGetValue(part.Number, out int c))
                sb.Append("<p class=\"crumb\"><a href=\"#c").Append(c).Append("\"><bdi>").Append(E(book.Chapters[c - 1].Title)).Append("</bdi></a></p>");
            sb.Append("<h1 id=\"h-p").Append(part.Number).Append("\" tabindex=\"-1\"><bdi>").Append(E(partLabel(part))).Append("</bdi></h1>");
            sb.Append("<p class=\"meta\"><span class=\"src\">").Append(Svg("pin")).Append(E(part.Where)).Append("</span><span class=\"state\"></span></p>");
            var ideas = Ideas(part);
            if (ideas.Count > 0)
            {
                sb.Append("<section class=\"box ideas\"><h2>").Append(Svg("bulb")).Append(E(words["keyIdeas"])).Append("</h2><ul>");
                foreach (var idea in ideas)
                    sb.Append("<li>").Append(Inline(idea, linker, marked)).Append("</li>");
                sb.Append("</ul></section>");
            }
            sb.Append("<section class=\"explain\"><h2>").Append(E(words["explained"])).Append("</h2>");
            for (int k = 0; k < part.Passages.Count; k++)
            {
                var passage = part.Passages[k];
                sb.Append("<article class=\"passage\" id=\"p").Append(part.Number).Append('-').Append(k + 1).Append("\">");
                if (part.Passages.Count > 1 && passage.Title.Length > 0)
                    sb.Append("<h3><bdi>").Append(E(passage.Title)).Append("</bdi></h3>");
                if (passage.Original.Length > 0)
                    sb.Append("<p class=\"note\">").Append(E(words["notExplained"])).Append("</p><blockquote class=\"raw\" dir=\"auto\" lang=\"").Append(book.SourceLanguage).Append("\">").Append(Rich(passage.Original, null)).Append("</blockquote>");
                else
                    sb.Append(Rich(passage.Explanation, linker, marked));
                sb.Append("<p class=\"src\">").Append(Svg("pin")).Append(E(passage.Where)).Append("</p></article>");
            }
            sb.Append("</section>");
            if (part.Visuals.Count > 0)
            {
                sb.Append("<section class=\"visuals\"><h2>").Append(Svg("eye")).Append(E(words["seeIt"])).Append("</h2>");
                foreach (var visual in part.Visuals)
                    AppendVisual(sb, visual, words);
                sb.Append("</section>");
            }
            var examples = part.Passages.Where(p => p.Example.Length > 0).ToList();
            if (examples.Count > 0)
            {
                sb.Append("<section class=\"box example\"><h2>").Append(Svg("pencil")).Append(E(words["example"])).Append("</h2>");
                foreach (var passage in examples)
                {
                    if (examples.Count > 1 && passage.Title.Length > 0)
                        sb.Append("<h3><bdi>").Append(E(passage.Title)).Append("</bdi></h3>");
                    sb.Append("<div class=\"steps\">").Append(Rich(passage.Example, linker, marked)).Append("</div>");
                }
                sb.Append("</section>");
            }
            var here = book.Terms.Where(t => t.Part == part.Number).ToList();
            if (here.Count > 0)
            {
                sb.Append("<section class=\"here\"><h2>").Append(E(words["termsHere"])).Append("</h2><ul class=\"chips\">");
                foreach (var term in here)
                    sb.Append("<li><a class=\"chip\" href=\"#").Append(ids[term]).Append("\" data-t=\"").Append(ids[term]).Append("\"><bdi>").Append(E(TermLinker.Shown(term.Term))).Append("</bdi></a></li>");
                sb.Append("</ul></section>");
            }
            if (part.Activities.Count > 0)
            {
                sb.Append("<section class=\"acts\"><h2>").Append(Svg("puzzle")).Append(E(words["tryIt"])).Append("</h2>");
                foreach (var activity in part.Activities.Where(activityIds.ContainsKey))
                    AppendActivity(sb, activity, activityIds[activity], words);
                sb.Append("</section>");
            }
            if (part.Quiz.Count > 0)
            {
                sb.Append("<section class=\"check\"><h2>").Append(Svg("test")).Append(E(words["check"])).Append("</h2>");
                foreach (var q in part.Quiz)
                    AppendQuestion(sb, q, Letter, words);
                sb.Append("</section>");
            }
            if (part.Cards.Count > 0)
            {
                sb.Append("<section class=\"deck\"><h2>").Append(Svg("cards")).Append(E(words["partCards"])).Append("</h2><div class=\"cards\">");
                foreach (var card in part.Cards)
                    AppendCard(sb, card, words);
                sb.Append("</div></section>");
            }
            sb.Append("<footer class=\"finish needs-js\"><button type=\"button\" class=\"btn mark\" data-p=\"").Append(part.Number).Append("\" aria-pressed=\"false\">").Append(Svg("check")).Append("<span>").Append(E(words["markStudied"])).Append("</span></button><nav class=\"pager\">");
            if (i > 0)
                sb.Append("<a class=\"btn quiet\" rel=\"prev\" href=\"#p").Append(book.Parts[i - 1].Number).Append("\">").Append(Svg("prev")).Append("<span>").Append(E(words["previous"])).Append("</span></a>");
            if (i + 1 < book.Parts.Count)
                sb.Append("<a class=\"btn quiet\" rel=\"next\" href=\"#p").Append(book.Parts[i + 1].Number).Append("\"><span>").Append(E(words["next"])).Append("</span>").Append(Svg("next")).Append("</a>");
            sb.Append("</nav></footer></section>\n");
        }

        // ── the glossary ──
        if (book.Terms.Count > 0)
        {
            var collator = StringComparer.Create(Culture(book.Language), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            sb.Append("<section class=\"view glossary\" id=\"terms\" aria-labelledby=\"h-terms\"><h1 id=\"h-terms\" tabindex=\"-1\">").Append(E(words["terms"])).Append("</h1>");
            sb.Append("<p class=\"needs-js\"><input type=\"search\" class=\"filter\" placeholder=\"").Append(A(words["findTerm"])).Append("\" aria-label=\"").Append(A(words["findTerm"])).Append("\"></p><dl class=\"gloss\">");
            foreach (var term in book.Terms.OrderBy(t => TermLinker.Shown(t.Term), collator))
            {
                var part = book.Parts.FirstOrDefault(p => p.Number == term.Part);
                sb.Append("<div class=\"g\" id=\"").Append(ids[term]).Append("\"><dt><bdi>").Append(E(TermLinker.Shown(term.Term))).Append("</bdi></dt><dd>").Append(E(term.Meaning));
                if (part is not null)
                    sb.Append(" <a class=\"src\" href=\"#p").Append(part.Number).Append("\">").Append(Svg("pin")).Append(E(term.Where)).Append(" · <bdi>").Append(E(partTitle(part))).Append("</bdi></a>");
                sb.Append("</dd></div>");
            }
            sb.Append("</dl></section>\n");
        }

        // ── what needs the script: the cards one by one, the test, the progress ──
        if (cardCount > 0)
            sb.Append("<section class=\"view trainer needs-js\" id=\"cards\" aria-labelledby=\"h-cards\"><h1 id=\"h-cards\" tabindex=\"-1\">").Append(E(words["cards"])).Append("</h1><div class=\"slot\"></div></section>\n");
        if (questionCount > 0)
            sb.Append("<section class=\"view exam needs-js\" id=\"test\" aria-labelledby=\"h-test\"><h1 id=\"h-test\" tabindex=\"-1\">").Append(E(words["test"])).Append("</h1><div class=\"slot\"></div></section>\n");
        if (activityCount > 0)
            sb.Append("<section class=\"view practice needs-js\" id=\"practice\" aria-labelledby=\"h-practice\"><h1 id=\"h-practice\" tabindex=\"-1\">").Append(E(words["activities"])).Append("</h1><div class=\"slot\"></div></section>\n");
        sb.Append("<section class=\"view stand needs-js\" id=\"progress\" aria-labelledby=\"h-progress\"><h1 id=\"h-progress\" tabindex=\"-1\">").Append(E(words["progress"])).Append("</h1><div class=\"slot\"></div></section>\n");
        sb.Append("<noscript><p class=\"note\">").Append(E(words["needsScript"])).Append("</p></noscript>\n");
        sb.Append("<footer class=\"foot\"><p>").Append(E(string.Format(CultureInfo.InvariantCulture, words["madeFrom"], book.Source))).Append(" · <time datetime=\"").Append(book.Made.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("\">").Append(book.Made.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</time> · DropWheel</p>");
        sb.Append("<p class=\"ai\">").Append(Svg("info")).Append("<span>").Append(E(string.Format(CultureInfo.InvariantCulture, words["aiNote"], book.Writer))).Append("</span></p></footer>\n</main>\n</div>\n");
        sb.Append("<div class=\"pop\" id=\"pop\" role=\"tooltip\" hidden></div>\n<div class=\"sr\" id=\"say\" aria-live=\"polite\"></div>\n");
        sb.Append("<script type=\"application/json\" id=\"data\">").Append(Data(book, words, ids, activityIds)).Append("</script>\n");
        sb.Append("<script>").Append(script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Each activity's name, by its kind and words: progress stays with it when the file is made again. The same activity twice is shown once.</summary>
    private static Dictionary<StudyActivity, string> ActivityIds(StudyBook book)
    {
        var ids = new Dictionary<StudyActivity, string>(ReferenceEqualityComparer.Instance);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in book.Parts.SelectMany(p => p.Activities))
        {
            var id = ItemId("a", activity.Kind + "\n" + activity.Prompt + "\n" + string.Join("\n", activity.Items.Select(i => i.Text)));
            if (taken.Add(id))
                ids[activity] = id;
        }
        return ids;
    }

    /// <summary>The blanks of an activity's sentences, in order.</summary>
    internal static List<string> BlanksOf(StudyActivity activity) =>
        activity.Items.SelectMany(i => StudyActivity.Pieces(i.Text).Where((_, k) => k % 2 == 1)).Select(b => b.Trim()).ToList();

    /// <summary>The order an activity's pieces are first shown in (for blanks, its words to place): mixed, the same every time.</summary>
    internal static int[] Mixed(StudyActivity activity) => activity.Kind switch
    {
        StudyActivityKind.Blanks => StudyActivity.Mixed(activity.Prompt + string.Concat(activity.Items.Select(i => i.Text)), BlanksOf(activity).Count),
        StudyActivityKind.TrueFalse => Enumerable.Range(0, activity.Items.Count).ToArray(),
        _ => StudyActivity.Mixed(activity.Prompt + string.Concat(activity.Items.Select(i => i.Text + i.Answer)), activity.Items.Count),
    };

    /// <summary>A diagram: steps with arrows between (round again for a cycle), dates down a line, an idea with its branches, or a table that compares.</summary>
    private static void AppendVisual(StringBuilder sb, StudyVisual visual, StudyFileWords words)
    {
        string Text(string t) => Inline(t, null, null);
        switch (visual.Kind)
        {
            case StudyVisualKind.Flow or StudyVisualKind.Cycle:
                // Steps that say what happens in each are gone through one at a time (all of them shown without the script
                // and in print); short steps without it go side by side on a wide screen, long ones one under another.
                bool walk = visual.Items.Count >= 3 && visual.Items.All(i => i.Details.Count > 0);
                bool brief = !walk && visual.Items.Count <= 5 && visual.Items.All(i => i.Text.Length <= 48);
                sb.Append("<figure class=\"vis flow").Append(visual.Kind == StudyVisualKind.Cycle ? " cycle" : "").Append(brief ? " brief" : "").Append(walk ? " walk" : "").Append("\">");
                if (visual.Title.Length > 0)
                    sb.Append("<figcaption>").Append(Text(visual.Title)).Append("</figcaption>");
                sb.Append("<ol>");
                for (int i = 0; i < visual.Items.Count; i++)
                {
                    sb.Append("<li><span class=\"n\">").Append(i + 1).Append("</span><span class=\"t\">").Append(Text(visual.Items[i].Text));
                    if (walk)
                        sb.Append("<span class=\"d\">").Append(Text(visual.Items[i].Details[0])).Append("</span>");
                    sb.Append("</span></li>");
                }
                sb.Append("</ol>");
                if (visual.Kind == StudyVisualKind.Cycle)
                    sb.Append("<p class=\"loop\">").Append(Svg("loop")).Append("<span>").Append(E(words["backToStart"])).Append("</span></p>");
                sb.Append("</figure>");
                break;
            case StudyVisualKind.Timeline:
                sb.Append("<figure class=\"vis timeline\">");
                if (visual.Title.Length > 0)
                    sb.Append("<figcaption>").Append(Text(visual.Title)).Append("</figcaption>");
                sb.Append("<ol>");
                foreach (var item in visual.Items)
                    sb.Append("<li><span class=\"when\"><bdi>").Append(E(item.Answer)).Append("</bdi></span><span class=\"t\">").Append(Text(item.Text)).Append("</span></li>");
                sb.Append("</ol></figure>");
                break;
            case StudyVisualKind.Map:
                sb.Append("<figure class=\"vis map\"><figcaption class=\"hub\">").Append(Text(visual.Title)).Append("</figcaption><ul class=\"arms\">");
                foreach (var branch in visual.Items)
                {
                    sb.Append("<li><strong>").Append(Text(branch.Text)).Append("</strong>");
                    if (branch.Details.Count > 0)
                        sb.Append("<ul>").Append(string.Concat(branch.Details.Select(d => "<li>" + Text(d) + "</li>"))).Append("</ul>");
                    sb.Append("</li>");
                }
                sb.Append("</ul></figure>");
                break;
            default:
                sb.Append("<figure class=\"vis compare\">");
                if (visual.Title.Length > 0)
                    sb.Append("<figcaption>").Append(Text(visual.Title)).Append("</figcaption>");
                sb.Append("<div class=\"table\"><table><thead><tr><th></th>").Append(string.Concat(visual.Columns.Select(c => "<th scope=\"col\">" + Text(c) + "</th>"))).Append("</tr></thead><tbody>");
                foreach (var row in visual.Items)
                    sb.Append("<tr><th scope=\"row\">").Append(Text(row.Text)).Append("</th>").Append(string.Concat(row.Details.Select(d => "<td>" + Text(d) + "</td>"))).Append("</tr>");
                sb.Append("</tbody></table></div></figure>");
                break;
        }
    }

    /// <summary>
    /// An activity as a worksheet: its pieces mixed, room to answer, and its answer in a part that opens; printed with or
    /// without the answers. The page's script makes it pieces to drag or press into place.
    /// </summary>
    private static void AppendActivity(StringBuilder sb, StudyActivity activity, string id, StudyFileWords words)
    {
        string Text(string t) => Inline(t, null, null);
        var (kind, label) = activity.Kind switch
        {
            StudyActivityKind.Order => ("order", words["kindOrder"]),
            StudyActivityKind.Match => ("match", words["kindMatch"]),
            StudyActivityKind.Sort => ("sort", words["kindSort"]),
            StudyActivityKind.Blanks => ("blanks", words["kindBlanks"]),
            StudyActivityKind.MapFill => ("mapfill", words["kindMapFill"]),
            _ => ("tf", words["kindTrueFalse"]),
        };
        var order = Mixed(activity);
        var items = activity.Items;
        sb.Append("<div class=\"act\" id=\"").Append(id).Append("\" data-a=\"").Append(id).Append("\" data-k=\"").Append(kind).Append("\">");
        sb.Append("<p class=\"kind\">").Append(E(label)).Append("</p>");
        if (activity.Prompt.Length > 0)
            sb.Append("<p class=\"ask\">").Append(Text(activity.Prompt)).Append("</p>");
        var key = new StringBuilder();
        switch (activity.Kind)
        {
            case StudyActivityKind.Order:
                sb.Append("<ul class=\"sheet pieces\">").Append(string.Concat(order.Select(i => "<li><span class=\"box\"></span>" + Text(items[i].Text) + "</li>"))).Append("</ul>");
                key.Append("<ol>").Append(string.Concat(items.Select(i => "<li>" + Text(i.Text) + "</li>"))).Append("</ol>");
                break;
            case StudyActivityKind.Match:
                sb.Append("<div class=\"sheet two\"><ol>").Append(string.Concat(items.Select(i => "<li>" + Text(i.Text) + " <span class=\"line\"></span></li>"))).Append("</ol><ul class=\"pieces\">")
                    .Append(string.Concat(order.Select(i => "<li>" + Text(items[i].Answer) + "</li>"))).Append("</ul></div>");
                key.Append("<ul>").Append(string.Concat(items.Select(i => "<li>" + Text(i.Text) + " → <strong>" + Text(i.Answer) + "</strong></li>"))).Append("</ul>");
                break;
            case StudyActivityKind.Sort:
                sb.Append("<div class=\"sheet\"><ul class=\"pieces inline\">").Append(string.Concat(order.Select(i => "<li>" + Text(items[i].Text) + "</li>"))).Append("</ul><div class=\"bins\">")
                    .Append(string.Concat(activity.Groups.Select(g => "<div class=\"bin\"><strong>" + Text(g) + "</strong></div>"))).Append("</div></div>");
                key.Append("<ul>").Append(string.Concat(activity.Groups.Select(g => "<li><strong>" + Text(g) + ":</strong> " + string.Join(" · ", items.Where(i => i.Answer == g).Select(i => Text(i.Text))) + "</li>"))).Append("</ul>");
                break;
            case StudyActivityKind.Blanks:
                var blanks = BlanksOf(activity);
                sb.Append("<div class=\"sheet\"><ol class=\"sentences\">");
                foreach (var item in items)
                    sb.Append("<li>").Append(string.Concat(StudyActivity.Pieces(item.Text).Select((piece, k) => k % 2 == 1 ? "<span class=\"gap\"></span>" : Text(piece)))).Append("</li>");
                sb.Append("</ol><p class=\"bankline\"><strong>").Append(E(words["bank"])).Append(":</strong> ").Append(string.Join(" · ", order.Select(i => "<bdi>" + E(blanks[i]) + "</bdi>"))).Append("</p></div>");
                key.Append("<ol>");
                foreach (var item in items)
                    key.Append("<li>").Append(string.Concat(StudyActivity.Pieces(item.Text).Select((piece, k) => k % 2 == 1 ? "<strong>" + Text(piece.Trim()) + "</strong>" : Text(piece)))).Append("</li>");
                key.Append("</ol>");
                break;
            case StudyActivityKind.MapFill:
                // The map with a line for each branch's name, under what the branch says; the names to write in below.
                sb.Append("<div class=\"sheet mapsheet\"><p class=\"hub\">").Append(Text(activity.Prompt)).Append("</p><ol class=\"arms\">");
                foreach (var item in items)
                    sb.Append("<li><span class=\"line\"></span><ul>").Append(string.Concat(item.Text.Split("; ").Select(d => "<li>" + Text(d) + "</li>"))).Append("</ul></li>");
                sb.Append("</ol><p class=\"bankline\"><strong>").Append(E(words["bank"])).Append(":</strong> ").Append(string.Join(" · ", order.Select(i => "<bdi>" + E(items[i].Answer) + "</bdi>"))).Append("</p></div>");
                key.Append("<ul>").Append(string.Concat(items.Select(i => "<li><strong>" + Text(i.Answer) + "</strong> — " + Text(i.Text) + "</li>"))).Append("</ul>");
                break;
            default:
                sb.Append("<ol class=\"sheet statements\">").Append(string.Concat(items.Select(i => "<li>" + Text(i.Text) + " <span class=\"tf\">" + E(words["true"]) + " / " + E(words["false"]) + "</span></li>"))).Append("</ol>");
                key.Append("<ol>");
                foreach (var item in items)
                {
                    key.Append("<li><strong>").Append(E(item.Answer == "true" ? words["true"] : words["false"])).Append("</strong>");
                    if (item.Note.Length > 0)
                        key.Append(" — ").Append(Text(item.Note));
                    key.Append("</li>");
                }
                key.Append("</ol>");
                break;
        }
        sb.Append("<details class=\"key\"><summary>").Append(E(words["answer"])).Append("</summary>").Append(key).Append("<p class=\"src\">").Append(Svg("pin")).Append(E(activity.Where)).Append("</p></details></div>");
    }

    /// <summary>A question: its four choices as buttons, and its answer (with why, where and the file's own sentence) in a part that opens.</summary>
    private static void AppendQuestion(StringBuilder sb, StudyQuestion q, Func<int, string> letter, StudyFileWords words)
    {
        sb.Append("<div class=\"q\" data-q=\"").Append(ItemId("q", q.Question)).Append("\" data-a=\"").Append(q.Correct).Append("\"><p class=\"ask\">").Append(E(q.Question)).Append("</p><ol class=\"opts\">");
        for (int i = 0; i < q.Options.Count; i++)
            sb.Append("<li><button type=\"button\" class=\"opt\" data-i=\"").Append(i).Append("\"><span class=\"l\">").Append(E(letter(i))).Append("</span><span class=\"t\">").Append(E(q.Options[i])).Append("</span></button></li>");
        sb.Append("</ol><details class=\"key\"><summary>").Append(E(words["answer"])).Append("</summary><p class=\"verdict\"></p><p><strong>").Append(E(letter(q.Correct))).Append(") ").Append(E(q.Options[q.Correct])).Append("</strong>");
        if (q.Why.Length > 0)
            sb.Append(" — ").Append(E(q.Why));
        sb.Append("</p><p class=\"src\">").Append(Svg("pin")).Append(E(q.Where)).Append("</p>");
        if (q.Quote.Length > 0)
            sb.Append("<blockquote class=\"from\" dir=\"auto\"><span class=\"label\">").Append(E(words["fromOriginal"])).Append("</span> <q>").Append(E(q.Quote)).Append("</q></blockquote>");
        sb.Append("<button type=\"button\" class=\"link redo needs-js\">").Append(E(words["redo"])).Append("</button></details></div>");
    }

    private static void AppendCard(StringBuilder sb, StudyCard card, StudyFileWords words)
    {
        sb.Append("<button type=\"button\" class=\"card\" data-c=\"").Append(ItemId("c", card.Front)).Append("\" aria-label=\"").Append(A(card.Front + " · " + words["turn"])).Append("\"><span class=\"face front\"><span class=\"t\">").Append(E(card.Front)).Append("</span></span>");
        sb.Append("<span class=\"face back\"><span class=\"t\">").Append(E(card.Back)).Append("</span><span class=\"src\">").Append(E(card.Where)).Append("</span></span></button>");
    }

    /// <summary>What the script works from: the words of the page, and the parts, chapters, cards, questions and terms.</summary>
    private static string Data(StudyBook book, StudyFileWords words, Dictionary<StudyTerm, string> ids, Dictionary<StudyActivity, string> activityIds)
    {
        static string Kind(StudyActivityKind kind) => kind switch
        {
            StudyActivityKind.Order => "order",
            StudyActivityKind.Match => "match",
            StudyActivityKind.Sort => "sort",
            StudyActivityKind.Blanks => "blanks",
            StudyActivityKind.MapFill => "mapfill",
            _ => "tf",
        };
        var chapterOf = new Dictionary<int, int>();
        for (int c = 0; c < book.Chapters.Count; c++)
            foreach (var n in book.Chapters[c].Parts)
                chapterOf[n] = c + 1;
        var data = new Dictionary<string, object>
        {
            ["lang"] = book.Language,
            ["w"] = StudyFileWords.Keys.ToDictionary(k => k, k => words[k]),
            ["parts"] = book.Parts.Select(p => new Dictionary<string, object> { ["n"] = p.Number, ["c"] = chapterOf.GetValueOrDefault(p.Number), ["t"] = Label(p, words) }).ToList(),
            ["chapters"] = book.Chapters.Select((c, i) => new Dictionary<string, object> { ["n"] = i + 1, ["t"] = c.Title, ["p"] = c.Parts }).ToList(),
            ["cards"] = book.Parts.SelectMany(p => p.Cards.Select(c => new Dictionary<string, object> { ["id"] = ItemId("c", c.Front), ["p"] = p.Number, ["f"] = c.Front, ["b"] = c.Back, ["w"] = c.Where })).ToList(),
            ["quiz"] = book.Parts.SelectMany(p => p.Quiz.Select(q => new Dictionary<string, object> { ["id"] = ItemId("q", q.Question), ["p"] = p.Number, ["q"] = q.Question, ["o"] = q.Options, ["a"] = q.Correct, ["y"] = q.Why, ["w"] = q.Where, ["x"] = q.Quote })).ToList(),
            ["terms"] = book.Terms.Select(t => new Dictionary<string, object> { ["id"] = ids[t], ["t"] = TermLinker.Shown(t.Term), ["m"] = t.Meaning, ["w"] = t.Where, ["p"] = t.Part }).ToList(),
            // Each activity: its kind, what it asks, its pieces as [text, answer, note] and the order they are first shown in.
            ["acts"] = book.Parts.SelectMany(p => p.Activities.Where(activityIds.ContainsKey).Select(a => new Dictionary<string, object>
            {
                ["id"] = activityIds[a],
                ["p"] = p.Number,
                ["k"] = Kind(a.Kind),
                ["t"] = a.Prompt,
                ["w"] = a.Where,
                ["i"] = a.Items.Select(i => new[] { i.Text, i.Answer, i.Note }).ToList(),
                ["g"] = a.Groups,
                ["o"] = Mixed(a),
            })).ToList(),
        };
        // Every letter as it is (an Arabic file isn't six times its size), but nothing that could close the script.
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
    }

    /// <summary>
    /// A part's key ideas: the first of each passage's, then the second, and so on, so a long part's are from all of it;
    /// a few more for a long part, never the same twice.
    /// </summary>
    internal static List<string> Ideas(StudyPart part)
    {
        int most = Math.Clamp(part.Passages.Count + 3, 5, 10);
        var ideas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int round = 0; ideas.Count < most && part.Passages.Any(p => p.Ideas.Count > round); round++)
            foreach (var passage in part.Passages)
                if (round < passage.Ideas.Count && ideas.Count < most && seen.Add(Key(passage.Ideas[round])))
                    ideas.Add(passage.Ideas[round]);
        return ideas;
    }

    /// <summary>A part as the contents name it: its heading, "Introduction" for a chapter's words before its first section, its place when it has no heading.</summary>
    private static string Label(StudyPart p, StudyFileWords words) =>
        p.Chapter.Length > 0 && p.Title == p.Chapter ? words["intro"] : p.Title.Length > 0 ? p.Title : p.Where;

    /// <summary>The file's own name for its progress in the browser: the same study file made again keeps it.</summary>
    internal static string Id(StudyBook book)
    {
        var identity = book.Title + "\n" + book.Source + "\n" + string.Join("\n", book.Parts.Select(p => p.Title));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
    }

    /// <summary>A card's or question's name: by its words, so progress stays with it when the file is made again.</summary>
    internal static string ItemId(string kind, string text) =>
        kind + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(text))))[..10].ToLowerInvariant();

    private static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Parts in the order they come, those of a chapter together with it.</summary>
    private static List<(StudyChapter? Chapter, List<StudyPart> Parts)> Groups(StudyBook book)
    {
        var groups = new List<(StudyChapter?, List<StudyPart>)>();
        foreach (var part in book.Parts)
        {
            var chapter = book.Chapters.FirstOrDefault(c => c.Parts.Contains(part.Number));
            if (groups.Count > 0 && groups[^1].Item1 is { } open && ReferenceEquals(open, chapter))
                groups[^1].Item2.Add(part);
            else if (chapter is null && groups.Count > 0 && groups[^1].Item1 is null)
                groups[^1].Item2.Add(part);
            else
                groups.Add((chapter, [part]));
        }
        return groups;
    }

    private static CultureInfo Culture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    // ───────────── text ─────────────

    private static string E(string text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>Text for an attribute.</summary>
    private static string A(string text) => WebUtility.HtmlEncode(text ?? "").Replace("\n", " ");

    private static string Flat(string text) => Regex.Replace(text.Replace("**", ""), @"\s+", " ").Trim();

    private static string Clip(string text, int most)
    {
        if (text.Length <= most)
            return text;
        int cut = text.LastIndexOf(' ', most - 1, most / 2);
        return text[..(cut > 0 ? cut : most)] + "…";
    }

    /// <summary>
    /// What a model wrote, as the page shows it: paragraphs (a blank line between), lists (lines begun with "-" or a
    /// number), simple tables (lines between "|"), headings ("### "), bold ("**…**") and code ("`…`"); everything else
    /// is text, never markup. Terms of the glossary are marked where they first come in the part.
    /// </summary>
    internal static string Rich(string text, TermLinker? linker, HashSet<int>? marked = null)
    {
        var sb = new StringBuilder();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                i++;
                continue;
            }
            if (Regex.Match(line, @"^#{1,6}\s+(.+)$") is { Success: true } heading)
            {
                sb.Append("<h4>").Append(Inline(heading.Groups[1].Value.Trim('#', ' '), linker, marked)).Append("</h4>");
                i++;
            }
            else if (BulletLine().IsMatch(line) || NumberLine().IsMatch(line))
            {
                bool numbered = NumberLine().IsMatch(line);
                var item = numbered ? NumberLine() : BulletLine();
                sb.Append(numbered ? "<ol>" : "<ul>");
                while (i < lines.Length && lines[i].Trim() is { Length: > 0 } l && item.IsMatch(l))
                {
                    sb.Append("<li>").Append(Inline(item.Replace(l, "", 1), linker, marked)).Append("</li>");
                    i++;
                }
                sb.Append(numbered ? "</ol>" : "</ul>");
            }
            else if (line.StartsWith('|') && line.EndsWith('|') && line.Length > 2)
            {
                sb.Append("<div class=\"table\"><table>");
                bool head = true;
                while (i < lines.Length && lines[i].Trim() is { Length: > 2 } l && l.StartsWith('|') && l.EndsWith('|'))
                {
                    var cells = l[1..^1].Split('|').Select(c => c.Trim()).ToList();
                    i++;
                    if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$")))
                        continue;
                    var tag = head && i < lines.Length && Regex.IsMatch(lines[i].Trim(), @"^\|[\s:|-]+\|$") ? "th" : "td";
                    sb.Append("<tr>").Append(string.Concat(cells.Select(c => $"<{tag}>{Inline(c, linker, marked)}</{tag}>"))).Append("</tr>");
                    head = false;
                }
                sb.Append("</table></div>");
            }
            else
            {
                var paragraph = new List<string>();
                while (i < lines.Length && lines[i].Trim() is { Length: > 0 } l && !BulletLine().IsMatch(l) && !NumberLine().IsMatch(l) && !l.StartsWith('|') && !Regex.IsMatch(l, @"^#{1,6}\s"))
                {
                    paragraph.Add(Inline(l, linker, marked));
                    i++;
                }
                sb.Append("<p>").Append(string.Join("<br>", paragraph)).Append("</p>");
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^[-•*·▪]\s+")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\(?[0-9٠-٩۰-۹]{1,2}[.)]\s+")]
    private static partial Regex NumberLine();

    /// <summary>A line's bold, its emphasis and its code, and its terms; the rest as text.</summary>
    internal static string Inline(string text, TermLinker? linker, HashSet<int>? marked)
    {
        var sb = new StringBuilder();
        foreach (var part in Marked().Split(text))
        {
            if (part.Length == 0)
                continue;
            if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length > 4)
                sb.Append("<strong>").Append(Linked(part[2..^2], linker, marked)).Append("</strong>");
            else if (part.Length > 2 && (part[0] == '*' && part[^1] == '*' || part[0] == '_' && part[^1] == '_'))
                sb.Append("<em>").Append(Linked(part[1..^1], linker, marked)).Append("</em>");
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                sb.Append("<code>").Append(E(part[1..^1])).Append("</code>");
            else
                sb.Append(Linked(part.Replace("**", ""), linker, marked));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bold ("**…**"), emphasis ("*…*", or "_…_" between words) and code ("`…`"). A star with a space after it, as in
    /// "2 * 3", is no emphasis, and neither is a word joined by underscores.
    /// </summary>
    [GeneratedRegex(@"(\*\*[^*\n]+\*\*|\*(?=\S)[^*\n]*?\S\*|(?<![\p{L}\p{N}])_(?=\S)[^_\n]*?\S_(?![\p{L}\p{N}])|`[^`\n]+`)")]
    private static partial Regex Marked();

    private static string Linked(string text, TermLinker? linker, HashSet<int>? marked) =>
        linker is null || marked is null ? Written(text) : linker.Mark(text, marked);

    /// <summary>
    /// What the model or the file wrote, as text: in a page read right to left, each run of words in Latin letters is
    /// isolated (bdi), so "(chemical energy)" in an Arabic sentence keeps its words in order and its brackets around them.
    /// </summary>
    internal static string Written(string text)
    {
        if (!_isolating)
            return E(text);
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match run in LatinRun().Matches(text))
        {
            int start = run.Index, end = run.Index + run.Length;
            // A short term in brackets stays on one line with its brackets: broken over two, its words and brackets scatter.
            bool bracketed = start > at && text[start - 1] == '(' && end < text.Length && text[end] == ')' && run.Length <= 32;
            if (bracketed)
            {
                sb.Append(E(text[at..(start - 1)])).Append("<span class=\"nb\">(<bdi>").Append(E(run.Value)).Append("</bdi>)</span>");
                at = end + 1;
            }
            else
            {
                sb.Append(E(text[at..start])).Append("<bdi>").Append(E(run.Value)).Append("</bdi>");
                at = end;
            }
        }
        return sb.Append(E(text[at..])).ToString();
    }

    [GeneratedRegex(@"[A-Za-z\u00C0-\u024F][A-Za-z0-9\u00C0-\u024F'’+\-]*(?: [A-Za-z0-9\u00C0-\u024F][A-Za-z0-9\u00C0-\u024F'’+\-]*)*")]
    private static partial Regex LatinRun();

    // ───────────── the page's parts ─────────────

    private static string Resource(string name)
    {
        using var stream = typeof(StudyFileWriter).Assembly.GetManifestResourceStream("Petal.Recipes.Study." + name)
            ?? throw new InvalidOperationException($"The study file's {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>The page's few pictures, drawn inline (Lucide's lines, ISC).</summary>
    private static string Svg(string name)
    {
        var path = name switch
        {
            "menu" => "<path d=\"M4 6h16M4 12h16M4 18h16\"/>",
            "search" => "<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-3.5-3.5\"/>",
            "contrast" => "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 3v18a9 9 0 0 0 0-18z\" fill=\"currentColor\"/>",
            "home" => "<path d=\"M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z\"/>",
            "book" => "<path d=\"M12 7v14M3 18a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h5a4 4 0 0 1 4 4 4 4 0 0 1 4-4h5a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1h-6a3 3 0 0 0-3 3 3 3 0 0 0-3-3z\"/>",
            "cards" => "<rect x=\"3\" y=\"6\" width=\"14\" height=\"14\" rx=\"2\"/><path d=\"M7 3h11a3 3 0 0 1 3 3v11\"/>",
            "test" => "<path d=\"M9 11l3 3L22 4\"/><path d=\"M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11\"/>",
            "chart" => "<path d=\"M3 3v18h18\"/><path d=\"M7 16v-5M12 16V8M17 16v-8\"/>",
            "cap" => "<path d=\"M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z\"/><path d=\"M22 10v6\"/><path d=\"M6 12.5V16a6 3 0 0 0 12 0v-3.5\"/>",
            "info" => "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 16v-4M12 8h.01\"/>",
            "pin" => "<path d=\"M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0 1 16 0z\"/><circle cx=\"12\" cy=\"10\" r=\"3\"/>",
            "bulb" => "<path d=\"M9 18h6M10 22h4M12 2a7 7 0 0 0-4 12.7V17h8v-2.3A7 7 0 0 0 12 2z\"/>",
            "pencil" => "<path d=\"M21.2 6.8a2.8 2.8 0 0 0-4-4L4 16v4h4z\"/><path d=\"m14.5 5.5 4 4\"/>",
            "check" => "<path d=\"M20 6 9 17l-5-5\"/>",
            "eye" => "<path d=\"M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>",
            "puzzle" => "<path d=\"M19.4 13.8a2.4 2.4 0 1 0 0-3.6V7a1 1 0 0 0-1-1h-3.2a2.4 2.4 0 1 0-3.6 0H8.4a1 1 0 0 0-1 1v3.2a2.4 2.4 0 1 1 0 3.6V17a1 1 0 0 0 1 1h3.2a2.4 2.4 0 1 1 3.6 0h3.2a1 1 0 0 0 1-1z\"/>",
            "loop" => "<path d=\"M3 12a9 9 0 0 1 15.5-6.2L21 8\"/><path d=\"M21 3v5h-5\"/><path d=\"M21 12a9 9 0 0 1-15.5 6.2L3 16\"/><path d=\"M3 21v-5h5\"/>",
            "next" => "<path class=\"fwd\" d=\"m9 18 6-6-6-6\"/>",
            "prev" => "<path class=\"fwd\" d=\"m15 18-6-6 6-6\"/>",
            _ => "",
        };
        return "<svg class=\"i\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\">" + path + "</svg>";
    }
}

/// <summary>
/// Marks the glossary's terms in a part's text, the first time each comes in it: by the term's words compared folded
/// (case, Arabic letters' forms and vowel marks aside), allowing Arabic's joined little words before it (و ف ب ك ل،
/// the article) and a short ending after it in languages written with Latin letters ("cells", "hücreler").
/// </summary>
internal sealed class TermLinker
{
    private sealed record Pattern(int Term, string[] Words);

    private readonly Dictionary<string, List<Pattern>> _byFirst = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<StudyTerm> _terms;

    private static readonly string[] Proclitics = ["", "و", "ف", "ب", "ك", "ل", "ال", "وال", "فال", "بال", "كال", "لل", "ولل", "فلل"];

    public TermLinker(IReadOnlyList<StudyTerm> terms)
    {
        _terms = terms;
        for (int i = 0; i < terms.Count; i++)
        {
            foreach (var form in new[] { StudyFile.Bare(terms[i].Term), StudyFile.Bracketed(terms[i].Term) })
            {
                var words = Words(form).Select(w => w.Folded).ToArray();
                if (words.Length == 0 || words.Sum(w => w.Length) < 3)
                    continue;
                words[0] = Core(words[0]);
                if (!_byFirst.TryGetValue(words[0], out var list))
                    _byFirst[words[0]] = list = [];
                list.Add(new Pattern(i, words));
            }
        }
        foreach (var list in _byFirst.Values)
            list.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));
    }

    /// <summary>A term as shown: without stray marks.</summary>
    public static string Shown(string term) => StudyFile.Plainly(term);

    /// <summary>The text with each term not yet <paramref name="marked"/> in its part marked once, as markup; the rest as text.</summary>
    public string Mark(string text, HashSet<int> marked)
    {
        var words = Words(text);
        var sb = new StringBuilder();
        int written = 0;
        for (int i = 0; i < words.Count; i++)
        {
            if (Find(words, i) is not var (term, length) || marked.Contains(term))
                continue;
            marked.Add(term);
            int start = words[i].Start, end = words[i + length - 1].End;
            sb.Append(StudyFileWriter.Written(text[written..start]));
            sb.Append("<span class=\"term\" tabindex=\"0\" data-t=\"t").Append(term + 1).Append("\" title=\"").Append(WebUtility.HtmlEncode(_terms[term].Meaning)).Append("\">")
                .Append(StudyFileWriter.Written(text[start..end])).Append("</span>");
            written = end;
            i += length - 1;
        }
        sb.Append(StudyFileWriter.Written(text[written..]));
        return sb.ToString();
    }

    private (int Term, int Length)? Find(List<(string Folded, int Start, int End)> words, int at)
    {
        var word = words[at].Folded;
        foreach (var (candidate, ending) in Candidates(word))
        {
            if (!_byFirst.TryGetValue(candidate, out var patterns))
                continue;
            foreach (var pattern in patterns)
            {
                // An ending is the last word's: a term of several words takes none on its first.
                if (at + pattern.Words.Length > words.Count || ending > 0 && (pattern.Words.Length > 1 || ending > Ending(candidate)))
                    continue;
                bool all = true;
                for (int k = 1; k < pattern.Words.Length && all; k++)
                    all = k == pattern.Words.Length - 1 ? Ends(words[at + k].Folded, pattern.Words[k]) : words[at + k].Folded == pattern.Words[k];
                if (all)
                    return (pattern.Term, pattern.Words.Length);
            }
        }
        return null;
    }

    /// <summary>
    /// What a word of the text may be the start of a term as, and how many letters of ending it was given: itself,
    /// without an Arabic little word joined before it, or (Latin letters) without a short ending.
    /// </summary>
    private static IEnumerable<(string Word, int Ending)> Candidates(string word)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in Proclitics)
            if (word.Length - prefix.Length >= 2 && word.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(word[prefix.Length..]))
                yield return (word[prefix.Length..], 0);
        if (IsLatin(word))
            for (int cut = 1; cut <= 3 && word.Length - cut >= 4; cut++)
                if (seen.Add(word[..^cut]))
                    yield return (word[..^cut], cut);
    }

    /// <summary>The most letters of ending a word of a term takes ("cells", "hücreler"): none on a short word, more on a long one.</summary>
    private static int Ending(string wanted) => !IsLatin(wanted) || wanted.Length < 4 ? 0 : wanted.Length < 6 ? 2 : 3;

    /// <summary>A word of the text as a term's word: the same, or with an ending it may take.</summary>
    private static bool Ends(string word, string wanted) =>
        word == wanted || word.Length - wanted.Length is > 0 and var extra && extra <= Ending(wanted) && word.StartsWith(wanted, StringComparison.Ordinal);

    private static bool IsLatin(string word) => word.Length > 0 && word.All(c => c < 0x0590);

    /// <summary>A term's first word without the Arabic article, as the text's words are looked up.</summary>
    private static string Core(string word) =>
        word.Length > 4 && word.StartsWith("ال", StringComparison.Ordinal) ? word[2..] : word;

    /// <summary>The text's words, each folded, with where it is.</summary>
    internal static List<(string Folded, int Start, int End)> Words(string text)
    {
        var words = new List<(string, int, int)>();
        var current = new StringBuilder();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark || c == 'ـ' || c == '‌')
            {
                if (start < 0)
                    start = i;
                if (Fold(c) is char folded and not '\0')
                    current.Append(folded);
                continue;
            }
            if (start >= 0)
            {
                if (current.Length > 0)
                    words.Add((current.ToString(), start, i));
                current.Clear();
                start = -1;
            }
        }
        return words;
    }

    /// <summary>A letter as compared ('\0' for none): lower case, Arabic's forms of a letter as one, no vowel marks, Persian's ی and ک as Arabic's.</summary>
    private static char Fold(char c) => c switch
    {
        >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ' or '‌' => '\0',
        _ when char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark => '\0',
        'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
        'ة' => 'ه',
        'ى' or 'ی' => 'ي',
        'ک' => 'ك',
        'ؤ' => 'و',
        'ئ' => 'ي',
        'İ' => 'i',
        _ => char.ToLowerInvariant(RemoveAccent(c)),
    };

    private static char RemoveAccent(char c)
    {
        if (c < 0xC0 || c > 0x24F)
            return c;
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        return decomposed.Length > 0 ? decomposed[0] : c;
    }
}
