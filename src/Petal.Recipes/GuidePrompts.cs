namespace Petal.Recipes;

/// <summary>
/// What the Tools Guide tells the AI model, and the little words its search leaves out, in the languages Petal
/// speaks. The model is told to use only the numbered notes it is given (the guide's own entries).
/// </summary>
public static class GuidePrompts
{
    /// <summary>Between a note in English and the same note in the language Petal speaks ({0}: "Arabic").</summary>
    public const string SameNoteIn = "The same note in {0}:";

    /// <summary>Before the user's words, after the notes.</summary>
    public const string RequestLabel = "Request: ";

    /// <summary>Which notes' tools do what the user wants to do: their numbers only, which can be checked.</summary>
    public const string Choose =
        "You choose tools for the user of DropWheel, an app for Windows. The user's message has numbered notes about DropWheel's tools, then the user's " +
        "request. Reply with only the numbers of the notes whose tool does what the request asks, the best first, separated by commas, at most three, " +
        "for example: 2, 1. If none of them does it, reply with: 0";

    /// <summary>How to use the one tool chosen, for the request, from its note alone ({0}: the language to reply in).</summary>
    public const string Tip =
        "You are the guide inside DropWheel, an app for Windows. The user's message has a note about one of DropWheel's tools, then the user's request. " +
        "In one to three short sentences, give the steps to use this tool for the request: the keys, the wheel, the petal and the option to choose, " +
        "with the names as the note writes them. Use only what the note says; don't describe anything the note doesn't. Reply in {0}.";

    /// <summary>A question about one tool, from its note alone ({0}: the language to reply in).</summary>
    public const string About =
        "You are the guide inside DropWheel, an app for Windows. The user's message has a note about one of DropWheel's tools, then a question about it. " +
        "Answer the question in one to four short sentences, with steps when it asks how, using only what the note says. " +
        "If the note doesn't answer it, say that the guide doesn't say. Reply in {0}.";

    /// <summary>Words left out when a request is compared with a keyword of several words ("this", "the", "a"), by language.</summary>
    public static readonly IReadOnlyList<string> FillerWords =
    [
        "this that the a an my your",
        "هذه هذا ذلك تلك",
        "این آن یک یه",
        "یہ وہ ایک",
        "bu şu bir",
        "ce cet cette ces le la les un une mon ma mes",
        "este esta ese esa el la los las un una mi mis",
        "ini itu sebuah",
    ];

    /// <summary>Little words that say nothing about the task, by language, separated by spaces.</summary>
    public static readonly IReadOnlyList<string> StopWords =
    [
        "i want need would like a an the to for of in on at with and or do does how can could should what which that this my me it is are be some something any way tool tools app thing please use using get help",
        "انا اريد اود احتاج اداه ادوات اداة ادوات تقوم يقوم بمهمه مهمه شي شيء طريقه كيف ممكن استطيع يمكنني لي في من على الى عن مع او و هل ما ماذا التي الذي هذا هذه ذلك عندي لدي برنامج",
        "من می خواهم خواهم ابزار که را برای یک با به در از این آن چطور چگونه",
        "مجهے مجھے چاہیے چاہئے ٹول کے کی کا ایک سے میں کو اور کیسے",
        "bir için istiyorum araç ve ile bu şu nasıl ben bana",
        "je veux voudrais un une outil pour qui le la les de des du et ou avec comment",
        "quiero una un herramienta para que el la los las de del y o con como",
        "saya ingin mau alat yang untuk sebuah dan atau dengan bagaimana",
    ];
}
