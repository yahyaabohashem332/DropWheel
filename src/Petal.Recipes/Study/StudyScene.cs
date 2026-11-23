using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Study;

/// <summary>What the interactive file is for: it sets the model's voice, and which parts of the page there are.</summary>
public enum StudyPurpose
{
    /// <summary>To study from: cards, questions, a test and progress.</summary>
    Study,

    /// <summary>To teach a lesson from: the same, written for a teacher to take into class.</summary>
    Teach,

    /// <summary>To explain an idea to someone who isn't a specialist: no cards or tests.</summary>
    Explain,

    /// <summary>To present to an audience: points that carry, no cards or tests.</summary>
    Present,

    /// <summary>A quick briefing for a busy reader: the essentials only.</summary>
    Brief,

    /// <summary>For children: simply and warmly, with a few easy questions.</summary>
    Children,

    /// <summary>To explore and play with: things to try by hand above all, no tests.</summary>
    Explore,
}

/// <summary>How many hands-on scenes to make from each passage.</summary>
public enum StudyScenes
{
    None,
    Some,
    Many,
}

/// <summary>
/// A hands-on scene: things to mix in a flask and see what happens, quantities to change on sliders and watch a result
/// follow, a system whose parts push one another, situations to choose in, or numbers to guess.
/// </summary>
public enum StudySceneKind
{
    Lab,
    Simulate,
    System,
    Scenario,
    Estimate,
}

/// <summary>Something on the lab's shelf: its name, what it is, and its colour (one of <see cref="StudyScenes"/>' colour words).</summary>
public sealed record SceneThing(string Name, string About, string Color);

/// <summary>What happens when some of the lab's things are put together: what is seen, why, how it looks (an effect and the colour after).</summary>
public sealed record SceneMix(IReadOnlyList<int> Things, string Seen, string Why, string Effect, string Color);

/// <summary>A quantity to change on a slider.</summary>
public sealed record SceneVar(string Id, string Name, string Unit, double Lowest, double Highest, double Start);

/// <summary>A quantity worked out from the others by its formula.</summary>
public sealed record SceneResult(string Id, string Name, string Unit, string Formula);

/// <summary>What a range of a quantity means: "when <see cref="Id"/> <see cref="Op"/> <see cref="Value"/>, <see cref="Says"/>".</summary>
public sealed record SceneLevel(string Id, string Op, double Value, string Says);

/// <summary>One part of a system acting on another: raising it or lowering it, and why.</summary>
public sealed record SceneLink(int From, bool Raises, int To, string Why);

/// <summary>A choice in a situation: how good it is (1 good, 0 partly, −1 bad) and what follows.</summary>
public sealed record SceneChoice(string Text, int Grade, string Outcome);

/// <summary>A situation and the choices in it.</summary>
public sealed record SceneStage(string Situation, IReadOnlyList<SceneChoice> Choices);

/// <summary>A number to guess: the question, the number, its unit, the range offered and why it is so.</summary>
public sealed record SceneGuess(string Question, double Answer, string Unit, double Lowest, double Highest, string Why);

/// <summary>A hands-on scene of a part, with what it is about and where in the file it comes from; the lists of its kind filled.</summary>
public sealed record StudyScene(StudySceneKind Kind, string Title, string Where)
{
    public IReadOnlyList<SceneThing> Things { get; init; } = [];

    public IReadOnlyList<SceneMix> Mixes { get; init; } = [];

    public IReadOnlyList<SceneVar> Vars { get; init; } = [];

    public IReadOnlyList<SceneResult> Results { get; init; } = [];

    public IReadOnlyList<SceneLevel> Levels { get; init; } = [];

    /// <summary>System: its parts, by the names the links use.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    public IReadOnlyList<SceneLink> Links { get; init; } = [];

    public IReadOnlyList<SceneStage> Stages { get; init; } = [];

    public IReadOnlyList<SceneGuess> Guesses { get; init; } = [];

    /// <summary>Every word of it, to tell its language by.</summary>
    public string Said => string.Join(" ", new[] { Title }
        .Concat(Things.SelectMany(t => new[] { t.Name, t.About }))
        .Concat(Mixes.SelectMany(m => new[] { m.Seen, m.Why }))
        .Concat(Vars.Select(v => v.Name)).Concat(Results.Select(r => r.Name)).Concat(Levels.Select(l => l.Says))
        .Concat(Parts).Concat(Links.Select(l => l.Why))
        .Concat(Stages.SelectMany(s => s.Choices.SelectMany(c => new[] { c.Text, c.Outcome }).Prepend(s.Situation)))
        .Concat(Guesses.SelectMany(g => new[] { g.Question, g.Why })));
}

/// <summary>
/// What a file is for, as the model is told it: who the reader is and how to write for them. Study is told nothing more,
/// so what was made before for it stays as it was.
/// </summary>
public static class StudyPurposes
{
    /// <summary>Whether the page has its study parts: cards, a test, parts to mark as studied and progress.</summary>
    public static bool Studies(StudyPurpose purpose) => purpose is StudyPurpose.Study or StudyPurpose.Teach or StudyPurpose.Children;

    /// <summary>Whether the model is asked for cards and questions at all.</summary>
    public static bool Asks(StudyPurpose purpose) => Studies(purpose);

