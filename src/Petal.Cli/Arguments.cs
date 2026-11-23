namespace Petal.Cli;

/// <summary>A mistake in how the command was typed; the message says what to fix.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Nothing to work on: no file matches, or a folder holds none that the command can take.</summary>
internal sealed class NothingToDoException(string message) : Exception(message);

/// <summary>A file to work on, and the folder named on the command line it was found in (null for a file named itself).</summary>
internal sealed record Input(string File, string? Root);

/// <summary>
/// "--to png", "--to=png", "-o out.zip" and plain values, in any order. Options listed as flags
/// take no value.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Values { get; } = [];

    public static Arguments Parse(IReadOnlyList<string> args, params string[] flags)
    {
        var parsed = new Arguments();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            bool isOption = arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2
                            || arg.Length == 2 && arg[0] == '-' && char.IsLetter(arg[1]);
            if (!isOption)
            {
                parsed.Values.Add(arg);
                continue;
            }
            var name = arg.TrimStart('-');
            string? value = null;
            int eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (!flags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                    throw new UsageException($"--{name} needs a value.");
                value = args[++i];
            }
            parsed._options[name] = value;
        }
        return parsed;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(params string[] names) =>
        names.Select(n => _options.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);

    public int? Int(string name, int min, int max)
    {
        if (Get(name) is not { } text)
            return null;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new UsageException($"--{name} must be a whole number from {min} to {max}.");
        return value;
    }

    /// <summary>Options that this command doesn't know, so typos don't pass silently.</summary>
    public void Only(params string[] known)
    {
        var unknown = _options.Keys.FirstOrDefault(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new UsageException($"Unknown option --{unknown}.");
    }

    /// <summary>
    /// The files named on the command line, with wildcards expanded (cmd.exe leaves "*.png" to the
    /// program). Every name must exist.
    /// </summary>
    public IReadOnlyList<string> Files(bool allowFolders = false)
    {
        if (Values.Count == 0)
            throw new UsageException("Name at least one file.");
        var files = new List<string>();
        foreach (var value in Values)
        {
            if (value.IndexOfAny(['*', '?']) >= 0)
            {
                var dir = Path.GetDirectoryName(value);
                dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
                var matches = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, Path.GetFileName(value)).Order(StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                if (matches.Count == 0)
                    throw new NothingToDoException($"Nothing matches {value}.");
                files.AddRange(matches);
                continue;
            }
            var full = Path.GetFullPath(value);
            if (File.Exists(full) || allowFolders && Directory.Exists(full))
                files.Add(full);
            else if (Directory.Exists(full))
                throw new UsageException($"{value} is a folder; name the files in it (for example \"{Path.Combine(value, "*.jpg")}\").");
            else
                throw new NothingToDoException($"{value} doesn't exist.");
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Files and folders: a folder gives the files in it (and in its subfolders with
    /// <paramref name="subfolders"/>), in name order, each with the folder it came from so results can keep
    /// their place under --out. Hidden and system files are left out.
    /// </summary>
    public IReadOnlyList<Input> FilesAndFolders(bool subfolders)
    {
        var inputs = new List<Input>();
        foreach (var item in Files(allowFolders: true))
        {
            if (!Directory.Exists(item))
            {
                inputs.Add(new(item, null));
                continue;
            }
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = subfolders,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = true,
            };
            inputs.AddRange(Directory.EnumerateFiles(item, "*", options).Order(StringComparer.OrdinalIgnoreCase).Select(f => new Input(f, item)));
        }
        return inputs.DistinctBy(i => i.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>--out: an existing folder for the results, or null to save beside each original.</summary>
    public string? OutputFolder()
    {
        if (Get("out") is not { } folder)
            return null;
        var full = Path.GetFullPath(folder);
        Directory.CreateDirectory(full);
        return full;
    }
}
