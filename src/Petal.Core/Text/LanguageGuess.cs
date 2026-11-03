namespace Petal.Core.Text;

/// <summary>
/// Tells the language of a piece of text well enough to translate it: the script first, and for Latin letters the
/// little words every language is full of. Null when there is too little to go on.
/// </summary>
public static class LanguageGuess
{
    private static readonly (string Code, HashSet<string> Words)[] Latin =
    [
        ("en", Set("the of and to in is that it for was with as on are this be at by have from or an you not but they we his her which will")),
        ("es", Set("el la de que y en los las del se por con para una un es no al lo como más pero sus le ya este sí porque esta entre cuando")),
        ("fr", Set("le la les de des et en un une du que qui dans pour pas au sur est ne se ce il elle avec plus nous vous sont cette mais ou")),
        ("de", Set("der die das und ist in zu den mit von für auf nicht ein eine sich dem des auch als an er es sie wir aber wie im bei oder")),
        ("it", Set("il lo la le di che e in un una per non con del della sono si da come più al dei nel ma anche questo ha gli alla")),
        ("pt", Set("o a os as de que e em um uma para com não do da dos das no na por mais se como mas foi ao são também pela pelo você")),
        ("tr", Set("bir ve bu için ile de da olarak daha çok en gibi kadar ama ne var olan sonra her ben sen biz siz değil mi ise")),
        ("id", Set("yang dan di ke dari untuk dengan pada ini itu dalam tidak akan saya anda kami mereka atau juga oleh ada sudah bisa karena")),
    ];

    private static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.Ordinal);

    public static string? Of(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        int arabic = 0, persian = 0, urdu = 0, cyrillic = 0, han = 0, kana = 0, hangul = 0, devanagari = 0, latin = 0;
        foreach (char c in text)
        {
            switch (c)
            {
                case >= (char)0x0600 and <= (char)0x06FF or >= (char)0x0750 and <= (char)0x077F or >= (char)0xFB50 and <= (char)0xFDFF or >= (char)0xFE70 and <= (char)0xFEFF:
                    arabic++;
                    // پ چ ژ گ ک ی are Persian's and Urdu's; ٹ ڈ ڑ ں ھ ے Urdu's alone.
                    if (c is 'پ' or 'چ' or 'ژ' or 'گ' or 'ک' or 'ی')
                        persian++;
                    if (c is 'ٹ' or 'ڈ' or 'ڑ' or 'ں' or 'ھ' or 'ے')
                        urdu++;
                    break;
                case >= (char)0x0400 and <= (char)0x04FF:
                    cyrillic++;
                    break;
                case >= (char)0x4E00 and <= (char)0x9FFF:
                    han++;
                    break;
                case >= (char)0x3040 and <= (char)0x30FF:
                    kana++;
                    break;
                case >= (char)0xAC00 and <= (char)0xD7AF:
                    hangul++;
                    break;
                case >= (char)0x0900 and <= (char)0x097F:
                    devanagari++;
                    break;
                default:
                    if (char.IsLetter(c))
                        latin++;
                    break;
            }
        }
        int most = new[] { arabic, cyrillic, han + kana, hangul, devanagari, latin }.Max();
        if (most == 0)
            return null;
        if (most == arabic)
            return urdu > 0 ? "ur" : persian * 40 > arabic ? "fa" : "ar";
        if (most == cyrillic)
            return "ru";
        if (most == han + kana)
            return kana > 0 ? "ja" : "zh";
        if (most == hangul)
            return "ko";
        if (most == devanagari)
            return "hi";
        return OfLatin(text);
    }

    private static string? OfLatin(string text)
    {
        var words = text.ToLowerInvariant().Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '"', '«', '»', '¿', '¡'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return null;
        string? best = null;
        int score = 0;
        foreach (var (code, common) in Latin)
        {
            int hits = words.Count(common.Contains);
            if (hits > score)
            {
                best = code;
                score = hits;
            }
        }
        // Turkish's own letters speak louder than a few shared words.
        if (text.Any(c => c is 'ğ' or 'ı' or 'ş' or 'İ'))
            return "tr";
        return best ?? "en";
    }
}