    /// <summary>The model's part and the reader, for the explanation; "" for Study.</summary>
    internal static string Role(StudyPurpose purpose) => purpose switch
    {
        StudyPurpose.Teach => "You help a teacher prepare a lesson. The user gives you a passage from a book, a course or a lecture; turn it into material the teacher can teach from in class: clear explanations they can say aloud, an example to work through on the board, and questions to ask the class.",
        StudyPurpose.Explain => "You are good at explaining things to people who aren't specialists. The user gives you a passage from a book, a document, a course or a lecture; explain what it says to someone meeting the idea for the first time, warmly and plainly, with comparisons from everyday life where they help.",
        StudyPurpose.Present => "You help someone present a subject to an audience. The user gives you a passage from a book, a document, a course or a lecture; turn it into what a presenter needs: points that carry on their own, a clear thread from one to the next, and an example the audience will remember.",
        StudyPurpose.Brief => "You brief busy people. The user gives you a passage from a book, a document, a report or a lecture; give the essentials quickly: what matters, what follows from it, and nothing more.",
        StudyPurpose.Children => "You explain things to children of about 8 to 12. The user gives you a passage from a book, a course or a lecture; explain it to them in short sentences, warmly and simply, with examples from a child's own life, but never wrongly: simple, not false.",
        StudyPurpose.Explore => "You make subjects fun to explore. The user gives you a passage from a book, a document, a course or a lecture; explain it to a curious reader who wants to understand how things work, inviting them to wonder what would happen if something changed.",
        _ => "",
    };

    /// <summary>Who the reader of the page is, for the diagrams and the scenes.</summary>
    internal static string Reader(StudyPurpose purpose) => purpose switch
    {
        StudyPurpose.Teach => "The page is for a teacher to use with a class.",
        StudyPurpose.Explain => "The page explains an idea to someone who isn't a specialist.",
        StudyPurpose.Present => "The page is shown to an audience by a presenter.",
        StudyPurpose.Brief => "The page briefs a busy reader.",
        StudyPurpose.Children => "The page is for children of about 8 to 12: keep the words short and simple.",
        StudyPurpose.Explore => "The page is for a curious reader who wants to play with the ideas.",
        _ => "The page is for a student meeting the subject for the first time.",
    };
}

/// <summary>
/// The hands-on scenes of a passage, asked for apart from its explanation and its activities: what the model is told,
/// and its reply read carefully. The page draws and moves each scene from these lines; nothing the model writes is run.
/// A formula is read by <see cref="SceneFormula"/> and kept only when it works out over its sliders.
/// </summary>
public static partial class StudySceneReader
{
    /// <summary>The colours a thing or a mix may have.</summary>
    public static readonly string[] Colors = ["clear", "white", "grey", "black", "red", "orange", "yellow", "green", "blue", "purple", "pink", "brown"];

    /// <summary>How a mix looks as it happens.</summary>
    public static readonly string[] Effects = ["bubbles", "fizz", "glow", "smoke", "solid", "heat", "cold", "spark", "color", "none"];

    internal static (int Least, int Most) Range(StudyScenes amount) => amount switch
    {
        StudyScenes.Many => (1, 3),
        StudyScenes.Some => (1, 1),
        _ => (0, 0),
    };

