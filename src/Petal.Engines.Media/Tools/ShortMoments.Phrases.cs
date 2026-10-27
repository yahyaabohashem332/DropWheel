namespace Petal.Engines.Media;

public static partial class ShortMoments
{
    /// <summary>
    /// The words and phrases the moments are read by, in English and Arabic, as <see cref="Key"/> writes them: lower
    /// case, no apostrophes ("dont"), Arabic with one alef, ya and ha ("الحقيقه") and no vowel marks. Arabic includes
    /// common Moroccan, Egyptian, Levantine and Gulf ways of saying the same.
    /// </summary>
    internal static class Phrases
    {
        private static string[][] Split(params string[] phrases) => phrases.Select(p => p.Split(' ')).ToArray();

        /// <summary>
        /// Words that start a question, for speech written without question marks. English is nearly always written with
        /// them, so only its question words are here ("can", "were" start as many statements: "we're" reads "were").
        /// </summary>
        public static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
        {
            "what", "why", "how", "who", "when", "where", "which", "whose",
            // Not "ما", "من" or "لم": they are as often "not" and "from".
            "ماذا", "لماذا", "ليش", "ليه", "كيف", "كيفاش", "ازاي", "متى", "امتى", "اين", "وين", "فين", "مين", "شكون", "هل",
            "ايش", "اش", "شو", "اشنو", "شنو", "واش", "علاش", "كم", "قديش", "شحال",
        };

        /// <summary>Words that tie a sentence to the one before it: a clip shouldn't start with them.</summary>
        public static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
        {
            "and", "but", "so", "because", "or", "then", "also", "plus", "which", "anyway", "however", "although", "though", "yet",
            "therefore", "besides", "nor", "whereas", "meanwhile",
            "و", "ف", "لكن", "ولكن", "بس", "لذلك", "ولذلك", "لهذا", "ولهذا", "ثم", "وبعدين", "بعدين", "ايضا", "وايضا", "كمان", "وكمان",
            "لان", "لانه", "لانها", "حيث", "اذن", "وبالتالي", "بالتالي", "يعني", "او", "بل", "وهكذا", "عشان", "علشان", "وهذا", "وهذه", "وهنا",
        };

        /// <summary>Sounds and words a speaker fills a pause with; a clip skips them at its start.</summary>
        public static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
        {
            "um", "uh", "er", "erm", "ah", "hmm", "mm", "mhm", "uhm", "yeah", "yes", "okay", "ok", "well", "so", "right", "alright", "oh",
            "يعني", "طيب", "اه", "اها", "ايه", "اوكي", "امم", "اممم", "ااه", "يلا", "ايوه", "اي", "نعم", "طب", "هيك", "صافي", "واخا",
        };

        /// <summary>
        /// Endings that make a statement sound like a question without asking one ("…, right?", "…, صح؟"): a sentence that
        /// ends with one isn't a question.
        /// </summary>
        public static readonly string[][] Tags = Split(
            "right", "you know", "isnt it", "ok", "okay", "yeah", "no", "correct", "huh", "eh", "dont you think", "you see", "am i right",
            "صح", "صحيح", "مش كده", "مش كدا", "اليس كذلك", "ولا لا", "واضح", "ماشي", "فهمت", "فاهم", "يعني", "مو", "مظبوط", "واخا");

        /// <summary>Fillers of two words: "all right, …", "you know, …".</summary>
        public static readonly string[][] TwoWordFillers = Split("all right", "you know", "i mean", "and so", "so yeah", "okay so", "يعني ايه", "طيب يعني");

        /// <summary>Words that turn to what comes next: a short sentence starting with one, at a clip's end, belongs to the next part.</summary>
        public static readonly HashSet<string> Turns = new(StringComparer.Ordinal)
        {
            "so", "now", "okay", "ok", "alright", "next", "anyway", "all", "and",
            "طيب", "اذن", "الان", "دابا", "يلا", "هلا", "هلق", "و",
        };

        /// <summary>Phrases that move on to something else: a clip ends before them, not with them.</summary>
        public static readonly string[][] Transitions = Split(
            "the next thing", "next thing", "another thing", "the other thing", "moving on", "lets talk about", "now lets", "lets move on",
            "the next question", "next question", "speaking of", "on another note", "by the way", "anyway", "the next step", "next up",
            "النقطه التاليه", "ننتقل الى", "لننتقل الى", "الشيء الثاني", "الشي الثاني", "السؤال التالي", "نمر الى", "بالمناسبه",
            "على كل حال", "على العموم", "الحاجه الثانيه", "خلينا ننتقل");

