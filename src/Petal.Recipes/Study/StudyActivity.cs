using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Study;

/// <summary>A diagram of a part: steps one after another, a cycle, dates, an idea and its branches, or a table that compares.</summary>
public enum StudyVisualKind
{
    Flow,
    Cycle,
    Timeline,
    Map,
    Compare,
}

/// <summary>Something to do with the pieces of a part: put them in order, match them, sort them into groups, fill in blanks, or say true or false.</summary>
public enum StudyActivityKind
{
    Order,
    Match,
    Sort,
    Blanks,
    TrueFalse,
    MapFill,
}

/// <summary>
/// A piece of a diagram or an activity. Flow and order: a step. Timeline: what happened (<see cref="Answer"/> when).
/// Map: a branch, its <see cref="Details"/>. Compare: a feature, its <see cref="Details"/> a cell for each thing compared.
/// Match: a left side, <see cref="Answer"/> its right. Sort: an item, <see cref="Answer"/> its group. Blanks: a sentence
/// with its blanks in [square brackets]. True or false: a statement, <see cref="Answer"/> "true" or "false", and
/// <see cref="Note"/> it put right. Map to complete: a branch's details, <see cref="Answer"/> the branch's name. Flow and
/// cycle steps may have <see cref="Details"/>: what happens in the step, which makes the diagram one to go through step by step.
/// </summary>
public sealed record StudyItem(string Text, string Answer = "")
{
    public string Note { get; init; } = "";

    public IReadOnlyList<string> Details { get; init; } = [];
}

/// <summary>A diagram of a part, with its title and where in the file it is.</summary>
public sealed record StudyVisual(StudyVisualKind Kind, string Title, IReadOnlyList<StudyItem> Items, string Where)
{
    /// <summary>Compare: the things compared, the table's columns after the first.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}

/// <summary>An activity of a part: what it asks, its pieces (see <see cref="StudyItem"/>), and where in the file it is.</summary>
public sealed record StudyActivity(StudyActivityKind Kind, string Prompt, IReadOnlyList<StudyItem> Items, string Where)
{
    /// <summary>Sort: the groups, in the order the model gave them.</summary>
    public IReadOnlyList<string> Groups => Kind == StudyActivityKind.Sort ? Items.Select(i => i.Answer).Distinct(StringComparer.Ordinal).ToList() : [];

    /// <summary>Blanks: a sentence as text and blanks, the blanks at the odd places ("The ", "cell", " is small.").</summary>
    public static IReadOnlyList<string> Pieces(string sentence) => Regex.Split(sentence, @"\[([^\[\]]+)\]");

    /// <summary>An order of <paramref name="count"/> pieces fixed by <paramref name="seed"/>'s words, never the right one when there are two or more.</summary>
    public static int[] Mixed(string seed, int count)
    {
        var order = Enumerable.Range(0, count).ToArray();
        if (count < 2)
            return order;
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        // Shuffled back into its own order: each moved on by one.
        if (order.Select((v, i) => v == i).All(b => b))
            order = order.Skip(1).Append(order[0]).ToArray();
        return order;
    }
}

/// <summary>What the model made a passage into besides its explanation: its diagrams and its activities.</summary>
public sealed record StudyExtras(IReadOnlyList<StudyVisual> Visuals, IReadOnlyList<StudyActivity> Activities)
{
    public static readonly StudyExtras Empty = new([], []);

    public bool IsEmpty => Visuals.Count == 0 && Activities.Count == 0;
}

/// <summary>
/// The diagrams and activities of a passage, asked for apart from its explanation (a small model on this PC holds only a
/// few pages, and its explanation already fills them): what the model is told, and its reply read carefully. Steps and
/// dates are also put in order, a diagram and an activity from the same lines.
/// </summary>
public static partial class StudyExtrasReader
{
    /// <summary>How many sections to ask for: at most, and at least when there is something that fits.</summary>
    internal static (int Least, int Most) Range(StudyActivities amount) => amount switch
    {
        StudyActivities.Many => (3, 5),
        StudyActivities.Some => (1, 2),
        _ => (0, 0),
    };

