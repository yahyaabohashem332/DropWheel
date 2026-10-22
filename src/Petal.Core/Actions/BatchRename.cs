using System.Globalization;
using System.Text;

namespace Petal.Core.Actions;

public enum NameCase
{
    Unchanged,
    Lower,
    Upper,
    /// <summary>Every word starts with a capital.</summary>
    Title,
}

/// <summary>
/// How a selection is renamed. <see cref="Pattern"/> builds each name from tokens:
/// {name} the current name (after find and replace), {n} a running number, {date} the date the
/// photo was taken or else the file was last changed (yyyy-MM-dd), {folder} the parent folder, {content} a
/// name from what the file holds (worked out by the caller; the current name until it is known).
/// </summary>
public sealed record RenameRule
{
    public string Pattern { get; init; } = "{name}";
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool MatchCase { get; init; }
    public NameCase Case { get; init; } = NameCase.Unchanged;
    public int Start { get; init; } = 1;

    /// <summary>Digits for {n}, padded with zeros; 0 uses as many as the largest number needs.</summary>
    public int Digits { get; init; }
}

/// <summary>Why a planned name can't be used.</summary>
public enum RenameProblem
{
    None,
    Empty,
    Duplicate,
    Exists,
}

/// <param name="NewName">The new file or folder name, extension included.</param>
public sealed record RenameItem(string Path, string NewName, RenameProblem Problem)
{
    public string OldName => System.IO.Path.GetFileName(Path);

    public bool Changes => Problem == RenameProblem.None && !string.Equals(NewName, OldName, StringComparison.Ordinal);

    public string NewPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", NewName);
}

public static class BatchRename
{
    /// <summary>Works out every new name, and flags names that clash or already exist.</summary>
    public static IReadOnlyList<RenameItem> Plan(IReadOnlyList<string> paths, RenameRule rule, Func<string, DateTime?>? dateTaken = null, Func<string, string?>? content = null)
    {
        int digits = rule.Digits > 0 ? rule.Digits : Math.Max(1, (rule.Start + paths.Count - 1).ToString(CultureInfo.InvariantCulture).Length);
        var names = new List<string>(paths.Count);
        var empty = new List<bool>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var (name, isEmpty) = NameFor(paths[i], i, rule, digits, dateTaken, content);
            names.Add(name);
            empty.Add(isEmpty);
        }

        var selected = new HashSet<string>(paths.Select(p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
        {
            var key = Path.Combine(Path.GetDirectoryName(paths[i]) ?? "", names[i]);
            targets[key] = targets.GetValueOrDefault(key) + 1;
        }

        var items = new List<RenameItem>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var name = names[i];
            var target = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
            var problem = RenameProblem.None;
            if (empty[i])
                problem = RenameProblem.Empty;
            else if (targets[target] > 1)
                problem = RenameProblem.Duplicate;
            else if (!selected.Contains(Path.GetFullPath(target)) && (File.Exists(target) || Directory.Exists(target)))
                problem = RenameProblem.Exists;
            items.Add(new RenameItem(path, name, problem));
        }
        return items;
    }

    /// <summary>The new name, and whether it lost its name part (only an extension, or nothing, left).</summary>
    private static (string Name, bool Empty) NameFor(string path, int index, RenameRule rule, int digits, Func<string, DateTime?>? dateTaken, Func<string, string?>? content)
    {
        bool folder = Directory.Exists(path);
        var oldName = Path.GetFileName(path.TrimEnd('\\', '/'));
        var stem = folder ? oldName : Path.GetFileNameWithoutExtension(oldName);
        var extension = folder ? "" : Path.GetExtension(oldName);
        bool hadName = stem.Length > 0;

        if (rule.Find.Length > 0)
            stem = ReplaceText(stem, rule.Find, rule.Replace, rule.MatchCase);

        var pattern = string.IsNullOrEmpty(rule.Pattern) ? "{name}" : rule.Pattern;
        var name = new StringBuilder(pattern)
            .Replace("{name}", stem)
            .Replace("{n}", (rule.Start + index).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'))
            .Replace("{folder}", Path.GetFileName(Path.GetDirectoryName(path) ?? ""))
            .ToString();
        if (name.Contains("{content}", StringComparison.Ordinal))
            name = name.Replace("{content}", content?.Invoke(path) ?? stem);
        if (name.Contains("{date}", StringComparison.Ordinal))
        {
            var date = dateTaken?.Invoke(path) ?? SafeLastWrite(path);
            name = name.Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        name = rule.Case switch
        {
            NameCase.Lower => name.ToLower(CultureInfo.CurrentCulture),
            NameCase.Upper => name.ToUpper(CultureInfo.CurrentCulture),
            NameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture)),
            _ => name,
        };
        var cleaned = Clean(name);
        // ".gitkeep" has no name part to begin with; only a name that becomes empty is a problem.
        bool empty = cleaned.Length == 0 && (hadName || extension.Length == 0);
        return (cleaned + extension, empty);
    }

    private static string ReplaceText(string text, string find, string replacement, bool matchCase) =>
        text.Replace(find, replacement, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>Characters Windows doesn't allow become "-"; trailing dots and spaces are dropped.</summary>
    public static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Renames everything that changes. Each item first moves to a temporary name, so names can be
    /// swapped or shifted within the selection. Returns the renames done, for undoing them later.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> Apply(IReadOnlyList<RenameItem> plan)
    {
        var moves = plan.Where(i => i.Changes).Select(i => (From: i.Path, To: i.NewPath)).ToList();
        return Move(moves);
    }

    /// <summary>Puts back what <see cref="Apply"/> did.</summary>
    public static IReadOnlyList<(string From, string To)> Undo(IReadOnlyList<(string From, string To)> done) =>
        Move(done.Select(d => (d.To, d.From)).ToList());

    private static IReadOnlyList<(string From, string To)> Move(List<(string From, string To)> moves)
    {
        var staged = new List<(string Temp, string From, string To)>();
        try
        {
            foreach (var (from, to) in moves)
            {
                var temp = Path.Combine(Path.GetDirectoryName(from) ?? "", $".petal-rename-{Guid.NewGuid():N}");
                MovePath(from, temp);
                staged.Add((temp, from, to));
            }
        }
        catch
        {
            // Put the ones already staged back before reporting the failure.
            foreach (var (temp, from, _) in staged)
                TryMove(temp, from);
            throw;
        }

        var done = new List<(string From, string To)>();
        foreach (var (temp, from, to) in staged)
        {
            try
            {
                MovePath(temp, to);
                done.Add((from, to));
            }
            catch
            {
                // Something took the name meanwhile: keep the original name.
                TryMove(temp, from);
            }
        }
        return done;
    }

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            MovePath(from, to);
        }
        catch
        {
            // Nothing more to try.
        }
    }
}