        /// <summary>Words that point back at something said before.</summary>
        public static readonly HashSet<string> Pronouns = new(StringComparer.Ordinal)
        {
            "it", "this", "that", "they", "he", "she", "these", "those", "them", "his", "her", "its", "their", "there",
            "هذا", "هذه", "هذي", "ذلك", "تلك", "هو", "هي", "هم", "هن", "هاد", "هادي", "هادا", "هاذا", "دا", "دي", "ده", "هيدا", "هيدي",
        };

        /// <summary>Openings that make you want to hear the rest.</summary>
        public static readonly string[][] Hooks = Split(
            "imagine", "here is", "heres", "the secret", "the truth", "the problem", "the biggest", "the best", "the worst", "the most",
            "the first time", "the hardest", "the craziest", "the only", "the real", "nobody", "no one", "everyone", "everybody",
            "most people", "never", "always", "what if", "did you know", "do you know", "let me tell you", "ill tell you", "listen",
            "believe it or not", "turns out", "it turns out", "the reason", "the key", "the thing is", "my favorite", "my favourite",
            "i couldnt believe", "i was shocked", "the mistake", "the biggest mistake", "the lesson", "surprisingly", "honestly", "the moment",
            "you wont believe", "you have to", "you need to", "if you", "one thing", "the one thing", "think about",
            "i have a question", "my question is", "here is a question", "heres a question", "the question is",
            "عندي سؤال", "سؤالي هو", "السؤال هو", "لدي سؤال",
            "تخيل", "تخيلوا", "تخيلي", "اسمع", "اسمعوا", "السر", "الحقيقه", "المشكله", "اكبر", "افضل", "احسن", "اسوا", "اهم", "اول مره",
            "لا احد", "ما حدا", "ولا حد", "حتى واحد", "الكل", "الجميع", "كل الناس", "معظم الناس", "اغلب الناس", "ابدا", "دائما", "دايما", "ديما",
            "هل تعلم", "هل تعرف", "هل تعرفون", "ماذا لو", "شو لو", "صدق او لا تصدق", "الغريب", "المدهش", "العجيب",
            "المفاجاه", "الخطا", "اكبر خطا", "الدرس", "خليني اقولك", "خلوني اقول لكم", "بصراحه", "الشيء الوحيد");

        /// <summary>Someone telling what happened.</summary>
        public static readonly string[][] Story = Split(
            "when i was", "i remember", "one day", "one time", "the first time", "i realized", "i realised", "i learned", "i found out",
            "it happened", "back then", "years ago", "growing up", "my dad", "my mom", "my mum", "my father", "my mother", "my wife",
            "my husband", "my son", "my daughter", "my kids", "true story", "at that moment", "suddenly", "i was like", "we were",
            "عندما كنت", "لما كنت", "ملي كنت", "كنت صغير", "اتذكر", "اذكر", "في يوم", "ذات يوم", "ذات مره", "اول مره", "اكتشفت", "عرفت",
            "تعلمت", "قبل سنوات", "قبل سنين", "زمان", "ابي", "امي", "والدي", "والدتي", "ابني", "بنتي", "زوجتي", "زوجي", "صار معي",
            "حصل معي", "وقع لي", "قصه", "حكايه", "فجاه", "وقتها", "في ذلك الوقت");

        /// <summary>Advice and lessons.</summary>
        public static readonly string[][] Advice = Split(
            "you should", "you need to", "you have to", "you must", "my advice", "the key is", "the secret is", "the trick is",
            "if you want", "make sure", "dont ever", "never ever", "the lesson", "the best way", "how to", "the way to", "remember that",
            "focus on", "dont be afraid", "start with", "try to", "the most important",
            "لازم", "يجب", "ينبغي", "نصيحتي", "انصحك", "انصحكم", "المهم", "الاهم", "السر هو", "الحل", "الطريقه", "اذا اردت", "اذا بغيت",
            "ان كنت تريد", "تاكد", "حاول", "حاولوا", "لا تنسى", "تذكر", "تذكروا", "لا تخاف", "ابدا ب", "خاصك", "خاصكم", "لازم تعرف");