    internal static string Instruction(string into, string from, StudyActivities amount, string request, StudyPurpose purpose = StudyPurpose.Study)
    {
        var (least, most) = Range(amount);
        var language = Translator.EnglishName(into);
        var translating = from != into && Translator.Languages.Any(l => l.Code == from)
            ? $", though the passage is in {Translator.EnglishName(from)}: keep a technical term's own word in brackets after yours where it helps"
            : "";
        var sb = new StringBuilder();
        sb.Append("You are an experienced teacher who makes study material visual and hands-on. The user gives you a passage from a book, a course or a lecture; a student has just read it explained. Turn what it teaches into diagrams and activities.\n");
        // Study is told nothing more: what was made for it before stays as it was.
        if (purpose != StudyPurpose.Study)
            sb.Append(StudyPurposes.Reader(purpose)).Append('\n');
        sb.Append($"Write everything in {language}{translating}.\n");
        sb.Append("Keep to the passage: every fact, name, number and date must come from it; add none of your own.\n");
        sb.Append(StudyFile.Requested(request, into));
        sb.Append($"Choose the kinds below that suit this passage best and give {least} to {most} sections, each kind at most once. Use a kind only when the passage really has that shape: STEPS for a real sequence, TIMELINE only with dates or times, COMPARE only when the passage compares two or three things. If none fits, reply with the one word NONE.\n\n");
        sb.Append("Reply with the sections only, in exactly this form, with the headings in English as they are here:\n\n");
        sb.Append("STEPS: what the steps are of\n1. a step, in a few words = one short sentence on what happens in it\n2. the next step = what happens in it\n(3 to 7 steps in their true order; write CYCLE: instead of STEPS: when the last step leads back to the first)\n\n");
        sb.Append("TIMELINE: what the dates are of\n- a date or a time = what happened then\n(3 to 7, in the order they happened)\n\n");
        sb.Append("MAP: the central idea, in a few words\n- a branch = a detail; another detail\n(3 to 6 branches)\n\n");
        sb.Append("COMPARE: what is compared\n| feature | first thing | second thing |\n|---|---|---|\n| a feature | how the first is | how the second is |\n(2 or 3 things, 3 to 6 features)\n\n");
        sb.Append("MATCH: what to match\n- a term, a name or a cause = what goes with it\n(3 to 6 pairs; every right side different)\n\n");
        sb.Append("GROUPS: what to sort, and by what\n- a group's name: an item; another item; another item\n(2 to 4 groups of 2 to 5 short items; each item in one group only)\n\n");
        sb.Append("BLANKS: what the sentences are about\n- a sentence with its key word or number in [square brackets]\n(2 to 4 sentences, each with one or two [blanks] of one to three words)\n\n");
        sb.Append("TRUE OR FALSE: what the statements are about\n- a statement = TRUE\n- a statement = FALSE: the statement put right\n(3 to 5 statements, some true and some false)");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[\s#*_>]*(STEPS|CYCLE|TIMELINE|MAP|COMPARE|MATCH|GROUPS|BLANKS|TRUE OR FALSE|TRUE/FALSE|TRUE-FALSE)\b[\s*_]*:?[\s*_]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*(?:[-•*·▪–]|\(?\d+[.)]|\(?[٠-٩۰-۹]+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>The diagrams and activities of a reply; <see cref="StudyExtras.Empty"/> when it has none in the form asked for.</summary>
    public static StudyExtras Read(string reply, string where)
    {
        var sections = new List<(string Kind, string Title, List<string> Lines)>();
        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var match = HeadingLine().Match(raw);
            // A heading is in capitals, as asked; "Map: …" inside a line of another section isn't one.
            if (match.Success && match.Groups[1].Value == match.Groups[1].Value.ToUpperInvariant())
            {
                var kind = match.Groups[1].Value.ToUpperInvariant() is "TRUE/FALSE" or "TRUE-FALSE" ? "TRUE OR FALSE" : match.Groups[1].Value.ToUpperInvariant();
                sections.Add((kind, StudyFile.Plainly(match.Groups[2].Value.Trim()), []));
                continue;
            }
            if (sections.Count > 0 && raw.Trim() is { Length: > 0 } line && !Regex.IsMatch(line, @"^\(.*\)$"))
                sections[^1].Lines.Add(line);
        }
        var visuals = new List<StudyVisual>();
        var activities = new List<StudyActivity>();
        foreach (var (kind, title, lines) in sections)
        {
            // Each kind once: the first the model gave.
            var same = kind == "CYCLE" ? "STEPS" : kind;
            if (visuals.Any(v => Heading(v.Kind) == same) || activities.Any(a => Heading(a.Kind) == same))
                continue;
            switch (kind)
            {
                case "STEPS" or "CYCLE" when Steps(lines) is { } steps:
                    var flow = kind == "CYCLE" ? StudyVisualKind.Cycle : StudyVisualKind.Flow;
                    visuals.Add(new StudyVisual(flow, title, steps, where));
                    activities.Add(new StudyActivity(StudyActivityKind.Order, title, steps.Select(s => new StudyItem(s.Text)).ToList(), where));
                    break;
                case "TIMELINE" when Timeline(lines) is { } events:
                    visuals.Add(new StudyVisual(StudyVisualKind.Timeline, title, events, where));
                    if (events.Count >= 3)
                        activities.Add(new StudyActivity(StudyActivityKind.Order, title, events.Select(e => new StudyItem(e.Text)).ToList(), where));
                    break;
                case "MAP" when title.Length > 0 && Map(lines) is { } branches:
                    visuals.Add(new StudyVisual(StudyVisualKind.Map, title, branches, where));
                    // The same map with its branches' names to put back, each found by what it says.
                    if (MapFill(branches) is { } blanks)
                        activities.Add(new StudyActivity(StudyActivityKind.MapFill, title, blanks, where));
                    break;
                case "COMPARE" when Compare(lines) is var (columns, rows):
                    visuals.Add(new StudyVisual(StudyVisualKind.Compare, title, rows, where) { Columns = columns });
                    break;
                case "MATCH" when Match(lines) is { } pairs:
                    activities.Add(new StudyActivity(StudyActivityKind.Match, title, pairs, where));
                    break;
                case "GROUPS" when Groups(lines) is { } items:
                    activities.Add(new StudyActivity(StudyActivityKind.Sort, title, items, where));
                    break;
                case "BLANKS" when Blanks(lines) is { } sentences:
                    activities.Add(new StudyActivity(StudyActivityKind.Blanks, title, sentences, where));
                    break;
                case "TRUE OR FALSE" when TrueFalse(lines) is { } statements:
                    activities.Add(new StudyActivity(StudyActivityKind.TrueFalse, title, statements, where));
                    break;
            }
        }
        return visuals.Count == 0 && activities.Count == 0 ? StudyExtras.Empty : new StudyExtras(visuals, activities);
    }

    private static string Heading(StudyVisualKind kind) => kind switch
    {
        StudyVisualKind.Flow or StudyVisualKind.Cycle => "STEPS",
        StudyVisualKind.Timeline => "TIMELINE",
        StudyVisualKind.Map => "MAP",
        _ => "COMPARE",
    };

    private static string Heading(StudyActivityKind kind) => kind switch
    {
        StudyActivityKind.Match => "MATCH",
        StudyActivityKind.Sort => "GROUPS",
        StudyActivityKind.Blanks => "BLANKS",
        StudyActivityKind.TrueFalse => "TRUE OR FALSE",
        _ => "",
    };

    /// <summary>The lines of a section without their bullets or numbers, each a piece; a line without one carries on the piece before it.</summary>
    private static List<string> Items(List<string> lines)
    {
        bool bulleted = lines.Any(l => Bullet().IsMatch(l));
        var items = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith('|'))
                continue;
            if (!bulleted || Bullet().IsMatch(line) || items.Count == 0)
                items.Add(StudyFile.Plainly(Bullet().Replace(line, "")));
            else
                items[^1] += " " + StudyFile.Plainly(line);
        }
        return items.Where(i => i.Length > 0).ToList();
    }

