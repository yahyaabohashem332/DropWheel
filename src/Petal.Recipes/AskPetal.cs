using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Models;

namespace Petal.Recipes;

/// <summary>What Ask Petal made of a request.</summary>
/// <param name="Recipe">The steps, or null when nothing in the request is something Petal can do.</param>
/// <param name="UsedModel">The AI model read the request (otherwise only the built-in word clues did).</param>
public sealed record AskAnswer(Recipe? Recipe, bool UsedModel);

/// <summary>
/// Ask Petal: a request in plain English or Arabic ("make them 1080 px WEBP and zip them",
/// "اضغطها للواتساب") becomes a recipe to run on the selected files. Common requests are read by
/// word clues at once; with the AI model downloaded (Qwen2.5 1.5B, run by llama.cpp on this PC),
/// free phrasing works too. Every step the model proposes must be backed by words in the request,
/// so it can't invent steps, and values the request states plainly (sizes, formats, apps) win.
/// </summary>
public static partial class AskPetal
{
    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.LlamaCpp, ModelCatalog.Qwen];

    public static bool ModelReady => Models.All(ModelStore.IsAvailable);

    // ---- Understanding by word clues ----

    internal sealed record Found(RecipeStep Step, int At);

    /// <summary>Letters folded so spelling variants match: Arabic hamzas, ta marbuta, alef maqsura, diacritics; lower case.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            switch (c)
            {
                case >= '\u064B' and <= '\u065F' or '\u0670' or 'ـ':
                    continue;
                case 'أ' or 'إ' or 'آ' or 'ٱ':
                    sb.Append('ا');
                    break;
                case 'ة':
                    sb.Append('ه');
                    break;
                case 'ى':
                    sb.Append('ي');
                    break;
                case '٠' or '١' or '٢' or '٣' or '٤' or '٥' or '٦' or '٧' or '٨' or '٩':
                    sb.Append((char)('0' + (c - '٠')));
                    break;
                default:
                    sb.Append(char.ToLowerInvariant(c));
                    break;
            }
        }
        return sb.ToString();
    }

    private const string FormatNames = "jpe?g|png|webp|heic|heif|avif|gif|tiff?|bmp|ico|pdf|mp4|mov|webm|mkv|avi|mp3|m4a|aac|wav|flac|ogg|opus|docx|word|txt|markdown|md|html|epub|xlsx|excel|csv|odt|rtf";

    [GeneratedRegex(@"(?<![\p{L}\d])(?<f>" + FormatNames + @")(?![\p{L}\d])")]
    private static partial Regex FormatWord();

    [GeneratedRegex(@"(?<n>\d{2,5})\s*(?:px|pixels?|pixel|بكسل|بيكسل|نقطه)")]
    private static partial Regex Pixels();

    [GeneratedRegex(@"(?:resize|shrink|scale|smaller|width|wide|long|size|صغر|تصغير|كبر|تكبير|مقاس|ابعاد|عرض|طول)\D{0,24}?(?<n>\d{3,5})(?!\s*(?:kb|mb|%|ك|م))")]
    private static partial Regex SizeNumber();

    // Short Arabic words only count as whole words: an optional "و", "ف", "ب", "ل" or "ال" in
    // front and an optional pronoun ("ها", "هم") behind, so "لف" isn't found inside "ملف".
    private const string WordStart = @"(?<![\p{L}])(?:[وفبل]|ال)?";
    private const string WordEnd = @"(?:ها|هم|هما|ه|يها)?(?![\p{L}])";

    [GeneratedRegex(@"rotate|turn (?:it|them)|upside|تدوير|" + WordStart + "(?:ادر|دور|اقلب)" + WordEnd)]
    private static partial Regex RotateWord();

    [GeneratedRegex(@"\bzip\b|archive|\.zip|ارشيف|مضغوط|zip")]
    private static partial Regex ZipWord();

    [GeneratedRegex(@"compress|smaller (?:file|size)|reduce (?:the )?size|file size|lighter|shrink (?:the )?files?|small enough|ضغط|اضغط|قلل|تقليل|حجم اصغر|حجمها|حجمه|" + WordStart + "(?:اخف|خفف)" + WordEnd)]
    private static partial Regex CompressWord();

    [GeneratedRegex(@"background|\bbg\b|cut ?out|transparent|خلفيه|الخلفيه|شفاف")]
    private static partial Regex BackgroundWord();

    [GeneratedRegex(@"watermark|stamp|علامه مائيه|" + WordStart + "(?:علامه|ختم)" + WordEnd)]
    private static partial Regex WatermarkWord();

    [GeneratedRegex(@"location|gps|metadata|meta data|exif|privacy|private|personal|camera (?:details|info)|الموقع|موقع التصوير|البيانات الوصفيه|الميتاداتا|الخصوصيه|بيانات الكاميرا|معلومات شخصيه|تنظيف|" + WordStart + "نظف" + WordEnd)]
    private static partial Regex CleanWord();

    [GeneratedRegex(@"rename|name them|call them|naming|تسميه|اعد تسميه|غير الاسم|غير اسم|الاسماء|اسماء|" + WordStart + "(?:سم|سمي)" + WordEnd)]
    private static partial Regex RenameWord();

    [GeneratedRegex(@"(?:one|single|a single|combined?|merged?|together|all in(?:to)?)\W+(?:\w+\W+){0,2}pdf|pdf\W+(?:\w+\W+){0,1}(?:واحد|موحد)|combine|merge|join|دمج|ادمج|اجمع|موحد|" + WordStart + "ضم" + WordEnd)]
    private static partial Regex CombineWord();

    [GeneratedRegex(@"searchable|\bocr\b|text layer|search(?:able)? text|قابل للبحث|قابله للبحث|البحث|التعرف علي النص|استخراج النص")]
    private static partial Regex SearchableWord();

    [GeneratedRegex("[\"“”«»](?<t>[^\"“”«»]{1,60})[\"“”«»]|'(?<t>[^']{1,60})'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?:saying|that says|with the text|reads|text|كلمه|نص|تقول|عليها|مكتوب)\s+(?<t>[\p{L}\d][\p{L}\d ]{0,40}?)(?=$|[.,،]|\s(?:then|and|ثم|و))")]
    private static partial Regex SayingText();

    [GeneratedRegex(@"(?:rename(?: them| it)?|name them|call them|سمها|سميها|سمهم)\s+(?:to|as|إلى|الى|باسم|ب)?\s*(?<t>[^,.،؛;]{1,40}?)\s*(?=$|[,.،؛;]|\s(?:then|and|ثم)\s)", RegexOptions.IgnoreCase)]
    private static partial Regex RenameTo();

    [GeneratedRegex(@"#[0-9a-f]{6}\b")]
    private static partial Regex Hex();

    /// <summary>The steps the request's words call for, in the order they're mentioned.</summary>
    public static Recipe? Understand(string request, IReadOnlyCollection<FileFormat> formats)
    {
        var steps = Clues(request, formats);
        return steps.Count == 0 ? null : Named(steps.OrderBy(s => s.At).Select(s => s.Step).ToList());
    }

    private static List<Found> Clues(string request, IReadOnlyCollection<FileFormat> formats)
    {
        var text = Fold(request);
        var found = new List<Found>();
        void Add(RecipeStep step, int at) => found.Add(new Found(step, at));

        var zipWord = ZipWord().Match(text);
        bool zip = zipWord.Success;
        if (zip)
            Add(new RecipeStep { Kind = RecipeStepKind.Zip }, zipWord.Index);

        var combine = CombineWord().Match(text);
        if (combine.Success)
            Add(new RecipeStep { Kind = RecipeStepKind.CombinePdf }, combine.Index);

        if (SearchableWord().Match(text) is { Success: true } sm)
            Add(new RecipeStep { Kind = RecipeStepKind.Searchable }, sm.Index);

        // A new format: one the files aren't already in (and not the PDF a combine makes).
        foreach (Match m in FormatWord().Matches(text))
        {
            var target = FormatFor(m.Groups["f"].Value);
            if (target is null || formats.Count > 0 && formats.All(f => f == target))
                continue;
            if (target == Formats.Pdf && (combine.Success || SearchableWord().IsMatch(text)))
                continue;
            if (target == Formats.Zip)
                continue;
            Add(new RecipeStep { Kind = RecipeStepKind.Convert, Format = target.Id }, m.Index);
            break;
        }

        var pixels = Pixels().Match(text);
        var size = pixels.Success ? pixels : SizeNumber().Match(text);
        if (size.Success && int.TryParse(size.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int side) && side is >= 16 and <= 20000)
            Add(new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = side }, size.Index);

        if (RotateWord().Match(text) is { Success: true } rm)
            Add(new RecipeStep { Kind = RecipeStepKind.Rotate, Turns = Turns(text) }, rm.Index);

        // Arabic "اضغط" means both: next to a ZIP ("في ملف مضغوط") it's the ZIP, unless an app
        // or "as small as possible" says the files themselves should shrink.
        var compress = CompressWord().Match(text);
        bool packing = zip && compress.Value is "ضغط" or "اضغط" && App(text) is null && !Strong(text);
        if (compress.Success && !packing)
            Add(new RecipeStep { Kind = RecipeStepKind.Compress, App = App(text), Strong = Strong(text) }, compress.Index);

        if (BackgroundWord().Match(text) is { Success: true } bm)
            Add(new RecipeStep { Kind = RecipeStepKind.RemoveBackground, Background = Colour(text) }, bm.Index);

        if (WatermarkWord().Match(text) is { Success: true } wm && WatermarkText(request) is { } mark)
            Add(new RecipeStep { Kind = RecipeStepKind.Watermark, Text = mark }, wm.Index);

        if (CleanWord().Match(text) is { Success: true } cm)
            Add(new RecipeStep { Kind = RecipeStepKind.Clean }, cm.Index);

        if (RenameWord().Match(text) is { Success: true } nm && !WatermarkWord().IsMatch(text)
            && (Quoted().Match(request) is { Success: true } q ? q.Groups["t"].Value : RenameTo().Match(request) is { Success: true } to ? to.Groups["t"].Value : null) is { Length: > 0 } pattern)
            Add(new RecipeStep { Kind = RecipeStepKind.Rename, Pattern = Pattern(pattern) }, nm.Index);
        return found;
    }

    private static FileFormat? FormatFor(string word) => word switch
    {
        "jpeg" or "jpg" => Formats.Jpg,
        "tif" or "tiff" => Formats.Tiff,
        "heif" => Formats.Heic,
        "word" => Formats.Docx,
        "excel" => Formats.Xlsx,
        "markdown" => Formats.Md,
        _ => Formats.FromId(word),
    };

    private static int Turns(string text) =>
        Regex.IsMatch(text, @"180|upside|half turn|مقلوب|راسا علي عقب|نصف دوره") ? 180
        : Regex.IsMatch(text, @"left|anti-?clockwise|counter-?clockwise|يسار|اليسار|عكس عقارب") ? 270
        : 90;

    private static string? App(string text) =>
        Regex.IsMatch(text, "whats ?app|واتس|واتساب|وتساب") ? "whatsapp"
        : Regex.IsMatch(text, @"e-?mail|\bmail\b|gmail|outlook|بريد|ايميل|الايميل") ? "email"
        : Regex.IsMatch(text, "discord|ديسكورد|دسكورد") ? "discord"
        : Regex.IsMatch(text, "instagram|insta|انستا|انستغرام|انستقرام|انستجرام") ? "instagram"
        : Regex.IsMatch(text, @"upload|\bform\b|portal|رفع|ارفع|بوابه|استماره") ? "upload"
        : null;

    private static bool Strong(string text) => Regex.IsMatch(text, @"smallest|as small as|strong|maximum|max\b|a lot|اقصي|اقل حجم|اصغر ما يمكن|قوي|بشده|كثيرا");

    private static string? Colour(string text)
    {
        if (Hex().Match(text) is { Success: true } hex)
            return hex.Value.ToUpperInvariant();
        return Regex.IsMatch(text, "white|ابيض|بيضاء|بيضا") ? "#FFFFFF"
            : Regex.IsMatch(text, "black|اسود|سوداء|سودا") ? "#000000"
            : Regex.IsMatch(text, "grey|gray|رمادي") ? "#808080"
            : Regex.IsMatch(text, "blue|ازرق|زرقاء") ? "#1E5AA8"
            : null;
    }

    private static string? WatermarkText(string request)
    {
        if (Quoted().Match(request) is { Success: true } q)
            return q.Groups["t"].Value.Trim();
        return SayingText().Match(request) is { Success: true } s ? s.Groups["t"].Value.Trim() : null;
    }

    private static string Pattern(string pattern)
    {
        pattern = pattern.Trim();
        return pattern.Contains('{') ? pattern : pattern + " {n}";
    }

    /// <summary>A short name from the steps: "Resize · WEBP · ZIP".</summary>
    private static Recipe Named(List<RecipeStep> steps)
    {
        var name = string.Join(" · ", steps.Select(s => s.Kind switch
        {
            RecipeStepKind.Convert => Formats.FromId(s.Format ?? "")?.Label ?? s.Format ?? "",
            RecipeStepKind.Resize => $"{s.LongestSide} px",
            RecipeStepKind.Compress => s.App ?? "Compress",
            RecipeStepKind.CombinePdf => "PDF",
            RecipeStepKind.Zip => "ZIP",
            RecipeStepKind.Searchable => "OCR",
            RecipeStepKind.Rotate => "Rotated",
            RecipeStepKind.RemoveBackground => "No Background",
            RecipeStepKind.Watermark => "Watermarked",
            RecipeStepKind.Clean => "Clean",
            RecipeStepKind.Rename => "Renamed",
            _ => s.Kind.ToString(),
        }));
        return new Recipe { Name = name.Length > 60 ? name[..60] : name, Steps = steps, OnWheel = false };
    }

    // ---- The AI model ----

    private const string SystemPrompt = """
        You turn a request about the user's selected files into steps for Petal, a file tool. Requests may be in English or Arabic. Answer with JSON only.

        Use only the steps the request asks for, in the order it asks for them. Never add a step the request doesn't mention.

        Each step is {"do": action, "value": text}:
        - convert: change the format; value is the new format: jpg, png, webp, heic, avif, gif, tiff, bmp, ico, pdf, mp4, mov, webm, mkv, mp3, m4a, wav, flac, ogg, docx, txt, md, html, epub, xlsx, csv
        - resize: make pictures smaller or larger; value is the longest side in pixels, e.g. "1080"
        - rotate: turn pictures; value is "90" (right, clockwise), "270" (left, counter-clockwise) or "180"
        - compress: make files smaller; value is "whatsapp", "email", "discord", "instagram" or "upload" only when the request names that app or place, "strong" for as small as possible, otherwise ""
        - remove_background: cut out the subject; value is a colour like "#FFFFFF" for a new background, or ""
        - watermark: stamp text on the files; value is the text
        - clean: remove the location, camera and author details for privacy; value ""
        - rename: value is the new name pattern; {n} is a running number, {name} the old name, {date} the date
        - zip: pack the files into one ZIP archive; value is a name or ""
        - combine_pdf: put all the files into one PDF; value is a name or ""
        - searchable: make scans searchable with OCR; value ""

        If the request is not about doing something to the files, answer {"steps": []}.

        Examples:
        Selected: 4 JPG photos. Request: shrink them to 1600 pixels and save as webp
        {"steps": [{"do": "resize", "value": "1600"}, {"do": "convert", "value": "webp"}]}
        Selected: 2 MOV videos. Request: make them small enough for email
        {"steps": [{"do": "compress", "value": "email"}]}
        Selected: 3 MP4 videos. Request: اضغطها لإرسالها على واتساب
        {"steps": [{"do": "compress", "value": "whatsapp"}]}
        Selected: 2 PDF documents. Request: make these smaller
        {"steps": [{"do": "compress", "value": ""}]}
        Selected: 10 JPG photos. Request: حوّلها إلى PDF واحد
        {"steps": [{"do": "combine_pdf", "value": ""}]}
        Selected: 6 HEIC photos. Request: remove the GPS location and convert them to jpg
        {"steps": [{"do": "clean", "value": ""}, {"do": "convert", "value": "jpg"}]}
        Selected: 3 PNG pictures. Request: أزل الخلفية ثم ضعها في ملف مضغوط
        {"steps": [{"do": "remove_background", "value": ""}, {"do": "zip", "value": ""}]}
        Selected: 5 JPG photos. Request: أدرها لليمين وأضف علامة مائية "سري"
        {"steps": [{"do": "rotate", "value": "90"}, {"do": "watermark", "value": "سري"}]}
        Selected: 1 PDF document. Request: tell me a joke
        {"steps": []}
        Selected: 4 MP3 audio files. Request: ما هو الطقس اليوم؟
        {"steps": []}
        """;

    private const string Schema = """
        {"type":"object","properties":{"steps":{"type":"array","maxItems":6,"items":{"type":"object","properties":{
        "do":{"enum":["convert","resize","rotate","compress","remove_background","watermark","clean","rename","zip","combine_pdf","searchable"]},
        "value":{"type":"string","maxLength":60}},"required":["do","value"]}}},"required":["steps"]}
        """;

    /// <summary>
    /// Asks the model when it's here, keeping only steps the request's words support; the word
    /// clues alone otherwise.
    /// </summary>
    public static async Task<AskAnswer> AskAsync(string request, IReadOnlyList<FileFormat> formats, CancellationToken cancellationToken = default)
    {
        var clues = Clues(request, formats);
        if (!ModelReady || string.IsNullOrWhiteSpace(request))
            return new AskAnswer(clues.Count == 0 ? null : Named(clues.OrderBy(c => c.At).Select(c => c.Step).ToList()), false);
        var proposed = await RunModelAsync(request, formats, cancellationToken).ConfigureAwait(false);
        return new AskAnswer(Ground(proposed, clues), true);
    }

    /// <summary>
    /// The model's steps that the request's words back up (each at the place it's mentioned), with
    /// stated values taken from the words; then any clue the model missed. Null when none is left.
    /// </summary>
    internal static Recipe? Ground(IReadOnlyList<RecipeStep> proposed, IReadOnlyList<Found> clues)
    {
        var steps = new List<Found>();
        foreach (var step in proposed)
        {
            var clue = clues.FirstOrDefault(c => c.Step.Kind == step.Kind);
            if (clue is null || steps.Any(s => s.Step.Kind == step.Kind))
                continue;
            var stated = clue.Step;
            // Plain values from the request win over the model's reading of them.
            var kept = step.Kind switch
            {
                RecipeStepKind.Convert => stated,
                RecipeStepKind.Resize => stated,
                RecipeStepKind.Rotate => stated,
                RecipeStepKind.Compress => stated,
                RecipeStepKind.RemoveBackground => stated.Background is null && step.Background is { } colour && Hex().IsMatch(colour.ToLowerInvariant()) ? step : stated,
                RecipeStepKind.Watermark => string.IsNullOrWhiteSpace(stated.Text) ? step : stated,
                RecipeStepKind.Rename => string.IsNullOrWhiteSpace(stated.Pattern) || stated.Pattern == "{name}" ? step : stated,
                _ => stated,
            };
            steps.Add(new Found(kept, clue.At));
        }
        foreach (var clue in clues)
        {
            if (!steps.Any(s => s.Step.Kind == clue.Step.Kind))
                steps.Add(clue);
        }
        return steps.Count == 0 ? null : Named(steps.OrderBy(s => s.At).Select(s => s.Step).ToList());
    }

    /// <summary>"12 JPG photos", "1 PDF document", "3 files".</summary>
    private static string Describe(IReadOnlyList<FileFormat> formats)
    {
        if (formats.Count == 0)
            return "some files";
        var kinds = formats.GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g =>
        {
            var noun = g.Key.Family switch
            {
                FormatFamily.Image or FormatFamily.Vector => "picture",
                FormatFamily.Video => "video",
                FormatFamily.Audio => "audio file",
                FormatFamily.Pdf => "document",
                _ => "file",
            };
            return $"{g.Count()} {g.Key.Label} {noun}{(g.Count() == 1 ? "" : "s")}";
        });
        return string.Join(", ", kinds);
    }

    /// <summary>The program's folder, unpacked from its download the first time.</summary>
    internal static string RuntimeFolder()
    {
        var zip = ModelStore.Find(ModelCatalog.LlamaCpp) ?? throw new InvalidOperationException("The AI model isn't downloaded yet.");
        var folder = Path.Combine(Path.GetDirectoryName(zip)!, "llama-runtime");
        if (File.Exists(Path.Combine(folder, "llama-completion.exe")))
            return folder;
        var staging = folder + ".part";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.Move(staging, folder);
        return folder;
    }

    internal static async Task<IReadOnlyList<RecipeStep>> RunModelAsync(string request, IReadOnlyList<FileFormat> formats, CancellationToken ct)
    {
        var folder = RuntimeFolder();
        var model = ModelStore.Find(ModelCatalog.Qwen)!;
        var work = Path.Combine(Path.GetTempPath(), "Petal", $"ask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var prompt = Path.Combine(work, "prompt.txt");
            var schema = Path.Combine(work, "schema.json");
            var clean = request.Replace("<|", "< |").Trim();
            if (clean.Length > 400)
                clean = clean[..400];
            await File.WriteAllTextAsync(prompt,
                $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\nSelected: {Describe(formats)}.\nRequest: {clean}<|im_end|>\n<|im_start|>assistant\n",
                new UTF8Encoding(false), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(schema, Schema, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(folder, "llama-completion.exe"))
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
            // The shared start of every prompt (instructions and examples) is cached beside the
            // model, so later questions only read their own few words.
            var cache = Path.Combine(Path.GetDirectoryName(model)!, "ask-petal.cache");
            foreach (var arg in new[] { "-m", model, "-f", prompt, "-jf", schema, "-n", "300", "--temp", "0", "-no-cnv", "--no-display-prompt",
                         "-c", "4096", "-t", threads.ToString(CultureInfo.InvariantCulture), "--prompt-cache", cache })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The AI model didn't start.");
            using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The AI model stopped unexpectedly.");
            return Parse(output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The model's JSON as steps; anything malformed counts as no steps.</summary>
    internal static IReadOnlyList<RecipeStep> Parse(string output)
    {
        int start = output.IndexOf('{'), end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
            return [];
        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)]);
            var steps = new List<RecipeStep>();
            foreach (var item in json.RootElement.GetProperty("steps").EnumerateArray())
            {
                var value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                RecipeStep? step = item.GetProperty("do").GetString() switch
                {
                    "convert" => FormatFor(Fold(value).Trim('.', ' ')) is { } f ? new RecipeStep { Kind = RecipeStepKind.Convert, Format = f.Id } : null,
                    "resize" => int.TryParse(Regex.Match(value, @"\d+").Value, NumberStyles.None, CultureInfo.InvariantCulture, out int side) && side is >= 16 and <= 20000
                        ? new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = side } : null,
                    "rotate" => new RecipeStep { Kind = RecipeStepKind.Rotate, Turns = value.Trim() is "180" ? 180 : value.Trim() is "270" ? 270 : 90 },
                    "compress" => new RecipeStep { Kind = RecipeStepKind.Compress, App = ShareTargets.Find(value) is { } t ? t.Id : null, Strong = value.Equals("strong", StringComparison.OrdinalIgnoreCase) },
                    "remove_background" => new RecipeStep { Kind = RecipeStepKind.RemoveBackground, Background = value.StartsWith('#') ? value.ToUpperInvariant() : null },
                    "watermark" => value.Trim().Length > 0 ? new RecipeStep { Kind = RecipeStepKind.Watermark, Text = value.Trim() } : null,
                    "clean" => new RecipeStep { Kind = RecipeStepKind.Clean },
                    "rename" => value.Trim().Length > 0 ? new RecipeStep { Kind = RecipeStepKind.Rename, Pattern = Pattern(value) } : null,
                    "zip" => new RecipeStep { Kind = RecipeStepKind.Zip },
                    "combine_pdf" => new RecipeStep { Kind = RecipeStepKind.CombinePdf },
                    "searchable" => new RecipeStep { Kind = RecipeStepKind.Searchable },
                    _ => null,
                };
                if (step is not null)
                    steps.Add(step);
            }
            return steps;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }
}