        /// <summary>Words of feeling.</summary>
        public static readonly string[][] Feeling = Split(
            "love", "loved", "hate", "amazing", "beautiful", "incredible", "unbelievable", "scary", "scared", "afraid", "terrified", "funny",
            "hilarious", "sad", "cried", "crying", "tears", "angry", "proud", "happy", "excited", "exciting", "wow", "oh my god", "awesome",
            "wonderful", "fantastic", "shocking", "shocked", "heartbreaking", "emotional", "dream", "passion", "crazy", "insane", "wild",
            "breathtaking", "magical", "fun", "favorite", "favourite",
            "احب", "حب", "بحب", "اكره", "رائع", "رائعه", "جميل", "جميله", "مذهل", "مخيف", "خايف", "خوف", "مضحك", "حزين", "بكيت", "دموع",
            "فخور", "سعيد", "فرحان", "متحمس", "صدمه", "صدمت", "والله", "سبحان الله", "ما شاء الله", "يا الله", "عجيب", "حلم", "شغف",
            "مجنون", "خرافي", "روعه", "يا سلام", "ممتع", "زوين", "واعر", "تحفه");

        /// <summary>Words that point back at what was said before: the clip would leave its viewer behind.</summary>
        public static readonly string[][] BackReferences = Split(
            "as i said", "like i said", "as i mentioned", "like i mentioned", "as we discussed", "as we said", "like we said", "earlier",
            "the previous", "going back to", "like we talked about", "as i was saying", "i mentioned",
            "كما قلت", "كما ذكرت", "كما قلنا", "مثل ما قلت", "مثل ما قلنا", "زي ما قلت", "زي ما قلنا", "كما اشرت", "سابقا", "قبل قليل",
            "كما سبق", "كيف قلت", "كما اسلفت", "ذكرت سابقا");

        /// <summary>Words of quantity, beside written numbers.</summary>
        public static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
        {
            "percent", "million", "millions", "billion", "billions", "thousand", "thousands", "hundred", "hundreds", "dozen", "half", "twice",
            "%", "بالمئه", "بالمائه", "المئه", "مليون", "ملايين", "مليار", "الف", "الاف", "مئه", "مائه", "نصف", "ضعف", "ضعفين",
        };

        /// <summary>Words too common to tell one topic from another.</summary>
        public static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "or", "but", "so", "of", "to", "in", "on", "at", "for", "with", "by", "from", "about", "as", "into", "like",
            "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "have", "has", "had", "will", "would", "can", "could",
            "should", "may", "might", "must", "shall", "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them", "my",
            "your", "his", "its", "our", "their", "this", "that", "these", "those", "there", "here", "what", "which", "who", "whom", "when",
            "where", "why", "how", "all", "any", "some", "no", "not", "very", "just", "also", "then", "than", "too", "really", "yeah", "yes",
            "okay", "ok", "um", "uh", "well", "now", "get", "got", "go", "going", "know", "think", "thing", "things", "one", "lot", "kind",
            "sort", "dont", "im", "its", "thats", "youre", "theyre", "were", "weve", "ive", "id", "ill", "theres", "lets", "gonna", "wanna",
            "out", "up", "down", "over", "if", "because", "more", "much", "many", "say", "said", "see", "way", "even", "back", "right",
            "في", "من", "الى", "علي", "على", "عن", "مع", "هذا", "هذه", "ذلك", "تلك", "التي", "الذي", "الذين", "هو", "هي", "هم", "نحن",
            "انا", "انت", "انتم", "كان", "كانت", "يكون", "تكون", "ان", "او", "ثم", "لا", "ما", "لم", "لن", "قد", "كل", "بعض", "غير", "بين",
            "عند", "عندما", "اذا", "حتى", "كما", "يعني", "ايضا", "كذلك", "فقط", "هناك", "هنا", "اي", "شي", "شيء", "حاجه", "واحد", "بس",
            "لكن", "و", "ف", "ب", "ل", "به", "بها", "له", "لها", "فيه", "فيها", "منه", "منها", "عليه", "عليها", "اللي", "ديال", "دابا",
            "راه", "كاين", "كاينه", "باش", "غادي", "هاد", "هادي", "واش", "فين", "شنو", "مش", "مو", "كده", "هيك", "كتير", "كثير", "جدا",
        };
    }
}
