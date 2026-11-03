using Petal.Engines.Media;
using Petal.Recipes;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>Dub: the language a video goes into, a new line heard back against what was meant, the voice learnt for a line, the subtitles, and the translation put right.</summary>
public sealed class DubbingRecipeTests
{
    [Fact]
    public void A_dub_goes_into_English_and_from_English_into_the_language_given()
    {
        Assert.Equal("en", Dubbing.Opposite("ar"));
        Assert.Equal("en", Dubbing.Opposite("fr"));
        Assert.Equal("ar", Dubbing.Opposite("en"));
        Assert.Equal("tr", Dubbing.Opposite("en", "tr"));
        Assert.Equal(8, Dubbing.Languages.Count);
    }

    [Theory]
    [InlineData("The stars are far away.", "the stars are far away", 0)]
    [InlineData("The stars are far away.", "The stars are very far away!", 0.2)]
    [InlineData("The stars are far away.", "", 1)]
    // Arabic as Whisper may write it back: another hamza, a taa marbuta as haa, the vowel marks.
    [InlineData("أنا في المدرسة الآن", "انا في المدرسه الآنَ", 0)]
    [InlineData("مرحبا بكم", "أهلا بكم", 0.5)]
    // A case ending heard, a name spelt another way: as said. A word said wrong: not.
    [InlineData("أنا شخص محظوظ جداً", "أنا شخصاً محظوظاً جدا", 0)]
    [InlineData("إنه جوش ألين في الفضاء", "إنه جوش أليم في الفضاء", 0)]
    [InlineData("أنا شخص محظوظ جداً", "أنا شخص محلوض جدا", 0.25)]
    [InlineData("في البيت", "فيه البيت", 0.5)]
    public void A_new_line_heard_back_is_compared_word_by_word(string meant, string heard, double errors) =>
        Assert.Equal(errors, Dubbing.WordErrors(meant, heard), 3);

    [Fact]
    public void A_voice_is_learnt_from_its_line_and_a_short_line_takes_in_more_around_it()
    {
        const int Rate = 100;
        var voice = Enumerable.Range(0, 20 * Rate).Select(i => (float)i).ToArray();
        // A long line: itself and a tenth of a second either side.
        var own = Dubbing.Reference(voice, new DubLine(4, 11, "", ""), Rate);
        Assert.Equal(390, (int)own[0]);
        Assert.Equal(720, own.Length);
        // A one-second line: five seconds around its middle.
        var widened = Dubbing.Reference(voice, new DubLine(10, 11, "", ""), Rate);
        Assert.Equal(5 * Rate, widened.Length);
        Assert.Equal(8 * Rate, (int)widened[0]);
        // At the very start: what there is.
        Assert.Equal(0, (int)Dubbing.Reference(voice, new DubLine(0, 1, "", ""), Rate)[0]);
    }

    [Fact]
    public void Sound_is_brought_to_the_rate_Whisper_hears()
    {
        var tone = Enumerable.Range(0, 24000).Select(i => (float)Math.Sin(2 * Math.PI * 300 * i / 24000)).ToArray();
        var heard = Dubbing.Resample(tone, 24000, 16000);
        Assert.Equal(16000, heard.Length);
        Assert.Equal(Math.Sin(2 * Math.PI * 300 * 8001 / 16000.0), heard[8001], 2);
    }

    [Fact]
    public void Subtitles_have_the_new_lines_at_their_new_times()
    {
        var srt = Dubbing.Srt([(0.5, 2.25, "مرحبا بكم"), (61.25, 3725.5, "Second line")]);
        Assert.Equal("1\n00:00:00,500 --> 00:00:02,250\nمرحبا بكم\n\n2\n00:01:01,250 --> 01:02:05,500\nSecond line\n\n", srt);
    }

    [Theory]
    // An English article left among the Arabic words.
    [InlineData("عندنا a برنامج جديد", "عندنا برنامج جديد")]
    [InlineData("مثل ذلك مثل a شيء؟", "مثل ذلك مثل شيء؟")]
    [InlineData("ذهبت إلى the متجر", "ذهبت إلى متجر")]
    // English words that belong there stay.
    [InlineData("أحب Plan A كثيرا", "أحب Plan A كثيرا")]
    [InlineData("استخدم a.m. في الصباح", "استخدم a.m. في الصباح")]
    // A number's thousands parted by an Arabic comma.
    [InlineData("سيكون لدينا 10،000 شخص", "سيكون لدينا 10,000 شخص")]
    [InlineData("1،000،000 دولار و3،4", "1,000,000 دولار و3،4")]
    [InlineData("", "")]
    public void Arabic_translations_are_put_right(string translated, string expected) => Assert.Equal(expected, Translator.Tidy(translated));

    [Fact]
    public void Vowel_marks_on_every_letter_go_and_a_few_put_there_on_purpose_stay()
    {
        // Fatha, damma, kasra and sukun all over: written as nobody writes today.
        string marked = "عِنْدَنا برنامج طاقمِ تجاري ساعدَ";
        Assert.Equal("عندنا برنامج طاقم تجاري ساعد", Translator.Tidy(marked));
        // The "an" ending and the doubling mark are how Arabic is written.
        Assert.Equal("أنا شخص محظوظ جداً", Translator.Tidy("أنا شخص محظوظ جداً"));
        Assert.Equal("هذا مهمّ", Translator.Tidy("هذا مهمّ"));
        // One mark to tell two words apart stays.
        Assert.Equal("عُلم الأمر", Translator.Tidy("عُلم الأمر"));
    }
}