    internal static string Instruction(string into, string from, StudyScenes amount, string request, StudyPurpose purpose)
    {
        var (least, most) = Range(amount);
        var language = Translator.EnglishName(into);
        var translating = from != into && Translator.Languages.Any(l => l.Code == from)
            ? $" (the passage is in {Translator.EnglishName(from)})"
            : "";
        var sb = new StringBuilder();
        sb.Append("You design hands-on scenes for an interactive page that opens in a browser: the reader mixes, moves, changes and chooses things by hand and sees at once what happens, so that they understand what the passage teaches by playing with it. The page draws and animates each scene from your lines; you write no code.\n");
        sb.Append(StudyPurposes.Reader(purpose)).Append('\n');
        sb.Append($"Write every name, sentence and explanation in {language}{translating}. The headings, the words in capitals below (THING, MIX, VAR, RESULT, SITUATION, RAISES, LOWERS, GOOD, OK, BAD), the effects, the colours and the ids stay in English exactly as shown.\n");
        sb.Append("Keep to the passage: what happens in a scene, every result, rule, number and reason, must be what the passage says or follows plainly from it; add no facts of your own.\n");
        sb.Append(StudyFile.Requested(request, into));
        sb.Append(least == most
            ? "Choose the one kind below that suits this passage best and give one scene of it. "
            : $"Choose the kinds below that suit this passage best and give {least} to {most} scenes, each kind once at most: all of a kind's lines go under its one heading. ");
        sb.Append("Use a kind only when the passage really has that shape. If none fits, reply with the one word NONE.\n\n");
        sb.Append("Reply with the scenes only. Each begins with its heading: the kind in capitals, a colon, and a short title of your own that says what the scene is about. Write what is in <angle brackets> in your own words, without the brackets.\n\n");
        sb.Append("LAB: <title>\nTHING: <name> = <what it is, in a few words> | <colour>\nMIX: <name> + <name> | <what the reader sees happen> | <why it happens> | <effect> | <colour after>\n");
        sb.Append("For real substances, ingredients or materials that the passage says react or change when put together; never for quantities or ideas. First 3 to 6 THINGs, each once; then 2 to 5 MIXes, each of two or three THINGs named exactly as in their THING line. An effect is one of: bubbles, fizz, glow, smoke, solid, heat, cold, spark, color, none. A colour is the one the thing really has (most liquids are clear), one of: clear, white, grey, black, red, orange, yellow, green, blue, purple, pink, brown.\n\n");
        sb.Append("SIMULATE: <title>\nVAR: <id> | <name> | <unit> | <lowest> | <highest> | <start>\nRESULT: <id> | <name> | <unit> | <formula>\n- <id> > <number> = <what that means>\n");
        sb.Append("Only when the passage gives a rule or a formula that links quantities. 1 to 3 VARs, the quantities the reader changes, then 1 or 2 RESULTs worked out from them. Each id is one short English word in small letters, a different one for each VAR and RESULT. Lowest, highest and start are plain numbers, and start is a usual value between them, not 0. For a rule a = b × c: VAR b, VAR c, then RESULT a with the formula b * c. A formula uses only VAR ids, numbers, + - * / ^ ( ) and sqrt, abs, min, max, sin, cos, log, ln, exp, pi, and it is the passage's own rule. Then 0 to 4 lines on what a range of a RESULT means, with >, <, >= or <=.\n\n");
        sb.Append("SYSTEM: <title>\n- <part> | RAISES | <another part> | <why, in one short sentence>\n- <part> | LOWERS | <another part> | <why>\n");
        sb.Append("When things act on one another: a cycle in nature, a body, a market, a machine, a society. 3 to 8 links between 3 to 7 parts, each part named the same way every time. RAISES: more of the first makes more of the second; LOWERS: more of the first makes less of the second.\n\n");
        sb.Append("SCENARIO: <title>\nSITUATION: <what is happening, in one or two sentences>\n- <a choice> | GOOD | <what happens then, and why>\n- <another choice> | BAD | <what happens then, and why>\n");
        sb.Append("When the passage teaches what to do, what to choose or what follows from a decision. 2 to 4 SITUATIONs under the one heading, each with 2 to 4 choices marked GOOD, OK or BAD, exactly one GOOD: the one the passage says is right.\n\n");
        sb.Append("ESTIMATE: <title>\n- <a question whose answer is a number> | <the number alone> | <its unit> | <lowest to offer> | <highest to offer> | <why, in one sentence>\n");
        sb.Append("When the passage gives numbers worth remembering. 2 to 4 questions, every number from the passage, written as a plain number.");
        // Said again last: a small model given a passage in another language writes the scenes in the form's English.
        sb.Append($"\n\nWrite all of it in {language}: the titles, the names of the THINGs, the VARs, the RESULTs and the parts, the choices and every sentence{(translating.Length > 0 ? ", putting the passage's words into " + language : "")}. Only the words in capitals, the effects, the colours and the ids stay in English.");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[\s#*_>]*(LAB|SIMULATE|SIMULATION|SYSTEM|SCENARIO|ESTIMATE)\b[\s*_]*:?[\s*_]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^[\s#*_>•-]*(THING|MIX|VAR|RESULT|SITUATION)\b[\s*_]*:[\s*_]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^\s*(?:[-•*·▪–]|\(?\d+[.)]|\(?[٠-٩۰-۹]+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>
    /// The scenes of a reply; none when it has none in the form asked for. With the <paramref name="passage"/> they were
    /// made from, a formula is kept only when its numbers are the passage's: a small model makes up "× 2" for a rule the
    /// passage never gives.
    /// </summary>
    public static IReadOnlyList<StudyScene> Read(string reply, string where, string? passage = null)
    {
        var sections = new List<(string Kind, string Title, List<(string Label, string Text)> Lines)>();
        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var heading = HeadingLine().Match(raw);
            if (heading.Success && heading.Groups[1].Value == heading.Groups[1].Value.ToUpperInvariant())
            {
                var kind = heading.Groups[1].Value.ToUpperInvariant() == "SIMULATION" ? "SIMULATE" : heading.Groups[1].Value.ToUpperInvariant();
                var title = Titled(heading.Groups[2].Value);
                // A kind given again (a small model writes each situation under its own heading) carries on the first.
                int again = sections.FindIndex(s => s.Kind == kind);
                if (again >= 0)
                {
                    sections.Add(sections[again]);
                    sections.RemoveAt(again);
                    if (sections[^1].Title.Length == 0 && title.Length > 0)
                        sections[^1] = (kind, title, sections[^1].Lines);
                }
                else
                    sections.Add((kind, title, []));
                continue;
            }
            if (sections.Count == 0 || raw.Trim() is not { Length: > 0 } line || Regex.IsMatch(line, @"^\(.*\)$"))
                continue;
            var label = LabelLine().Match(line);
            if (label.Success)
                sections[^1].Lines.Add((label.Groups[1].Value.ToUpperInvariant(), label.Groups[2].Value.Trim()));
            else if (Bullet().IsMatch(line))
                sections[^1].Lines.Add(("-", Bullet().Replace(line, "").Trim()));
            else if (line.Contains('|') || sections[^1].Lines.Count == 0)
                // Cells without a bullet are an item all the same.
                sections[^1].Lines.Add(("-", line.Trim()));
            else if (sections[^1].Lines.Count > 0)
            {
                // A line that carries on the one before.
                var (l, t) = sections[^1].Lines[^1];
                sections[^1].Lines[^1] = (l, t + " " + line.Trim());
            }
        }
        var scenes = new List<StudyScene>();
        foreach (var (kind, title, lines) in sections)
        {
            if (scenes.Any(s => s.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)))
                continue;
            var scene = kind switch
            {
                "LAB" => Lab(title, lines, where),
                "SIMULATE" => Simulate(title, lines, where, passage),
                "SYSTEM" => System(title, lines, where),
                "SCENARIO" => Scenario(title, lines, where),
                _ => Estimate(title, lines, where),
            };
            if (scene is not null)
                scenes.Add(scene);
        }
        return scenes;
    }

