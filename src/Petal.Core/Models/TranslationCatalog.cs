namespace Petal.Core.Models;

/// <summary>A translation model made for one pair of languages.</summary>
/// <param name="Start">The padding id the decoder starts from.</param>
/// <param name="Mark">The target language's mark, for a model that knows several.</param>
/// <param name="End">The id a text and an answer end with.</param>
public sealed record TranslationPair(string From, string To, IReadOnlyList<ModelInfo> Files, long Start, string? Mark, long End = 0);

/// <summary>
/// The small translation models, each file pinned to a commit with its SHA-256 and exact size: Opus-MT by Helsinki-NLP
/// (CC-BY 4.0), one model for a pair of languages, as quantized ONNX by Xenova and by the ONNX Community; and M2M100 by
/// Meta (MIT), one model for every pair, for the languages Opus-MT does poorly. Their decoders are the "merged" ones,
/// which carry on from what they worked out for the words before: what lets a processor alone translate captions as
/// fast as they are said. Written by a script.
/// </summary>
public static class TranslationCatalog
{
    /// <summary>M2M100 418M (Meta, MIT): one model for every language here, slower than a model made for the pair.</summary>
    public static readonly ModelInfo[] Universal =
    [
        new("m2m100-encoder", "M2M100", "m2m100-418m-encoder-quantized.onnx", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/onnx/encoder_model_quantized.onnx", "13a94e354a9140764eb81102d77d3ec6952d796e6f113c651eeb3c3443da0386", 287856370, "MIT"),
        new("m2m100-decoder", "M2M100", "m2m100-418m-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/onnx/decoder_model_merged_quantized.onnx", "007654bcabb6cea6fd3bde34ce933137b431330b3755781145d7b6906270b45a", 344128178, "MIT", Replaces: "m2m100-418m-decoder-quantized.onnx"),
        new("m2m100-pieces", "M2M100", "m2m100-418m-sentencepiece.bpe.model", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/sentencepiece.bpe.model", "d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a", 2423393, "MIT"),
        new("m2m100-vocab", "M2M100", "m2m100-418m-vocab.json", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/vocab.json", "b6e77e474aeea8f441363aca7614317c06381f3eacfe10fb9856d5081d1074cc", 3708092, "MIT"),
    ];

    // English into Arabic: Xenova/opus-mt-en-ar.
    private static readonly ModelInfo[] EnAr =
    [
        ModelCatalog.OpusEnArEncoder,
        new("opus-mt-en-ar-decoder-merged", "Opus-MT", "opus-mt-en-ar-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/onnx/decoder_model_merged_quantized.onnx", "c0e553b0c26611780c70417c9b4c1ca7169e9cdb18ba9e649197efb017af9f02", 59078120, "CC-BY-4.0"),
        ModelCatalog.OpusEnArSource,
        ModelCatalog.OpusEnArVocabulary,
    ];

    // Arabic into English: Xenova/opus-mt-ar-en.
    private static readonly ModelInfo[] ArEn =
    [
        ModelCatalog.OpusArEnEncoder,
        new("opus-mt-ar-en-decoder-merged", "Opus-MT", "opus-mt-ar-en-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-ar-en/resolve/09c7556866400dcc53562ba8d7035119e7d9a2c1/onnx/decoder_model_merged_quantized.onnx", "cdb25ae736afe9d48035b0ea311ab4b25f315810fab7f3c6f6cb0d8d87af35fa", 59094632, "CC-BY-4.0"),
        ModelCatalog.OpusArEnSource,
        ModelCatalog.OpusArEnVocabulary,
    ];

    // English into Turkish: onnx-community/opus-mt-tc-big-en-tr.
    private static readonly ModelInfo[] EnTr =
    [
        new("opus-mt-tc-big-en-tr-encoder", "Opus-MT", "opus-mt-tc-big-en-tr-encoder-quantized.onnx", "https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/ce128ea834a090aca240e4e113e7756e06cc78e0/onnx/encoder_model_quantized.onnx", "58bfc076347346049a52a8d53b1f6e4664af74e9737aded374d7d1f78805f46e", 135730164, "CC-BY-4.0"),
        new("opus-mt-tc-big-en-tr-decoder", "Opus-MT", "opus-mt-tc-big-en-tr-decoder-merged-quantized.onnx", "https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/ce128ea834a090aca240e4e113e7756e06cc78e0/onnx/decoder_model_merged_quantized.onnx", "23223bce303d925b119ec905b9422a2814e7ad5f96275407be1d387b13879147", 395716769, "CC-BY-4.0"),
        new("opus-mt-tc-big-en-tr-source", "Opus-MT", "opus-mt-tc-big-en-tr-source.spm", "https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/ce128ea834a090aca240e4e113e7756e06cc78e0/source.spm", "180d3f94ddfee5585d78b702290ded0f4f9bd40f299349c8383b32a579fefad4", 796953, "CC-BY-4.0"),
        new("opus-mt-tc-big-en-tr-vocab", "Opus-MT", "opus-mt-tc-big-en-tr-vocab.json", "https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/ce128ea834a090aca240e4e113e7756e06cc78e0/vocab.json", "01ef522455d2bf22b0716416ee34688cc2b3f5fa257f742665d21b6acc450ac1", 1501287, "CC-BY-4.0"),
    ];

    // Turkish into English: onnx-community/opus-mt-tc-big-tr-en.
    private static readonly ModelInfo[] TrEn =
    [
        new("opus-mt-tc-big-tr-en-encoder", "Opus-MT", "opus-mt-tc-big-tr-en-encoder-quantized.onnx", "https://huggingface.co/onnx-community/opus-mt-tc-big-tr-en/resolve/251254164dc7bc65d705fb9ef3ac47622a29335e/onnx/encoder_model_quantized.onnx", "fd5b022e1051535852f0478193f6cc092d6155196d8cc9d7929930fdc913f0ea", 135730164, "CC-BY-4.0", Replaces: "opus-mt-tr-en-encoder-quantized.onnx"),
        new("opus-mt-tc-big-tr-en-decoder", "Opus-MT", "opus-mt-tc-big-tr-en-decoder-merged-quantized.onnx", "https://huggingface.co/onnx-community/opus-mt-tc-big-tr-en/resolve/251254164dc7bc65d705fb9ef3ac47622a29335e/onnx/decoder_model_merged_quantized.onnx", "8dbaf327a449a81d937a720d1aac0892e7e086d3d99089939883b759cddb28d8", 395716769, "CC-BY-4.0", Replaces: "opus-mt-tr-en-decoder-quantized.onnx"),
        new("opus-mt-tc-big-tr-en-source", "Opus-MT", "opus-mt-tc-big-tr-en-source.spm", "https://huggingface.co/onnx-community/opus-mt-tc-big-tr-en/resolve/251254164dc7bc65d705fb9ef3ac47622a29335e/source.spm", "a6946a00a5cbd04044bb1c25b48ea798561a2fd12d872e5d1452bdc3dc076e77", 833378, "CC-BY-4.0", Replaces: "opus-mt-tr-en-source.spm"),
        new("opus-mt-tc-big-tr-en-vocab", "Opus-MT", "opus-mt-tc-big-tr-en-vocab.json", "https://huggingface.co/onnx-community/opus-mt-tc-big-tr-en/resolve/251254164dc7bc65d705fb9ef3ac47622a29335e/vocab.json", "01ef522455d2bf22b0716416ee34688cc2b3f5fa257f742665d21b6acc450ac1", 1501287, "CC-BY-4.0", Replaces: "opus-mt-tr-en-vocab.json"),
    ];

    // English into Indonesian: Xenova/opus-mt-en-id.
    private static readonly ModelInfo[] EnId =
    [
        new("opus-mt-en-id-encoder", "Opus-MT", "opus-mt-en-id-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/onnx/encoder_model_quantized.onnx", "9bfc9cf0f400f86ec11da24b2f56380fcfabab17fb2dbcf7bb4c1754b7076e24", 47674781, "CC-BY-4.0"),
        new("opus-mt-en-id-decoder", "Opus-MT", "opus-mt-en-id-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/onnx/decoder_model_merged_quantized.onnx", "0a59ebfe85137a21055d9f73d9f83a59487f0cff31db452c7db3b6cdeca8e7c2", 54947023, "CC-BY-4.0", Replaces: "opus-mt-en-id-decoder-quantized.onnx"),
        new("opus-mt-en-id-source", "Opus-MT", "opus-mt-en-id-source.spm", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/source.spm", "e88300911c2c573ec5526777a1e84bae698d20925b82dcef9c7248bb0e537ed0", 795925, "CC-BY-4.0"),
        new("opus-mt-en-id-vocab", "Opus-MT", "opus-mt-en-id-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/vocab.json", "cda834905a5d478c54a73abc98cc9155dc58217baa130cdf165411265b760d6b", 1366984, "CC-BY-4.0"),
    ];

    // Indonesian into English: Xenova/opus-mt-id-en.
    private static readonly ModelInfo[] IdEn =
    [
        new("opus-mt-id-en-encoder", "Opus-MT", "opus-mt-id-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/onnx/encoder_model_quantized.onnx", "c5ab889ecf81e120e53f0f39800c0609d8d77b6c4a32212d73a85e3cfeb66293", 47674782, "CC-BY-4.0"),
        new("opus-mt-id-en-decoder", "Opus-MT", "opus-mt-id-en-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/onnx/decoder_model_merged_quantized.onnx", "d65eeab370c2873bdf22a7b2b311f0f636aa693b8edd55c60b6b1db8473268dd", 54947024, "CC-BY-4.0", Replaces: "opus-mt-id-en-decoder-quantized.onnx"),
        new("opus-mt-id-en-source", "Opus-MT", "opus-mt-id-en-source.spm", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/source.spm", "2a8fefe71c7f26cb0c6aa1b9f0cc0f8d18006b20fe41c547af7f25b9c8333465", 800687, "CC-BY-4.0"),
        new("opus-mt-id-en-vocab", "Opus-MT", "opus-mt-id-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/vocab.json", "cda834905a5d478c54a73abc98cc9155dc58217baa130cdf165411265b760d6b", 1366984, "CC-BY-4.0"),
    ];

    // English into French: Xenova/opus-mt-en-fr.
    private static readonly ModelInfo[] EnFr =
    [
        new("opus-mt-en-fr-encoder", "Opus-MT", "opus-mt-en-fr-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/onnx/encoder_model_quantized.onnx", "0a81bdba62f53223740a8c6c6f58e716eba8ea1f92dfa569caf27ed5e3b6a0f7", 50090398, "CC-BY-4.0"),
        new("opus-mt-en-fr-decoder", "Opus-MT", "opus-mt-en-fr-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/onnx/decoder_model_merged_quantized.onnx", "333b244bce16023df04541c8cf9fd60aec9b0569da393c4b831d561897b0bda8", 57381512, "CC-BY-4.0", Replaces: "opus-mt-en-fr-decoder-quantized.onnx"),
        new("opus-mt-en-fr-source", "Opus-MT", "opus-mt-en-fr-source.spm", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/source.spm", "173e9f493a668fe396d599e28d414a201193094e6ffd7a4678e5aab0f6d3d838", 778395, "CC-BY-4.0"),
        new("opus-mt-en-fr-vocab", "Opus-MT", "opus-mt-en-fr-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/vocab.json", "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d", 1458196, "CC-BY-4.0"),
    ];

    // French into English: Xenova/opus-mt-fr-en.
    private static readonly ModelInfo[] FrEn =
    [
        new("opus-mt-fr-en-encoder", "Opus-MT", "opus-mt-fr-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/onnx/encoder_model_quantized.onnx", "e727cb26ac6bf816394c49671af69c9ae5798868fed1e40849872415dffc1772", 50090398, "CC-BY-4.0"),
        new("opus-mt-fr-en-decoder", "Opus-MT", "opus-mt-fr-en-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/onnx/decoder_model_merged_quantized.onnx", "73bc7ac8e29c42e6f212ebcc29a2991d3646a04aabb642c0036aa54b40e4e1a9", 57381512, "CC-BY-4.0", Replaces: "opus-mt-fr-en-decoder-quantized.onnx"),
        new("opus-mt-fr-en-source", "Opus-MT", "opus-mt-fr-en-source.spm", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/source.spm", "78d0e717c77053f1c4b856d8661d9cb87c64f083a35418c087b9146300e4f585", 802397, "CC-BY-4.0"),
        new("opus-mt-fr-en-vocab", "Opus-MT", "opus-mt-fr-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/vocab.json", "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d", 1458196, "CC-BY-4.0"),
    ];

    // English into Spanish: Xenova/opus-mt-en-es.
    private static readonly ModelInfo[] EnEs =
    [
        new("opus-mt-en-es-encoder", "Opus-MT", "opus-mt-en-es-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/onnx/encoder_model_quantized.onnx", "03434edd1147e6c49e22066e9dc5888235e99cc35a6711d1c4ea5b06455d5da9", 52899742, "CC-BY-4.0"),
        new("opus-mt-en-es-decoder", "Opus-MT", "opus-mt-en-es-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/onnx/decoder_model_merged_quantized.onnx", "58ee5ddd6e22d1693d8722b90c1486afb93700a4376cf28fd74175052ad530ae", 60212804, "CC-BY-4.0", Replaces: "opus-mt-en-es-decoder-quantized.onnx"),
        new("opus-mt-en-es-source", "Opus-MT", "opus-mt-en-es-source.spm", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/source.spm", "4dd547c24816a335e7b0b2e63376a8f1b3cbfc671eda5ab808dd44fdadaa8791", 801636, "CC-BY-4.0"),
        new("opus-mt-en-es-vocab", "Opus-MT", "opus-mt-en-es-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/vocab.json", "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163", 1720044, "CC-BY-4.0"),
    ];

    // Spanish into English: Xenova/opus-mt-es-en.
    private static readonly ModelInfo[] EsEn =
    [
        new("opus-mt-es-en-encoder", "Opus-MT", "opus-mt-es-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/onnx/encoder_model_quantized.onnx", "c01e70f8455efc350831aa2af7ed187b16829241f0018ba07d1ba643a391bc18", 52899742, "CC-BY-4.0"),
        new("opus-mt-es-en-decoder", "Opus-MT", "opus-mt-es-en-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/onnx/decoder_model_merged_quantized.onnx", "4cd91ab30240d295ce907b5100826031838c672005e57e524d711122d75605fa", 60212804, "CC-BY-4.0", Replaces: "opus-mt-es-en-decoder-quantized.onnx"),
        new("opus-mt-es-en-source", "Opus-MT", "opus-mt-es-en-source.spm", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/source.spm", "e236ee6d866b635c0142114f8647f39831f9d92534aa2aad75c942f6a78ad0e3", 825924, "CC-BY-4.0"),
        new("opus-mt-es-en-vocab", "Opus-MT", "opus-mt-es-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/vocab.json", "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163", 1720044, "CC-BY-4.0"),
    ];

    // Spanish into French: Xenova/opus-mt-es-fr.
    private static readonly ModelInfo[] EsFr =
    [
        new("opus-mt-es-fr-encoder", "Opus-MT", "opus-mt-es-fr-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-fr/resolve/3bef5f612825d503f6fb85e0527a1fe30f2578c2/onnx/encoder_model_quantized.onnx", "f2c497ebe1c3eadb329b8f36b250e7f87f9a327f46615dc5637492702cd0d2af", 57928094, "CC-BY-4.0"),
        new("opus-mt-es-fr-decoder", "Opus-MT", "opus-mt-es-fr-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-fr/resolve/3bef5f612825d503f6fb85e0527a1fe30f2578c2/onnx/decoder_model_merged_quantized.onnx", "f6e43796a68566c95ab387277c223726ac988b307e875c239b340424eb057a92", 65280440, "CC-BY-4.0"),
        new("opus-mt-es-fr-source", "Opus-MT", "opus-mt-es-fr-source.spm", "https://huggingface.co/Xenova/opus-mt-es-fr/resolve/3bef5f612825d503f6fb85e0527a1fe30f2578c2/source.spm", "0807c7b3dabe387a083700920a4b406c7656db09922bf934c25c2fab24706c4d", 818689, "CC-BY-4.0"),
        new("opus-mt-es-fr-vocab", "Opus-MT", "opus-mt-es-fr-vocab.json", "https://huggingface.co/Xenova/opus-mt-es-fr/resolve/3bef5f612825d503f6fb85e0527a1fe30f2578c2/vocab.json", "00298f6b835a284fc55779eb3af674807e71984169682d5156ed92184b3b0f0a", 2487351, "CC-BY-4.0"),
    ];

    // French into Spanish: Xenova/opus-mt-fr-es.
    private static readonly ModelInfo[] FrEs =
    [
        new("opus-mt-fr-es-encoder", "Opus-MT", "opus-mt-fr-es-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-es/resolve/962338be51033b548c13dbb6f4da3ddb8c8aba0e/onnx/encoder_model_quantized.onnx", "946954149b0179a0fb60145a3ed85da2f58868ef2e6a442a1cb4301563b70ac3", 57928094, "CC-BY-4.0"),
        new("opus-mt-fr-es-decoder", "Opus-MT", "opus-mt-fr-es-decoder-merged-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-es/resolve/962338be51033b548c13dbb6f4da3ddb8c8aba0e/onnx/decoder_model_merged_quantized.onnx", "ff991f24d534a62db615697ed9555db80be28fbf9e5677d8ebb1f78c6aabc2c6", 65280440, "CC-BY-4.0"),
        new("opus-mt-fr-es-source", "Opus-MT", "opus-mt-fr-es-source.spm", "https://huggingface.co/Xenova/opus-mt-fr-es/resolve/962338be51033b548c13dbb6f4da3ddb8c8aba0e/source.spm", "74ffc930c4af87e3bdec6dde479eb36f05152e1a657ec65dfb923baf4b5d27a9", 811963, "CC-BY-4.0"),
        new("opus-mt-fr-es-vocab", "Opus-MT", "opus-mt-fr-es-vocab.json", "https://huggingface.co/Xenova/opus-mt-fr-es/resolve/962338be51033b548c13dbb6f4da3ddb8c8aba0e/vocab.json", "00298f6b835a284fc55779eb3af674807e71984169682d5156ed92184b3b0f0a", 2487351, "CC-BY-4.0"),
    ];

    public static readonly TranslationPair[] Pairs =
    [
        new("en", "ar", EnAr, 62801, ">>ara<<"),
        new("ar", "en", ArEn, 62833, null),
        new("en", "tr", EnTr, 57059, null, 43741),
        new("tr", "en", TrEn, 57059, null, 43741),
        new("en", "id", EnId, 54795, null),
        new("id", "en", IdEn, 54795, null),
        new("en", "fr", EnFr, 59513, null),
        new("fr", "en", FrEn, 59513, null),
        new("en", "es", EnEs, 65000, null),
        new("es", "en", EsEn, 65000, null),
        new("es", "fr", EsFr, 74821, null),
        new("fr", "es", FrEs, 74821, null),
    ];

    /// <summary>Every file here that isn't in <see cref="ModelCatalog"/> too.</summary>
    public static IReadOnlyList<ModelInfo> All { get; } = [.. Universal, .. Pairs.SelectMany(p => p.Files).Where(f => !ModelCatalog.All.Contains(f))];
}
