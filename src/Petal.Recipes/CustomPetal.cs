using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.Recipes;

/// <summary>
/// A tool of the user's own on the tools wheel, described by a small JSON file in
/// %APPDATA%\Petal\petals: which files it takes, and which program runs on them. For example:
/// <code>
/// {
///   "name": "Optimise PNG", "name_ar": "تحسين PNG", "icon": "sparkles",
///   "accepts": ["png"],
///   "program": "C:\\Tools\\oxipng.exe",
///   "arguments": ["-o", "4", "--out", "{output}", "{input}"],
///   "output": "{name} Optimised.png"
/// }
/// </code>
/// Placeholders: {input}, {output}, {name} (without extension), {ext}, {folder}, and {inputs}
/// (every file, as separate arguments, when "each" is false). The program runs directly, never
/// through a shell, so names can't smuggle in commands; Petal shows the command before the first run.
/// </summary>
public sealed class CustomPetal
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("name_ar")] public string? NameArabic { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("description_ar")] public string? DescriptionArabic { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }

    /// <summary>Extensions ("png"), format ids, or families: "image", "video", "audio", "pdf", "document", "any".</summary>
    [JsonPropertyName("accepts")] public List<string> Accepts { get; set; } = [];

    [JsonPropertyName("program")] public string Program { get; set; } = "";
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = [];

    /// <summary>The result's name (placeholders allowed), saved beside the original. Null when the program saves its own results.</summary>
    [JsonPropertyName("output")] public string? Output { get; set; }

    /// <summary>The program's printed output is saved to this file (placeholders allowed), e.g. "{name} checksum.txt".</summary>
    [JsonPropertyName("stdout")] public string? Stdout { get; set; }

    /// <summary>Run once per file (the default), or once with all of them ({inputs}).</summary>
    [JsonPropertyName("each")] public bool Each { get; set; } = true;

    /// <summary>The manifest file it came from.</summary>
    [JsonIgnore] public string File { get; set; } = "";

    /// <summary>A fingerprint of the manifest: approval of a first run covers exactly this content.</summary>
    [JsonIgnore] public string Fingerprint { get; set; } = "";

    /// <summary>What's wrong with the manifest, or null when it can run.</summary>
    [JsonIgnore] public string? Problem => ProblemFormat is null ? null : ProblemDetail is null ? ProblemFormat : ProblemFormat.Replace("{0}", ProblemDetail);

    /// <summary><see cref="Problem"/> as a fixed sentence (to translate), with {0} for <see cref="ProblemDetail"/> when there is one.</summary>
    [JsonIgnore] public string? ProblemFormat { get; private set; }

    /// <summary>The program's name or the reading error that goes into <see cref="ProblemFormat"/>.</summary>
    [JsonIgnore] public string? ProblemDetail { get; private set; }

    /// <summary>%APPDATA%\Petal\petals, or beside the settings of a test or development run (PETAL_SETTINGS_DIR).</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR") is { Length: > 0 } own ? own : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal"), "petals");

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Every petal in the folder, broken ones included (with their <see cref="Problem"/>).</summary>
    public static IReadOnlyList<CustomPetal> LoadAll()
    {
        if (!Directory.Exists(Folder))
            return [];
        var petals = new List<CustomPetal>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            petals.Add(Load(file));
        return petals;
    }

    public static CustomPetal Load(string file)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(file);
            var petal = JsonSerializer.Deserialize<CustomPetal>(bytes, Json) ?? new CustomPetal();
            petal.File = file;
            petal.Fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            (petal.ProblemFormat, petal.ProblemDetail) = petal.Check() ?? (null, null);
            return petal;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CustomPetal { Name = Path.GetFileNameWithoutExtension(file), File = file, ProblemFormat = "The file can't be read: {0}", ProblemDetail = ex.Message };
        }
    }

    private (string Format, string? Detail)? Check()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return ("It has no \"name\".", null);
        if (Accepts.Count == 0)
            return ("It doesn't say which files it \"accepts\".", null);
        if (string.IsNullOrWhiteSpace(Program))
            return ("It doesn't name the \"program\" to run.", null);
        if (ResolveProgram() is null)
            return ("The program \"{0}\" wasn't found.", Program);
        if (!Each && Arguments.Any(a => a.Contains("{input}") || a.Contains("{output}")))
            return ("With \"each\": false, use {inputs} rather than {input} or {output}.", null);
        return null;
    }

    /// <summary>The program's full path: as given, or found on the PATH like a command prompt would.</summary>
    public string? ResolveProgram()
    {
        var program = Environment.ExpandEnvironmentVariables(Program.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(program))
            return System.IO.File.Exists(program) ? program : null;
        var names = Path.HasExtension(program) ? [program] : new[] { program + ".exe", program + ".com" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
        }
        return null;
    }

    public string DisplayName(bool arabic) => arabic && !string.IsNullOrWhiteSpace(NameArabic) ? NameArabic! : Name;

    /// <summary>The description in the interface language when the file has it, else whichever it has.</summary>
    public string? DisplayDescription(bool arabic) => arabic && !string.IsNullOrWhiteSpace(DescriptionArabic) ? DescriptionArabic : Description;

    /// <summary>True when every file is one this petal takes.</summary>
    public bool Takes(IReadOnlyList<string> files) =>
        Problem is null && files.Count > 0 && (Each || files.Count >= 1) && files.All(f => !Directory.Exists(f) && TakesOne(f));

    private bool TakesOne(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        var format = Formats.FromPath(path);
        foreach (var raw in Accepts)
        {
            var accept = raw.Trim().TrimStart('.').ToLowerInvariant();
            if (accept is "any" or "*")
                return true;
            if (accept.Equals(extension, StringComparison.OrdinalIgnoreCase) || format is not null && accept == format.Id)
                return true;
            if (format is not null && accept == format.Family switch
                {
                    FormatFamily.Image or FormatFamily.Vector => "image",
                    FormatFamily.Video => "video",
                    FormatFamily.Audio => "audio",
                    FormatFamily.Pdf => "pdf",
                    FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook or FormatFamily.Slides or FormatFamily.Spreadsheet => "document",
                    _ => "",
                })
                return true;
        }
        return false;
    }

    /// <summary>One run of the program: what it is, and the files it should leave behind.</summary>
    public sealed record Run(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, string? Output, string? Stdout, IReadOnlyList<string> Inputs)
    {
        /// <summary>As a command line, for showing (quoted where needed).</summary>
        public string CommandLine => string.Join(' ', new[] { Program }.Concat(Arguments).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
    }

    /// <summary>The runs for these files, with placeholders filled in and outputs given names that are free.</summary>
    public IReadOnlyList<Run> Plan(IReadOnlyList<string> files, string? outputDirectory = null)
    {
        var program = ResolveProgram() ?? throw new InvalidOperationException($"The program \"{Program}\" wasn't found.");
        if (!Each)
        {
            var folder = outputDirectory ?? Path.GetDirectoryName(files[0])!;
            var args = new List<string>();
            foreach (var argument in Arguments)
            {
                if (argument == "{inputs}")
                    args.AddRange(files);
                else
                    args.Add(Fill(argument, files[0], null, folder));
            }
            return [new Run(program, args, folder, null, Stdout is null ? null : FreeName(Fill(Stdout, files[0], null, folder), folder), files)];
        }
        return files.Select(file =>
        {
            var folder = outputDirectory ?? Path.GetDirectoryName(file)!;
            var output = Output is null ? null : FreeName(Fill(Output, file, null, folder), folder);
            var stdout = Stdout is null ? null : FreeName(Fill(Stdout, file, output, folder), folder);
            return new Run(program, Arguments.Select(a => Fill(a, file, output, folder)).ToList(), folder, output, stdout, [file]);
        }).ToList();
    }

    private static string Fill(string template, string input, string? output, string folder) => template
        .Replace("{input}", input)
        .Replace("{output}", output ?? "")
        .Replace("{name}", Path.GetFileNameWithoutExtension(input))
        .Replace("{ext}", Path.GetExtension(input).TrimStart('.'))
        .Replace("{folder}", folder);

    /// <summary>A file name inside the folder that isn't taken ("photo Optimised 2.png").</summary>
    private static string FreeName(string name, string folder)
    {
        name = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        return OutputNaming.Unique(folder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name).TrimStart('.'));
    }

    /// <summary>Runs the program; returns the files it made. Fails with the program's own message when it does.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(Run run, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(run.Program)
        {
            WorkingDirectory = run.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in run.Arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The program didn't start.");
        using var kill = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        var printed = await stdout;
        if (process.ExitCode != 0)
        {
            var message = (await stderr).Trim();
            if (message.Length == 0)
                message = printed.Trim();
            throw new InvalidOperationException(message.Length > 0 ? message.Split('\n')[0].Trim() : $"The program stopped with code {process.ExitCode}.");
        }
        var made = new List<string>();
        if (run.Stdout is { } saved)
        {
            await System.IO.File.WriteAllTextAsync(saved, printed, new UTF8Encoding(true), CancellationToken.None);
            made.Add(saved);
        }
        if (run.Output is { } output)
        {
            if (!System.IO.File.Exists(output) && !Directory.Exists(output))
                throw new InvalidOperationException("The program finished without making its result.");
            made.Add(output);
        }
        return made;
    }

    /// <summary>A working example to start from: SHA-256 checksums with Windows' own certutil.</summary>
    public static string WriteExample()
    {
        Directory.CreateDirectory(Folder);
        var path = OutputNaming.Unique(Folder, "checksum example", "json");
        System.IO.File.WriteAllText(path, """
            {
              "name": "Checksum",
              "name_ar": "بصمة الملف",
              "description": "Saves the file's SHA-256 checksum beside it, to check a download.",
              "description_ar": "يحفظ بصمة SHA-256 للملف بجانبه، للتحقق من سلامة تنزيل.",
              "icon": "fileSearch",
              "accepts": ["any"],
              "program": "certutil",
              "arguments": ["-hashfile", "{input}", "SHA256"],
              "stdout": "{name} SHA-256.txt"
            }
            """, new UTF8Encoding(false));
        return path;
    }
}