    private static bool Distinct(IEnumerable<string> texts)
    {
        var list = texts.Select(Key).ToList();
        return list.Distinct(StringComparer.Ordinal).Count() == list.Count;
    }

    private static string Key(string text) => new(StudyRequest.Fold(text).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// The steps, each with what happens in it when the model said so ("Light is absorbed = chlorophyll takes in the
    /// light"). The step by step needs it for every step: where some have it and some don't, none keep it.
    /// </summary>
    private static List<StudyItem>? Steps(List<string> lines)
    {
        var steps = new List<StudyItem>();
        foreach (var item in Items(lines))
        {
            int cut = item.IndexOf(" = ", StringComparison.Ordinal);
            var step = (cut > 0 ? item[..cut] : item).Trim();
            var happens = cut > 0 ? StudyFile.Plainly(item[(cut + 3)..]).Trim() : "";
            if (step.Length is > 1 and <= 220)
                steps.Add(new StudyItem(step) { Details = happens.Length is > 1 and <= 400 ? [happens] : [] });
        }
        if (steps.Count is < 3 or > 8 || !Distinct(steps.Select(s => s.Text)))
            return null;
        return steps.All(s => s.Details.Count > 0) ? steps : steps.Select(s => s with { Details = [] }).ToList();
    }

    /// <summary>A map to complete: three or more branches, each with something it says, that tell one branch from another.</summary>
    private static List<StudyItem>? MapFill(List<StudyItem> branches)
    {
        var blanks = branches.Where(b => b.Details.Count > 0).Select(b => new StudyItem(string.Join("; ", b.Details), b.Text)).ToList();
        return blanks.Count >= 3 && blanks.Count == branches.Count && Distinct(blanks.Select(b => b.Text)) && Distinct(blanks.Select(b => b.Answer)) ? blanks : null;
    }

    private static List<StudyItem>? Timeline(List<string> lines)
    {
        var events = new List<StudyItem>();
        foreach (var item in Items(lines))
        {
            var (when, what) = StudyFile.Pair(item);
            if (when.Length is > 0 and <= 40 && what.Length > 1)
                events.Add(new StudyItem(what, when));
        }
        return events.Count is >= 3 and <= 8 && Distinct(events.Select(e => e.Text)) ? events : null;
    }

    private static List<StudyItem>? Map(List<string> lines)
    {
        var branches = new List<StudyItem>();
        foreach (var item in Items(lines))
        {
            var (branch, rest) = StudyFile.Pair(item);
            if (branch.Length is 0 or > 70)
                continue;
            var details = rest.Split([';', '؛'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(StudyFile.Plainly).Where(d => d.Length > 0).Take(5).ToList();
            branches.Add(new StudyItem(branch) { Details = details });
        }
        return branches.Count is >= 2 and <= 7 && Distinct(branches.Select(b => b.Text)) ? branches : null;
    }

    private static (List<string> Columns, List<StudyItem> Rows)? Compare(List<string> lines)
    {
        var table = lines.Select(l => l.Trim()).Where(l => l.StartsWith('|') && l.EndsWith('|') && l.Length > 2)
            .Select(l => l[1..^1].Split('|').Select(c => StudyFile.Plainly(c.Trim())).ToList())
            .Where(cells => !cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$")))
            .ToList();
        if (table.Count < 3)
            return null;
        var head = table[0];
        int width = head.Count;
        if (width is < 3 or > 4 || table.Skip(1).Any(r => r.Count != width))
            return null;
        var rows = table.Skip(1).Where(r => r[0].Length > 0).Select(r => new StudyItem(r[0]) { Details = r.Skip(1).ToList() }).ToList();
        return rows.Count is >= 2 and <= 8 && head.Skip(1).All(c => c.Length > 0) ? (head.Skip(1).ToList(), rows) : null;
    }

    private static List<StudyItem>? Match(List<string> lines)
    {
        var pairs = Items(lines).Select(StudyFile.Pair).Where(p => p.Left.Length is > 0 and <= 120 && p.Right.Length is > 0 and <= 160).Select(p => new StudyItem(p.Left, p.Right)).ToList();
        return pairs.Count is >= 3 and <= 7 && Distinct(pairs.Select(p => p.Text)) && Distinct(pairs.Select(p => p.Answer)) ? pairs : null;
    }

    private static List<StudyItem>? Groups(List<string> lines)
    {
        var items = new List<StudyItem>();
        var groups = new List<string>();
        foreach (var line in Items(lines))
        {
            int colon = line.IndexOfAny([':', '=']);
            if (colon <= 0 || colon > 70)
                continue;
            var group = StudyFile.Plainly(line[..colon]);
            var members = line[(colon + 1)..].Split([';', '؛', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(StudyFile.Plainly).Where(m => m.Length is > 0 and <= 80).ToList();
            if (group.Length == 0 || members.Count == 0 || groups.Contains(group))
                continue;
            groups.Add(group);
            items.AddRange(members.Select(m => new StudyItem(m, group)));
        }
        // An item in two groups can't be sorted: it goes from both.
        var twice = items.GroupBy(i => Key(i.Text)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        items = items.Where(i => !twice.Contains(Key(i.Text))).ToList();
        var kept = items.Select(i => i.Answer).Distinct().ToList();
        return kept.Count is >= 2 and <= 4 && items.Count is >= 4 and <= 16 && kept.All(g => items.Count(i => i.Answer == g) >= 1) ? items : null;
    }

    private static List<StudyItem>? Blanks(List<string> lines)
    {
        var sentences = new List<StudyItem>();
        foreach (var item in Items(lines))
        {
            var pieces = StudyActivity.Pieces(item);
            int blanks = pieces.Count / 2;
            bool fair = blanks is >= 1 and <= 2 && Enumerable.Range(0, blanks).All(b => pieces[2 * b + 1].Trim() is { Length: > 0 and <= 40 } w && w.Split(' ').Length <= 4);
            if (fair && item.Length <= 260)
                sentences.Add(new StudyItem(Regex.Replace(item, @"\[\s*([^\[\]]+?)\s*\]", "[$1]")));
        }
        return sentences.Count is >= 1 and <= 5 ? sentences : null;
    }

    [GeneratedRegex(@"^(?<s>.+?)\s*(?:=|—|–|-|:)\s*\**(?<v>TRUE|FALSE|T|F|صح|صحيح|خطأ|خاطئ|خطا|doğru|yanlış|dogru|yanlis|درست|نادرست|غلط|صحیح|benar|salah|vrai|faux|verdadero|falso)\**\b\s*[:.,\-–—]?\s*(?<n>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Verdict();

    private static List<StudyItem>? TrueFalse(List<string> lines)
    {
        var statements = new List<StudyItem>();
        foreach (var item in Items(lines))
        {
            var m = Verdict().Match(item);
            if (!m.Success)
                continue;
            var said = m.Groups["v"].Value.ToLowerInvariant();
            bool right = said is "true" or "t" or "صح" or "صحيح" or "doğru" or "dogru" or "درست" or "صحیح" or "benar" or "vrai" or "verdadero";
            var statement = StudyFile.Plainly(m.Groups["s"].Value);
            if (statement.Length is < 6 or > 260)
                continue;
            statements.Add(new StudyItem(statement, right ? "true" : "false") { Note = right ? "" : StudyFile.Plainly(m.Groups["n"].Value) });
        }
        return statements.Count is >= 2 and <= 6 && Distinct(statements.Select(s => s.Text)) ? statements : null;
    }
}