    private static string Key(string text) => new(StudyRequest.Fold(text).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>What the instruction showed in place of a title, which a small model copies: no title at all.</summary>
    private static readonly string[] Placeholders =
        ["title", "what is put together", "what it shows", "what the system is", "the situation", "what to guess", "a short title", "a short title of your own"];

    private static string Titled(string text)
    {
        var title = StudyFile.Plainly(text.Trim().Trim('<', '>', '[', ']'));
        return Placeholders.Any(p => string.Equals(p, title.TrimEnd('.', ':'), StringComparison.OrdinalIgnoreCase)) || title.Length > 90 ? "" : title;
    }

    private static string[] Cells(string line) => line.Split(['|', '｜'], StringSplitOptions.TrimEntries).Select(c => StudyFile.Plainly(c)).ToArray();

    internal static string ColorOf(string word)
    {
        var w = word.Trim().ToLowerInvariant().Trim('.', '*', ' ');
        w = w switch { "gray" => "grey", "colorless" or "colourless" or "transparent" => "clear", "none" or "same" or "no change" => "", "violet" => "purple", "silver" => "grey", _ => w };
        return Colors.Contains(w) ? w : Colors.FirstOrDefault(c => w.Contains(c, StringComparison.Ordinal)) ?? "";
    }

    internal static string EffectOf(string word)
    {
        var w = word.Trim().ToLowerInvariant().Trim('.', '*', ' ');
        w = w switch { "gas" or "bubble" => "bubbles", "fizzing" or "foam" => "fizz", "light" or "flame" or "fire" => "glow", "precipitate" or "solid forms" => "solid", "hot" or "warm" => "heat", "cool" => "cold", "sparks" or "explosion" or "bang" => "spark", "colour" or "colour change" or "color change" => "color", _ => w };
        return Effects.Contains(w) ? w : Effects.FirstOrDefault(e => w.Contains(e, StringComparison.Ordinal)) ?? "";
    }

    /// <summary>A number as the model wrote it: Arabic or Persian digits, a decimal comma alone, a thin space or a word after it.</summary>
    internal static double? Number(string text)
    {
        var digits = new StringBuilder();
        foreach (var c in text.Trim())
            digits.Append(c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c is '٫' ? '.' : c is '−' ? '-' : c);
        var s = digits.ToString().Replace(" ", "").Replace(" ", "").Replace(" ", "");
        var m = Regex.Match(s, @"^[~≈]?([-+]?\d+(?:[.,]\d+)*(?:[eE][-+]?\d+)?)");
        if (!m.Success)
            return null;
        var n = m.Groups[1].Value;
        // "1,000" and "1,000,000" are thousands; "2,5" is two and a half.
        if (Regex.IsMatch(n, @"^[-+]?\d{1,3}(,\d{3})+(\.\d+)?$"))
            n = n.Replace(",", "");
        else if (n.Count(c => c == ',') == 1 && !n.Contains('.'))
            n = n.Replace(',', '.');
        return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static StudyScene? Lab(string title, List<(string Label, string Text)> lines, string where)
    {
        var things = new List<SceneThing>();
        foreach (var (label, text) in lines.Where(l => l.Label == "THING"))
        {
            var cells = Cells(text);
            var (name, about) = StudyFile.Pair(cells[0]);
            if (name.Length == 0)
                name = cells[0];
            name = StudyFile.Plainly(name);
            if (name.Length is 0 or > 50 || things.Any(t => Key(t.Name) == Key(name)))
                continue;
            things.Add(new SceneThing(name, about.Length <= 160 ? about : "", cells.Length > 1 ? ColorOf(cells[^1]) : ""));
        }
        int? Find(string name)
        {
            var key = Key(name);
            if (key.Length == 0)
                return null;
            int exact = things.FindIndex(t => Key(t.Name) == key);
            if (exact >= 0)
                return exact;
            var near = things.Select((t, i) => (Key: Key(t.Name), i)).Where(t => t.Key.Length >= 3 && (t.Key.Contains(key, StringComparison.Ordinal) || key.Contains(t.Key, StringComparison.Ordinal))).ToList();
            return near.Count == 1 ? near[0].i : null;
        }
        var mixes = new List<SceneMix>();
        foreach (var (label, text) in lines.Where(l => l.Label == "MIX"))
        {
            var cells = Cells(text);
            if (cells.Length < 3)
                continue;
            var named = Regex.Split(cells[0], @"\s*[+＋]\s*").Select(n => StudyFile.Plainly(n)).Where(n => n.Length > 0).ToList();
            var seen = cells[1];
            var why = cells[2];
            if (named.Count is < 2 or > 3 || named.Any(n => n.Length > 50) || seen.Length is < 3 or > 300 || why.Length > 400)
                continue;
            // A thing a mix names that has no line of its own goes on the shelf all the same.
            var found = new List<int>();
            foreach (var name in named)
            {
                if (Find(name) is not { } i)
                {
                    if (things.Count >= 8)
                        break;
                    things.Add(new SceneThing(name, "", ""));
                    i = things.Count - 1;
                }
                found.Add(i);
            }
            var set = found.Distinct().OrderBy(i => i).ToList();
            if (set.Count != named.Count || mixes.Any(m => m.Things.SequenceEqual(set)))
                continue;
            mixes.Add(new SceneMix(set, seen, why, cells.Length > 3 ? EffectOf(cells[3]) is { Length: > 0 } e ? e : "none" : "none", cells.Length > 4 ? ColorOf(cells[4]) : ""));
        }
        if (mixes.Count is 0 or > 6 || things.Count is < 2 or > 8)
            return null;
        return new StudyScene(StudySceneKind.Lab, title, where) { Things = things, Mixes = mixes };
    }

    [GeneratedRegex(@"^(?<id>[\p{L}_][\p{L}\p{N}_]*)\s*(?<op><=|>=|≤|≥|<|>)\s*(?<n>[-+−]?[\d٠-٩۰-۹][\d٠-٩۰-۹.,]*)\s*[\p{L}%/°²³µ]*\s*(?:=|:|—|–|-|→)\s*(?<s>.+)$")]
    private static partial Regex LevelLine();

    private static StudyScene? Simulate(string title, List<(string Label, string Text)> lines, string where, string? passage)
    {
        var vars = new List<SceneVar>();
        foreach (var (_, text) in lines.Where(l => l.Label == "VAR"))
        {
            var c = Cells(text);
            if (c.Length < 6 || SceneFormula.Name(c[0]) is not { } id || vars.Any(v => v.Id == id))
                continue;
            if (Number(c[3]) is not { } low || Number(c[4]) is not { } high || !(high > low))
                continue;
            // A start that isn't a number is the middle.
            var start = Number(c[5]) ?? (low + high) / 2;
            var unit = c[2] is "-" or "—" or "none" ? "" : c[2];
            if (c[1].Length is 0 or > 60 || unit.Length > 16)
                continue;
            vars.Add(new SceneVar(id, c[1], unit, low, high, Math.Clamp(start, low, high)));
        }
        if (vars.Count is 0 or > 4)
            return null;
        var results = new List<SceneResult>();
        foreach (var (_, text) in lines.Where(l => l.Label == "RESULT"))
        {
            var c = Cells(text);
            if (c.Length < 4 || SceneFormula.Name(c[0]) is not { } id || vars.Any(v => v.Id == id) || results.Any(r => r.Id == id))
                continue;
            // The formula as written: what tidies a sentence would take its signs away.
            var formula = SceneFormula.Tidy(text.Split(['|', '｜']).Skip(3).FirstOrDefault() ?? "");
            var known = vars.Select(v => v.Id).Concat(results.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
            if (SceneFormula.Parse(formula, known) is not { } parsed || !Works(parsed, vars, results) || passage is not null && !FromPassage(formula, passage))
                continue;
            var unit = c[2] is "-" or "—" or "none" ? "" : c[2];
            if (c[1].Length is 0 or > 60 || unit.Length > 16)
                continue;
            results.Add(new SceneResult(id, c[1], unit, formula));
        }
        if (results.Count == 0)
            return null;
        // Only the sliders a result follows.
        var ids = vars.Select(v => v.Id).Concat(results.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        var used = results.SelectMany(r => SceneFormula.Parse(r.Formula, ids)!.Names).ToHashSet(StringComparer.Ordinal);
        vars = vars.Where(v => used.Contains(v.Id)).ToList();
        if (vars.Count == 0)
            return null;
        var levels = new List<SceneLevel>();
        foreach (var (label, text) in lines.Where(l => l.Label == "-"))
        {
            var m = LevelLine().Match(text);
            if (!m.Success || Number(m.Groups["n"].Value) is not { } n)
                continue;
            var id = m.Groups["id"].Value.ToLowerInvariant();
            if (!results.Any(r => r.Id == id) && !vars.Any(v => v.Id == id))
                continue;
            var op = m.Groups["op"].Value switch { "≤" => "<=", "≥" => ">=", var o => o };
            var says = StudyFile.Plainly(m.Groups["s"].Value);
            if (says.Length is > 2 and <= 200 && levels.Count < 6)
                levels.Add(new SceneLevel(id, op, n, says));
        }
        return new StudyScene(StudySceneKind.Simulate, title, where) { Vars = vars, Results = results, Levels = levels };
    }

    /// <summary>
    /// Whether every number in a formula is one the passage gives, or 0 and 1, or a square or a cube's 2 and 3. A formula
    /// that is only a slider's name isn't a rule either.
    /// </summary>
    internal static bool FromPassage(string formula, string passage)
    {
        if (Regex.IsMatch(formula.Trim(), @"^[\p{L}_][\p{L}\p{N}_]*$"))
            return false;
        var given = new HashSet<double>();
        foreach (Match m in Regex.Matches(passage, @"[\d٠-٩۰-۹]+(?:[.,٫][\d٠-٩۰-۹]+)?"))
            if (Number(m.Value) is { } n)
                given.Add(n);
        foreach (Match m in Regex.Matches(formula, @"(\^?)\s*(\d*\.?\d+)"))
        {
            var n = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            bool power = m.Groups[1].Value == "^" && n is 2 or 3;
            if (!(n is 0 or 1 || power || given.Contains(n)))
                return false;
        }
        return true;
    }

    /// <summary>Whether a result can be worked out at the sliders' start, and changes as some slider moves.</summary>
    private static bool Works(SceneFormula formula, List<SceneVar> vars, List<SceneResult> before)
    {
        Dictionary<string, double> At(Func<SceneVar, double> value)
        {
            var values = vars.ToDictionary(v => v.Id, value, StringComparer.Ordinal);
            foreach (var r in before)
                values[r.Id] = SceneFormula.Parse(r.Formula, values.Keys.ToHashSet())!.Value(values);
            return values;
        }
        var start = formula.Value(At(v => v.Start));
        if (!double.IsFinite(start))
            return false;
        foreach (var moved in vars.Where(v => formula.Names.Contains(v.Id) || before.Any(r => formula.Names.Contains(r.Id))))
            foreach (var end in new[] { moved.Lowest, moved.Highest, (moved.Lowest + moved.Highest) / 2 })
            {
                var other = formula.Value(At(v => v == moved ? end : v.Start));
                if (double.IsFinite(other) && Math.Abs(other - start) > 1e-9 * Math.Max(1, Math.Abs(start)))
                    return true;
            }
        return false;
    }

    [GeneratedRegex(@"^(?<a>.+?)\s+(?<v>RAISES|LOWERS|INCREASES|DECREASES|REDUCES)\s+(?<b>.+?)(?:\s*[:=|—–]\s*(?<w>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkWords();

    private static bool? Raises(string word)
    {
        var w = StudyRequest.Fold(word.Trim().Trim('*', '.'));
        if (w is "raises" or "raise" or "increases" or "increase" or "up" or "+" or "boosts" or "يزيد" or "يرفع" or "يزيد من" or "artirir" or "افزایش" or "بڑھاتا" or "menaikkan" or "augmente" or "aumenta")
            return true;
        if (w is "lowers" or "lower" or "decreases" or "decrease" or "reduces" or "down" or "-" or "−" or "يقلل" or "يخفض" or "ينقص" or "azaltir" or "کاهش" or "گھٹاتا" or "menurunkan" or "diminue" or "reduce" or "disminuye")
            return false;
        return w.StartsWith("rais", StringComparison.Ordinal) || w.StartsWith("increas", StringComparison.Ordinal) ? true
            : w.StartsWith("lower", StringComparison.Ordinal) || w.StartsWith("decreas", StringComparison.Ordinal) || w.StartsWith("reduc", StringComparison.Ordinal) ? false : null;
    }

    private static StudyScene? System(string title, List<(string Label, string Text)> lines, string where)
    {
        var parts = new List<string>();
        int PartOf(string name)
        {
            var key = Key(name);
            int i = parts.FindIndex(p => Key(p) == key);
            if (i >= 0)
                return i;
            parts.Add(name);
            return parts.Count - 1;
        }
        var links = new List<SceneLink>();
        foreach (var (label, text) in lines.Where(l => l.Label == "-"))
        {
            string a, b, why;
            bool? up;
            var c = Cells(text);
            if (c.Length >= 3)
                (a, up, b, why) = (c[0], Raises(c[1]), c[2], c.Length > 3 ? string.Join(" ", c.Skip(3)) : "");
            else if (LinkWords().Match(StudyFile.Plainly(text)) is { Success: true } m)
                (a, up, b, why) = (m.Groups["a"].Value, Raises(m.Groups["v"].Value), m.Groups["b"].Value, m.Groups["w"].Value);
            else
                continue;
            a = StudyFile.Plainly(a);
            b = StudyFile.Plainly(b);
            if (up is not { } raises || a.Length is 0 or > 50 || b.Length is 0 or > 50 || Key(a) == Key(b) || Key(a).Length == 0 || Key(b).Length == 0 || why.Length > 300)
                continue;
            int from = PartOf(a), to = PartOf(b);
            if (links.Any(l => l.From == from && l.To == to))
                continue;
            links.Add(new SceneLink(from, raises, to, StudyFile.Plainly(why)));
        }
        if (links.Count is < 2 or > 12 || parts.Count is < 3 or > 8)
            return null;
        return new StudyScene(StudySceneKind.System, title, where) { Parts = parts, Links = links };
    }

    private static int? Grade(string word)
    {
        var w = StudyRequest.Fold(word.Trim().Trim('*', '.', '(', ')'));
        return w switch
        {
            "good" or "best" or "right" or "correct" or "جيد" or "صحيح" or "صح" or "iyi" or "dogru" or "خوب" or "درست" or "اچھا" or "baik" or "benar" or "bon" or "bien" or "bueno" or "correcto" => 1,
            "ok" or "okay" or "partly" or "fair" or "مقبول" or "وسط" or "orta" or "متوسط" or "cukup" or "moyen" or "regular" => 0,
            "bad" or "wrong" or "poor" or "سيئ" or "سيء" or "خطا" or "kotu" or "yanlis" or "بد" or "غلط" or "برا" or "buruk" or "salah" or "mauvais" or "malo" or "incorrecto" => -1,
            _ => null,
        };
    }

    private static StudyScene? Scenario(string title, List<(string Label, string Text)> lines, string where)
    {
        var stages = new List<SceneStage>();
        string? situation = null;
        var choices = new List<SceneChoice>();
        void Close()
        {
            if (situation is { Length: > 5 and <= 500 } s && choices.Count is >= 2 and <= 5 && choices.Any(c => c.Grade == 1))
                stages.Add(new SceneStage(s, choices.ToList()));
            choices.Clear();
        }
        foreach (var (label, text) in lines)
        {
            if (label == "SITUATION")
            {
                if (situation is not null)
                    Close();
                situation = StudyFile.Plainly(text);
                continue;
            }
            if (label != "-" || situation is null)
                continue;
            var c = Cells(text);
            if (c.Length < 3 || Grade(c[1]) is not { } grade)
                continue;
            var outcome = string.Join(" ", c.Skip(2));
            if (c[0].Length is < 2 or > 200 || outcome.Length is < 3 or > 400 || choices.Any(x => Key(x.Text) == Key(c[0])))
                continue;
            choices.Add(new SceneChoice(c[0], grade, outcome));
        }
        if (situation is not null)
            Close();
        if (stages.Count is 0 or > 5)
            return null;
        return new StudyScene(StudySceneKind.Scenario, title, where) { Stages = stages };
    }

    private static StudyScene? Estimate(string title, List<(string Label, string Text)> lines, string where)
    {
        var guesses = new List<SceneGuess>();
        foreach (var (label, text) in lines.Where(l => l.Label == "-"))
        {
            var c = Cells(text);
            if (c.Length < 3 || Number(c[1]) is not { } answer || c[0].Length is < 6 or > 220)
                continue;
            // The number alone, or with its unit: "8 minutes and 20 seconds" isn't one number.
            var after = Regex.Replace(c[1].Trim(), @"^[~≈]?\s*[-+−]?[\d٠-٩۰-۹][\d٠-٩۰-۹.,٫  ]*", "").Trim();
            if (Regex.IsMatch(after, @"[\d٠-٩۰-۹]") || after.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2)
                continue;
            var unit = after.Length > 0 ? after : c[2] is "-" or "—" or "none" ? "" : c[2];
            double? low = c.Length > 3 ? Number(c[3]) : null, high = c.Length > 4 ? Number(c[4]) : null;
            // A range that doesn't hold the answer, or none, is made around it.
            if (low is not { } lo || high is not { } hi || !(lo < answer && answer < hi))
            {
                (lo, hi) = answer > 0 ? (0.0, answer * 2.5) : answer < 0 ? (answer * 2.5, 0.0) : (-10.0, 10.0);
                if (answer > 0 && Math.Abs(answer) >= 1000)
                    lo = answer / 4;
            }
            var why = c.Length > 5 ? string.Join(" ", c.Skip(5)) : "";
            if (unit.Length > 20 || guesses.Any(g => Key(g.Question) == Key(c[0])))
                continue;
            guesses.Add(new SceneGuess(c[0], answer, unit, lo, hi, why.Length <= 300 ? why : ""));
        }
        if (guesses.Count is 0 or > 5)
            return null;
        return new StudyScene(StudySceneKind.Estimate, title, where) { Guesses = guesses };
    }
}

/// <summary>
/// A formula of a scene, read and worked out here as the page's script works it out: numbers, names, + − × ÷ ^,
/// brackets and a few functions, nothing else. A formula that names anything else isn't one.
/// </summary>
public sealed class SceneFormula
{
    private readonly Func<IReadOnlyDictionary<string, double>, double> _value;

    private SceneFormula(Func<IReadOnlyDictionary<string, double>, double> value, IReadOnlySet<string> names)
    {
        _value = value;
        Names = names;
    }

    /// <summary>The names the formula uses.</summary>
    public IReadOnlySet<string> Names { get; }

    public double Value(IReadOnlyDictionary<string, double> values)
    {
        try
        {
            return _value(values);
        }
        catch (KeyNotFoundException)
        {
            return double.NaN;
        }
    }

    public static readonly string[] Functions = ["sqrt", "abs", "min", "max", "sin", "cos", "tan", "log", "ln", "exp", "round", "pow"];

    /// <summary>An id as the model wrote it, in small letters; null when it isn't one.</summary>
    public static string? Name(string text)
    {
        var id = text.Trim().Trim('*', '`').ToLowerInvariant();
        return Regex.IsMatch(id, @"^[\p{L}_][\p{L}\p{N}_]{0,23}$") && !Functions.Contains(id) && id is not ("pi" or "e") ? id : null;
    }

    /// <summary>A formula with its signs as the script reads them: × and · are *, ÷ is /, − is -, and "id =" before it goes.</summary>
    public static string Tidy(string formula)
    {
        var f = formula.Trim().Trim('`', '*').Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-').Replace("**", "^").Replace('²', '\u0001').Replace('³', '\u0002');
        f = f.Replace("\u0001", "^2").Replace("\u0002", "^3");
        f = Regex.Replace(f, @"^\s*[\p{L}_][\p{L}\p{N}_]*\s*=\s*", "");
        return Regex.Replace(f, @"\s+", " ").Trim();
    }

    /// <summary>The formula read, when it is one and names only <paramref name="known"/>, pi and e.</summary>
    public static SceneFormula? Parse(string formula, IReadOnlySet<string> known)
    {
        var tokens = new List<string>();
        int i = 0;
        var text = formula.ToLowerInvariant();
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                var m = Regex.Match(text[i..], @"^\d*\.?\d+(?:e[-+]?\d+)?|^\d+\.");
                tokens.Add(m.Value);
                i += m.Length;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var m = Regex.Match(text[i..], @"^[\p{L}_][\p{L}\p{N}_]*");
                tokens.Add(m.Value);
                i += m.Length;
                continue;
            }
            if ("+-*/^(),".Contains(c))
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }
            return null;
        }
        if (tokens.Count == 0 || tokens.Count > 200)
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        int at = 0;
        Func<IReadOnlyDictionary<string, double>, double>? Expression()
        {
            var left = Term();
            while (left is not null && at < tokens.Count && tokens[at] is "+" or "-")
            {
                var op = tokens[at++];
                var right = Term();
                if (right is null)
                    return null;
                var l = left;
                left = op == "+" ? v => l(v) + right(v) : v => l(v) - right(v);
            }
            return left;
        }
        Func<IReadOnlyDictionary<string, double>, double>? Term()
        {
            var left = Unary();
            while (left is not null && at < tokens.Count && tokens[at] is "*" or "/")
            {
                var op = tokens[at++];
                var right = Unary();
                if (right is null)
                    return null;
                var l = left;
                left = op == "*" ? v => l(v) * right(v) : v => l(v) / right(v);
            }
            return left;
        }
        Func<IReadOnlyDictionary<string, double>, double>? Unary()
        {
            if (at < tokens.Count && tokens[at] is "-" or "+")
            {
                var op = tokens[at++];
                var inner = Unary();
                return inner is null ? null : op == "-" ? v => -inner(v) : inner;
            }
            return Power();
        }
        Func<IReadOnlyDictionary<string, double>, double>? Power()
        {
            var b = Atom();
            if (b is null || at >= tokens.Count || tokens[at] != "^")
                return b;
            at++;
            var e = Unary();
            return e is null ? null : v => Math.Pow(b(v), e(v));
        }
        Func<IReadOnlyDictionary<string, double>, double>? Atom()
        {
            if (at >= tokens.Count)
                return null;
            var t = tokens[at++];
            if (t == "(")
            {
                var inner = Expression();
                if (inner is null || at >= tokens.Count || tokens[at++] != ")")
                    return null;
                return inner;
            }
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return _ => n;
            if (!(char.IsLetter(t[0]) || t[0] == '_'))
                return null;
            if (Functions.Contains(t))
            {
                if (at >= tokens.Count || tokens[at++] != "(")
                    return null;
                var args = new List<Func<IReadOnlyDictionary<string, double>, double>>();
                if (at < tokens.Count && tokens[at] == ")")
                    return null;
                while (true)
                {
                    var a = Expression();
                    if (a is null)
                        return null;
                    args.Add(a);
                    if (at < tokens.Count && tokens[at] == ",")
                    {
                        at++;
                        continue;
                    }
                    if (at >= tokens.Count || tokens[at++] != ")")
                        return null;
                    break;
                }
                int count = args.Count;
                bool two = t is "min" or "max" or "pow";
                if (two ? count != 2 && !(t is "min" or "max" && count >= 2) : count != 1)
                    return null;
                var x = args[0];
                return t switch
                {
                    "sqrt" => v => Math.Sqrt(x(v)),
                    "abs" => v => Math.Abs(x(v)),
                    "sin" => v => Math.Sin(x(v)),
                    "cos" => v => Math.Cos(x(v)),
                    "tan" => v => Math.Tan(x(v)),
                    "log" => v => Math.Log10(x(v)),
                    "ln" => v => Math.Log(x(v)),
                    "exp" => v => Math.Exp(x(v)),
                    "round" => v => Math.Round(x(v), MidpointRounding.AwayFromZero),
                    "pow" => v => Math.Pow(x(v), args[1](v)),
                    "min" => v => args.Min(a => a(v)),
                    _ => v => args.Max(a => a(v)),
                };
            }
            if (t == "pi")
                return _ => Math.PI;
            if (t == "e")
                return _ => Math.E;
            if (!known.Contains(t))
                return null;
            names.Add(t);
            return v => v[t];
        }
        var value = Expression();
        return value is null || at != tokens.Count ? null : new SceneFormula(value, names);
    }
}
