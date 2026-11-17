using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Recipes.Study;

/// <summary>How many diagrams and activities to make from each passage, besides the explanation.</summary>
public enum StudyActivities
{
    None,
    Some,
    Many,
}

/// <summary>What a request aims at, as the window says it back; in the order the window says them.</summary>
public enum StudyAim
{
    Beginners,
    Children,
    Exam,
    Summary,
    Problems,
    Terms,
    Teaching,
    Visual,
}

/// <summary>What a request comes to: the languages it asks for, its aims, and whether it asks for diagrams or activities.</summary>
public sealed record StudyRequestReading(IReadOnlyList<string> Languages, IReadOnlyList<StudyAim> Aims, bool AsksActivities)
{
    public static readonly StudyRequestReading Empty = new([], [], false);
}

/// <summary>
/// What a person asks a study file to be, in their own words ("simply, for beginners, in English, with things to drag"):
/// the language it asks to be written in, when it says one, so the window's choice follows it; the rest is the model's to
/// follow. A language counts only where the request asks for it ("in English", "بالإنجليزية", "İngilizce olarak",
/// "به انگلیسی", "انگریزی میں", "dalam bahasa Inggris", "en anglais", "en inglés"), not where it only names one ("the
/// English terms", "العالم العربي", "from English").
/// </summary>
public static class StudyRequest
{
    /// <summary>The most characters a request may have: it goes with every passage, to a model that holds only a few pages.</summary>
    public const int Longest = 500;

    /// <summary>Each language's names in English, Turkish, Indonesian, French and Spanish, and in Arabic letters, folded as <see cref="Fold"/> folds.</summary>
    private sealed record Names(string Code, string English, string Turkish, string Indonesian, string French, string Spanish, string Arabic);

    private static readonly Names[] All =
    [
        new("en", "english", "ingilizce", "inggris", "anglais", "ingles", "انجليزي|انجليزيه|انكليزي|انكليزيه|انقليزي|انقليزيه|انگليسي|انگريزي"),
        new("ar", "arabic", "arapca", "arab", "arabe", "arabe", "عربي|عربيه"),
        new("tr", "turkish", "turkce", "turki", "turc", "turco", "تركي|تركيه"),
        new("fa", "persian|farsi", "farsca", "persia|farsi", "persan|farsi", "persa|farsi", "فارسي|فارسيه"),
        new("ur", "urdu", "urduca", "urdu", "ourdou", "urdu", "اردو|اوردو|اورديه|ارديه"),
        new("id", "indonesian", "endonezce", "indonesia", "indonesien", "indonesio", "اندونيسي|اندونيسيه|اندونزيايي|انڈونيشي"),
        new("fr", "french", "fransizca", "prancis|perancis", "francais", "frances", "فرنسي|فرنسيه|فرانسوي|فرانسيسي"),
        new("es", "spanish", "ispanyolca", "spanyol", "espagnol", "espanol", "اسباني|اسبانيه|اسپانيايي|هسپانوي"),
    ];

    private static readonly (string Code, Regex Asked, Regex Alone)[] Patterns = All.Select(n => (n.Code, Asking(n), Alone(n))).ToArray();

    /// <summary>The language a request asks the study file to be written in; null when it asks for none, or for two.</summary>
    public static string? Language(string request) => Languages(request) is [var one] ? one : null;

    /// <summary>Every language a request asks the study file to be written in, in the order of <see cref="All"/>.</summary>
    public static IReadOnlyList<string> Languages(string request)
    {
        if (string.IsNullOrWhiteSpace(request))
            return [];
        var text = " " + Fold(request) + " ";
        var found = Patterns.Where(p => p.Asked.IsMatch(text)).Select(p => p.Code).ToList();
        // A request that is only a language's name ("English", "بالعربي") asks for it too.
        if (found.Count == 0)
            found = Patterns.Where(p => p.Alone.IsMatch(text)).Select(p => p.Code).ToList();
        return found;
    }

