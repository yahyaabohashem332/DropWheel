namespace Petal.Core.Models;

/// <summary>A translation model made for one pair of languages.</summary>
/// <param name="Start">The padding id the decoder starts from.</param>
/// <param name="Mark">The target language's mark, for a model that knows several.</param>
public sealed record TranslationPair(string From, string To, IReadOnlyList<ModelInfo> Files, long Start, string? Mark);

/// <summary>
/// The translation models beyond Arabic and English (which are in <see cref="ModelCatalog"/>), each pinned to a commit
/// with its SHA-256 and exact size. Opus-MT by Helsinki-NLP (CC-BY 4.0), quantized ONNX by Xenova. Written by a script.
/// </summary>
public static class TranslationCatalog
{
    /// <summary>M2M100 418M (Meta, MIT): one model for every language here, slower than a model made for the pair.</summary>
    public static readonly ModelInfo[] Universal =
    [
        new("m2m100-encoder", "M2M100 encoder", "m2m100-418m-encoder-quantized.onnx", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/onnx/encoder_model_quantized.onnx", "13a94e354a9140764eb81102d77d3ec6952d796e6f113c651eeb3c3443da0386", 287856370, "MIT"),
        new("m2m100-decoder", "M2M100", "m2m100-418m-decoder-quantized.onnx", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/onnx/decoder_model_quantized.onnx", "6015e31c8976659aedb06058c4dadf0f400d087a3f9830f838e68f220d79bcb6", 339181945, "MIT"),
        new("m2m100-pieces", "M2M100 pieces", "m2m100-418m-sentencepiece.bpe.model", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/sentencepiece.bpe.model", "d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a", 2423393, "MIT"),
        new("m2m100-vocab", "M2M100 vocabulary", "m2m100-418m-vocab.json", "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/vocab.json", "b6e77e474aeea8f441363aca7614317c06381f3eacfe10fb9856d5081d1074cc", 3708092, "MIT"),
    ];

    private static readonly ModelInfo[] EnEs =
    [
        new("opus-mt-en-es-encoder", "Opus-MT English → Spanish encoder", "opus-mt-en-es-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/onnx/encoder_model_quantized.onnx", "03434edd1147e6c49e22066e9dc5888235e99cc35a6711d1c4ea5b06455d5da9", 52899742, "CC-BY-4.0"),
        new("opus-mt-en-es-decoder", "Opus-MT English → Spanish", "opus-mt-en-es-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/onnx/decoder_model_quantized.onnx", "2b45074cc819f1d901e40510d7bc0781d99327d16c7eca04518c4ee0c3d6ba10", 59842102, "CC-BY-4.0"),
        new("opus-mt-en-es-source", "English pieces", "opus-mt-en-es-source.spm", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/source.spm", "4dd547c24816a335e7b0b2e63376a8f1b3cbfc671eda5ab808dd44fdadaa8791", 801636, "CC-BY-4.0"),
        new("opus-mt-en-es-vocab", "Opus-MT English → Spanish vocabulary", "opus-mt-en-es-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-es/resolve/4b002a4c7edd54a7ced58877258b87f7efd3f892/vocab.json", "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163", 1720044, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] EnFr =
    [
        new("opus-mt-en-fr-encoder", "Opus-MT English → French encoder", "opus-mt-en-fr-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/onnx/encoder_model_quantized.onnx", "0a81bdba62f53223740a8c6c6f58e716eba8ea1f92dfa569caf27ed5e3b6a0f7", 50090398, "CC-BY-4.0"),
        new("opus-mt-en-fr-decoder", "Opus-MT English → French", "opus-mt-en-fr-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/onnx/decoder_model_quantized.onnx", "bdacc5f38e4d2b805a2fc9f38cf43844cdea56666e735137dbacf2a54a785257", 57010810, "CC-BY-4.0"),
        new("opus-mt-en-fr-source", "English pieces", "opus-mt-en-fr-source.spm", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/source.spm", "173e9f493a668fe396d599e28d414a201193094e6ffd7a4678e5aab0f6d3d838", 778395, "CC-BY-4.0"),
        new("opus-mt-en-fr-vocab", "Opus-MT English → French vocabulary", "opus-mt-en-fr-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-fr/resolve/28726206f80896b90035bd99cccd5cc1e151f916/vocab.json", "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d", 1458196, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] EnId =
    [
        new("opus-mt-en-id-encoder", "Opus-MT English → Indonesian encoder", "opus-mt-en-id-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/onnx/encoder_model_quantized.onnx", "9bfc9cf0f400f86ec11da24b2f56380fcfabab17fb2dbcf7bb4c1754b7076e24", 47674781, "CC-BY-4.0"),
        new("opus-mt-en-id-decoder", "Opus-MT English → Indonesian", "opus-mt-en-id-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/onnx/decoder_model_quantized.onnx", "d46dea5d13bec5ed7552baa5c0917e0d3dfa32e14fb7f09eae534c50d0cfb42f", 54576321, "CC-BY-4.0"),
        new("opus-mt-en-id-source", "English pieces", "opus-mt-en-id-source.spm", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/source.spm", "e88300911c2c573ec5526777a1e84bae698d20925b82dcef9c7248bb0e537ed0", 795925, "CC-BY-4.0"),
        new("opus-mt-en-id-vocab", "Opus-MT English → Indonesian vocabulary", "opus-mt-en-id-vocab.json", "https://huggingface.co/Xenova/opus-mt-en-id/resolve/b3b41f654c2fb3286d007dee1da7b0f47fd27d82/vocab.json", "cda834905a5d478c54a73abc98cc9155dc58217baa130cdf165411265b760d6b", 1366984, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] EsEn =
    [
        new("opus-mt-es-en-encoder", "Opus-MT Spanish → English encoder", "opus-mt-es-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/onnx/encoder_model_quantized.onnx", "c01e70f8455efc350831aa2af7ed187b16829241f0018ba07d1ba643a391bc18", 52899742, "CC-BY-4.0"),
        new("opus-mt-es-en-decoder", "Opus-MT Spanish → English", "opus-mt-es-en-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/onnx/decoder_model_quantized.onnx", "d518f1f0041b2831f0aeb5a087cf899742cd9d4b574f6b98ad9f816b8c06925b", 59842102, "CC-BY-4.0"),
        new("opus-mt-es-en-source", "Spanish pieces", "opus-mt-es-en-source.spm", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/source.spm", "e236ee6d866b635c0142114f8647f39831f9d92534aa2aad75c942f6a78ad0e3", 825924, "CC-BY-4.0"),
        new("opus-mt-es-en-vocab", "Opus-MT Spanish → English vocabulary", "opus-mt-es-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-es-en/resolve/eadfd7c658a9d8929ac3b8e996b68a68e2c7d480/vocab.json", "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163", 1720044, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] FrEn =
    [
        new("opus-mt-fr-en-encoder", "Opus-MT French → English encoder", "opus-mt-fr-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/onnx/encoder_model_quantized.onnx", "e727cb26ac6bf816394c49671af69c9ae5798868fed1e40849872415dffc1772", 50090398, "CC-BY-4.0"),
        new("opus-mt-fr-en-decoder", "Opus-MT French → English", "opus-mt-fr-en-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/onnx/decoder_model_quantized.onnx", "8c5a0c5d113a1c4c5ae46e014aacf770f3bff2109fe8b74ead6aae5c5f408ffc", 57010810, "CC-BY-4.0"),
        new("opus-mt-fr-en-source", "French pieces", "opus-mt-fr-en-source.spm", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/source.spm", "78d0e717c77053f1c4b856d8661d9cb87c64f083a35418c087b9146300e4f585", 802397, "CC-BY-4.0"),
        new("opus-mt-fr-en-vocab", "Opus-MT French → English vocabulary", "opus-mt-fr-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-fr-en/resolve/6b166a182780e118c997879d0ad5be4b53671644/vocab.json", "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d", 1458196, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] IdEn =
    [
        new("opus-mt-id-en-encoder", "Opus-MT Indonesian → English encoder", "opus-mt-id-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/onnx/encoder_model_quantized.onnx", "c5ab889ecf81e120e53f0f39800c0609d8d77b6c4a32212d73a85e3cfeb66293", 47674782, "CC-BY-4.0"),
        new("opus-mt-id-en-decoder", "Opus-MT Indonesian → English", "opus-mt-id-en-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/onnx/decoder_model_quantized.onnx", "e72a8b1062477950b6464b1fcfb37a99c196618e5bb67222451472ea52e98090", 54576322, "CC-BY-4.0"),
        new("opus-mt-id-en-source", "Indonesian pieces", "opus-mt-id-en-source.spm", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/source.spm", "2a8fefe71c7f26cb0c6aa1b9f0cc0f8d18006b20fe41c547af7f25b9c8333465", 800687, "CC-BY-4.0"),
        new("opus-mt-id-en-vocab", "Opus-MT Indonesian → English vocabulary", "opus-mt-id-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-id-en/resolve/c38ef36c843b71177da903442835c3c870975fd6/vocab.json", "cda834905a5d478c54a73abc98cc9155dc58217baa130cdf165411265b760d6b", 1366984, "CC-BY-4.0"),
    ];

    private static readonly ModelInfo[] TrEn =
    [
        new("opus-mt-tr-en-encoder", "Opus-MT Turkish → English encoder", "opus-mt-tr-en-encoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-tr-en/resolve/78247f41f354af036d24e6f0f8a3d02c6806f96e/onnx/encoder_model_quantized.onnx", "b9b73903b99de7303f3c465b335a9cb695ba6c9c93ef527064f5fe2cf7a3235f", 51562398, "CC-BY-4.0"),
        new("opus-mt-tr-en-decoder", "Opus-MT Turkish → English", "opus-mt-tr-en-decoder-quantized.onnx", "https://huggingface.co/Xenova/opus-mt-tr-en/resolve/78247f41f354af036d24e6f0f8a3d02c6806f96e/onnx/decoder_model_quantized.onnx", "70a8a968a4fe8cbf75733fb287957e53c204f17513ff7445e5a68e66874f1ec4", 58494310, "CC-BY-4.0"),
        new("opus-mt-tr-en-source", "Turkish pieces", "opus-mt-tr-en-source.spm", "https://huggingface.co/Xenova/opus-mt-tr-en/resolve/78247f41f354af036d24e6f0f8a3d02c6806f96e/source.spm", "4a6356e3ee892f2896dbf0b5cabeb51fdf16e03c70b0702cefc8b847e504241c", 839750, "CC-BY-4.0"),
        new("opus-mt-tr-en-vocab", "Opus-MT Turkish → English vocabulary", "opus-mt-tr-en-vocab.json", "https://huggingface.co/Xenova/opus-mt-tr-en/resolve/78247f41f354af036d24e6f0f8a3d02c6806f96e/vocab.json", "74f0a0f39956b77598d9654201cb1a178bd421360a4c5d87805089320de0677b", 1688744, "CC-BY-4.0"),
    ];

    public static readonly TranslationPair[] Pairs =
    [
        new("en", "es", EnEs, 65000, null),
        new("en", "fr", EnFr, 59513, null),
        new("en", "id", EnId, 54795, null),
        new("es", "en", EsEn, 65000, null),
        new("fr", "en", FrEn, 59513, null),
        new("id", "en", IdEn, 54795, null),
        new("tr", "en", TrEn, 62388, null),
    ];
}