    /// <summary>
    /// What a request comes to, said back to the person before anything is made: the languages it asks for, what it aims
    /// at (beginners, children, an exam…), and whether it asks for diagrams or activities. Read from words in the eight
    /// languages, without a model; the model itself reads the whole request.
    /// </summary>
    public static StudyRequestReading Read(string request)
    {
        if (string.IsNullOrWhiteSpace(request))
            return StudyRequestReading.Empty;
        var text = " " + Fold(request) + " ";
        var aims = AimWords.Where(a => a.Words.IsMatch(text)).Select(a => a.Aim).ToList();
        bool visual = aims.Contains(StudyAim.Visual);
        return new StudyRequestReading(Languages(request), aims, visual || ActivityWords.IsMatch(text));
    }

    /// <summary>
    /// Words, folded as <see cref="Fold"/> folds: a word ending in "$" is whole, any other is the start of a word (so
    /// "beginner" is "beginners" too). Arabic's joined little words (و ف ب ل ال) may come before. Write them folded:
    /// "مسايل" for مسائل, "كودك" for کودک, "ساده" for سادہ, no accents.
    /// </summary>
    private static Regex Words(params string[] words) => new(
        @"(?<![\p{L}\p{N}])(?:و|ف|ب|ل|ال|وال|بال|لل|فال)?(?:" + string.Join("|", words.Select(w => w.EndsWith('$') ? Regex.Escape(w[..^1]) + After : Regex.Escape(w))) + ")",
        RegexOptions.CultureInvariant);

    private static readonly (StudyAim Aim, Regex Words)[] AimWords =
    [
        (StudyAim.Beginners, Words("beginner", "simple$", "simply$", "easy$", "first time", "مبتدي", "مبسط", "ببساطه", "بسيط", "سهل$", "basit", "yeni baslayan", "kolay", "ساده", "اسان$", "pemula", "sederhana", "mudah$", "debutant", "simplement", "facile", "principiante", "sencill", "facil")),
        (StudyAim.Children, Words("child", "kid$", "kids$", "اطفال", "طفل", "صغار", "cocuk", "كودك", "بچه", "بچو", "بچي", "anak$", "anak-anak", "enfant", "nino", "ninos", "infantil")),
        (StudyAim.Exam, Words("exam", "test$", "tests$", "revision", "revise", "امتحان", "اختبار", "مراجعه", "sinav", "ازمون", "ٹيسٹ", "ujian", "examen", "repaso")),
        (StudyAim.Summary, Words("summar", "brief", "short$", "shorter$", "ملخص", "تلخيص", "لخص$", "لخصه$", "لخصها$", "مختصر", "باختصار", "ozet", "kisa$", "kisaca", "خلاصه", "كوتاه", "ringkas", "singkat", "resume$", "resumer", "bref$", "court$", "courte$", "resumen", "breve")),
        (StudyAim.Problems, Words("problem", "exercise", "solve", "solving", "worked example", "مسايل", "محلوله", "حل كرن", "حل شده", "تمارين", "تمرين", "مسيله", "مساله", "مشق", "soru coz", "alistirma", "cozum", "soal$", "latihan", "penyelesaian", "exercice", "resoudre", "ejercicio", "resolver")),
        (StudyAim.Terms, Words("term$", "terms$", "terminology", "vocabular", "definition", "glossary", "مصطلح", "تعريفات", "مفردات", "terim", "kavram", "tanim", "اصطلاح", "واژه", "istilah", "kosakata", "definisi", "terme$", "termes$", "terminolog", "termino", "definicion")),
        (StudyAim.Teaching, Words("teach", "lesson", "classroom", "class$", "معلم", "تدريس", "حصه$", "ders$", "dersi$", "ogretmen", "sinif", "سبق$", "استاد", "كلاس", "pelajaran", "guru$", "mengajar", "kelas$", "lecon", "enseign", "professeur", "classe$", "leccion", "clase$", "ensen", "profesor")),
        (StudyAim.Visual, Words("visual", "diagram", "chart", "map$", "maps$", "mind map", "draw", "مرئي", "بصري", "رسوم", "رسومات", "مخطط", "خريطه", "خرائط", "gorsel", "sema$", "semalar", "harita", "تصويري", "نمودار", "نقشه", "خاكه", "gambar", "bagan", "peta$", "visuel", "schema", "carte$", "cartes$", "esquema", "mapa$", "mapas$")),
    ];

    /// <summary>Words that ask for something to do on the page: to drag, match, sort, play, or "interactive".</summary>
    private static readonly Regex ActivityWords = Words(
        "activit", "interactiv", "drag", "match", "sort$", "puzzle", "game", "نشاط", "انشطه", "تفاعلي", "تفاعليه", "اسحب", "سحب$", "توصيل", "العاب", "لعبه",
        "etkinlik", "etkilesim", "interaktif", "surukle", "eslestir", "oyun", "فعاليت", "تعاملي", "بازي", "سرگرمي", "انٹرايكٹو", "aktivitas", "seret", "cocokkan",
        "permainan", "activite", "interactif", "glisser", "associer", "jeu$", "jeux$", "actividad", "interactiv", "arrastr", "emparej", "juego");

    private const string Before = @"(?<![\p{L}\p{N}'])";
    private const string After = @"(?![\p{L}\p{N}])";

    private static Regex Asking(Names n) => new(string.Join("|",
        // English: "in English", "into English", "to English", "an English version".
        $@"{Before}(?:in|into|to)\s+(?:the\s+)?(?:{n.English}){After}",
        $@"{Before}(?:{n.English})\s+(?:version|translation|please|language|only){After}",
        // Turkish: "İngilizce olarak / yaz / anlat", "İngilizceye çevir"; not "İngilizceden".
        $@"{Before}(?:{n.Turkish})(?:\s+(?:olarak|yaz|yazin|yazsin|anlat|acikla|hazirla|olsun|dilinde)|'?y?[ae]){After}",
        // Indonesian: "dalam bahasa Inggris", "ke bahasa Inggris"; not "dari bahasa Inggris".
        $@"{Before}(?:dalam|ke|pakai|dengan|berbahasa)\s+(?:bahasa\s+)?(?:{n.Indonesian}){After}",
        // French: "en anglais", "vers l'anglais"; Spanish: "en inglés", "al inglés", "a inglés".
        $@"{Before}(?:en|vers)\s+(?:l'|le\s+)?(?:{n.French}){After}",
        $@"{Before}(?:en|al|a)\s+(?:el\s+)?(?:{n.Spanish}){After}",
        // Arabic: "بالإنجليزية", "باللغة الإنجليزية", "إلى العربية", "للعربية"; Persian: "به انگلیسی", "به زبان انگلیسی".
        $@"{Before}(?:ب|بال|لل|باللغه\s+(?:ال)?|الي\s+(?:اللغه\s+)?(?:ال)?|للغه\s+(?:ال)?|به\s+(?:زبان\s+)?|بزبان\s+)(?:{n.Arabic}){After}",
        // Urdu: "انگریزی میں", "انگریزی زبان میں"; Persian: "انگلیسی بنویس".
        $@"{Before}(?:ال)?(?:{n.Arabic})\s+(?:زبان\s+)?(?:ميں|مين|بنويس|بنويسيد){After}"), RegexOptions.CultureInvariant);

    private static Regex Alone(Names n) => new(
        $@"^\s*(?:(?:bahasa\s+)?(?:{n.English}|{n.Turkish}|{n.Indonesian}|{n.French}|{n.Spanish})|(?:ب|بال|ال)?(?:{n.Arabic}))\s*[.!،]?\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A request as it is compared: lower case, without accents, Arabic letters in one form without vowel marks, and
    /// Persian's and Urdu's ی ک ہ ے as Arabic writes them.
    /// </summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace('İ', 'i').Replace('ı', 'i').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark || c is 'ـ' or '‌' or '‎' or '‏')
                continue;
            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' or 'ۀ' or 'ە' or 'ہ' or 'ھ' => 'ه',
                'ى' or 'ی' or 'ے' or 'ئ' => 'ي',
                'ک' => 'ك',
                '’' or '‘' => '\'',
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
