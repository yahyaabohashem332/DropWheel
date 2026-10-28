using System.Security.Cryptography;

namespace Petal.Core.Models;

/// <summary>
/// An open AI model Petal runs locally. Bundled models ship in the app's tools\models folder;
/// the others are downloaded once, when a tool first needs them, and checked against
/// <see cref="Sha256"/> before use.
/// </summary>
/// <param name="FileName">The model's file (for a bundled model, relative to tools\models).</param>
public sealed record ModelInfo(string Id, string Name, string FileName, string Url, string Sha256, long Size, string License, bool Bundled = false);

public static class ModelCatalog
{
    /// <summary>BiRefNet lite (MIT): cuts the subject out of a photo, edges and hair included.</summary>
    public static readonly ModelInfo Background = new(
        "birefnet-lite", "BiRefNet lite", "birefnet-lite.onnx",
        "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx",
        "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", 224_005_088, "MIT");

    /// <summary>Real-ESRGAN general x4v3 (BSD-3-Clause), Qualcomm's ONNX export: 4× enlargement. Ships with Petal.</summary>
    public static readonly ModelInfo Upscaler = new(
        "realesrgan-x4v3", "Real-ESRGAN general x4v3", @"realesrgan-x4v3\real_esrgan_general_x4v3.onnx",
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_general_x4v3/releases/v0.64.0/real_esrgan_general_x4v3-onnx-float.zip",
        "fc094329b24a00c936ebc1cc4c51bc59633e7dc36f4a553344342171cbd3c7cf", 4_540_895, "BSD-3-Clause", Bundled: true);

    /// <summary>Whisper base, 5-bit (MIT): quick, best for clear English.</summary>
    public static readonly ModelInfo WhisperFast = new(
        "whisper-base", "Whisper base", "ggml-base-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin",
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", 59_707_625, "MIT");

    /// <summary>Whisper small, 5-bit (MIT): a good balance for most languages.</summary>
    public static readonly ModelInfo WhisperBalanced = new(
        "whisper-small", "Whisper small", "ggml-small-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487, "MIT");

    /// <summary>Whisper large-v3 turbo, 5-bit (MIT): the most accurate, Arabic included.</summary>
    public static readonly ModelInfo WhisperAccurate = new(
        "whisper-large-v3-turbo", "Whisper large-v3 turbo", "ggml-large-v3-turbo-q5_0.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195, "MIT");

    /// <summary>Piper 2023.11.14-2 (MIT): the program that speaks with the natural voices, Arabic diacritics model included.</summary>
    public static readonly ModelInfo Piper = new(
        "piper", "Piper", "piper_windows_amd64.zip",
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
        "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea", 22_477_236, "MIT");

    /// <summary>Piper's Kareem (piper-voices, MIT): a natural Arabic voice.</summary>
    public static readonly ModelInfo VoiceKareem = new(
        "piper-ar-kareem", "Kareem (Arabic voice)", "ar_JO-kareem-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ar/ar_JO/kareem/medium/ar_JO-kareem-medium.onnx",
        "9e95cab07b679da603bba17c4dec7ab3111320571964ee95c0379603c086491e", 63_201_294, "MIT");

    public static readonly ModelInfo VoiceKareemSettings = new(
        "piper-ar-kareem-config", "Kareem settings", "ar_JO-kareem-medium.onnx.json",
        "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ar/ar_JO/kareem/medium/ar_JO-kareem-medium.onnx.json",
        "ea6d9b9d9076dbdb6bf5c98c6a141ef154959d2359709b37855727964e7d6c4d", 5_024, "MIT");

    /// <summary>Piper's Joe (piper-voices, MIT; recordings CC0): a natural English voice.</summary>
    public static readonly ModelInfo VoiceJoe = new(
        "piper-en-joe", "Joe (English voice)", "en_US-joe-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/joe/medium/en_US-joe-medium.onnx",
        "58afce0321b8d9c46d7cdf9c16500cc55a793b4220212dba6b70fb788b3baf06", 63_201_294, "MIT");

    public static readonly ModelInfo VoiceJoeSettings = new(
        "piper-en-joe-config", "Joe settings", "en_US-joe-medium.onnx.json",
        "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/joe/medium/en_US-joe-medium.onnx.json",
        "3d6d5410b3795cb1950595247ef8f06190719e6fdbfa3a2356d8ec368e1aad33", 4_794, "MIT");

    /// <summary>llama.cpp b11763, CPU build (MIT): runs the language model below on any PC; the ARM64 build on ARM64 PCs.</summary>
    public static readonly ModelInfo LlamaCpp = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        ? new("llama-cpp", "llama.cpp", "llama-b11763-bin-win-cpu-arm64.zip",
            "https://github.com/ggml-org/llama.cpp/releases/download/b11763/llama-b11763-bin-win-cpu-arm64.zip",
            "606bfaf95260114152102cafbbdb70318339b01c05a9c63d6e1d040d9cb2bdda", 12_596_851, "MIT")
        : new("llama-cpp", "llama.cpp", "llama-b11763-bin-win-cpu-x64.zip",
            "https://github.com/ggml-org/llama.cpp/releases/download/b11763/llama-b11763-bin-win-cpu-x64.zip",
            "a24504506cd03a352f4990c5f3cbff347eff3a2cf7ec15038835aa690b73d0ad", 23_958_528, "MIT");

    /// <summary>Qwen2.5 1.5B Instruct, 4-bit (Apache-2.0): understands requests in English and Arabic for Ask Petal.</summary>
    public static readonly ModelInfo Qwen = new(
        "qwen2.5-1.5b", "Qwen2.5 1.5B", "qwen2.5-1.5b-instruct-q4_k_m.gguf",
        "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
        "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", 1_117_320_736, "Apache-2.0");

    /// <summary>
    /// Qwen3 4B Instruct (2507), 4-bit (Apache-2.0; Unsloth's GGUF at a fixed revision): answers questions about a file from
    /// its own words, in Arabic and English, for Ask a File. Run by llama.cpp.
    /// </summary>
    public static readonly ModelInfo QwenAnswers = new(
        "qwen3-4b-instruct", "Qwen3 4B", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
        "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/a06e946bb6b655725eafa393f4a9745d460374c9/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
        "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597", 2_497_281_120, "Apache-2.0");

    /// <summary>
    /// multilingual-e5-small (intfloat, MIT), Xenova's 8-bit ONNX at a fixed revision: finds the passages of a file that
    /// speak to a question by what they mean, in about a hundred languages and across them.
    /// </summary>
    public static readonly ModelInfo Passages = new(
        "multilingual-e5-small", "multilingual-e5-small", "multilingual-e5-small-int8.onnx",
        "https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78/onnx/model_int8.onnx",
        "4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c", 118_054_593, "MIT");

    /// <summary>multilingual-e5-small's SentencePiece vocabulary (MIT).</summary>
    public static readonly ModelInfo PassagesVocabulary = new(
        "multilingual-e5-small-spm", "multilingual-e5-small vocabulary", "multilingual-e5-small.spm",
        "https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78/sentencepiece.bpe.model",
        "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", 5_069_051, "MIT");

    /// <summary>SmolVLM 500M Instruct, 8-bit (Apache-2.0): says what a picture shows, for Describe Picture. Run by llama.cpp.</summary>
    public static readonly ModelInfo SmolVlm = new(
        "smolvlm-500m", "SmolVLM 500M", "SmolVLM-500M-Instruct-Q8_0.gguf",
        "https://huggingface.co/ggml-org/SmolVLM-500M-Instruct-GGUF/resolve/main/SmolVLM-500M-Instruct-Q8_0.gguf",
        "9d4612de6a42214499e301494a3ecc2be0abdd9de44e663bda63f1152fad1bf4", 436_806_912, "Apache-2.0");

    /// <summary>SmolVLM's picture reader (Apache-2.0): turns the picture into what the model reads.</summary>
    public static readonly ModelInfo SmolVlmProjector = new(
        "smolvlm-500m-mmproj", "SmolVLM picture reader", "mmproj-SmolVLM-500M-Instruct-Q8_0.gguf",
        "https://huggingface.co/ggml-org/SmolVLM-500M-Instruct-GGUF/resolve/main/mmproj-SmolVLM-500M-Instruct-Q8_0.gguf",
        "d1eb8b6b23979205fdf63703ed10f788131a3f812c7b1f72e0119d5d81295150", 108_783_360, "Apache-2.0");

    /// <summary>CLIP ViT-B/32 image encoder, 8-bit (MIT): what pictures show, for Search by Meaning.</summary>
    public static readonly ModelInfo ClipVision = new(
        "clip-vision", "CLIP ViT-B/32", "clip-vit-b32-vision-quantized.onnx",
        "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx",
        "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299", 89_117_001, "MIT");

    /// <summary>Multilingual CLIP text encoder, 8-bit (Apache-2.0): searches in about 50 languages, Arabic included.</summary>
    public static readonly ModelInfo ClipText = new(
        "clip-text-multilingual", "Multilingual CLIP text model", "clip-vit-b32-multilingual-text-quantized.onnx",
        "https://huggingface.co/sentence-transformers/clip-ViT-B-32-multilingual-v1/resolve/main/onnx/model_quint8_avx2.onnx",
        "fbc8fbeaa5237d96bd1bf430057c70d34de8334a463e933a5faa305f6caeed9c", 135_377_779, "Apache-2.0");

    public static readonly ModelInfo ClipTextProjection = new(
        "clip-text-multilingual-dense", "Multilingual CLIP projection", "clip-vit-b32-multilingual-dense.safetensors",
        "https://huggingface.co/sentence-transformers/clip-ViT-B-32-multilingual-v1/resolve/main/2_Dense/model.safetensors",
        "d12568dc7300970a4d3dbb49068ad16cd89b99840b74b026f8e48071e9414f74", 1_572_984, "Apache-2.0");

    public static readonly ModelInfo ClipTextVocabulary = new(
        "clip-text-multilingual-vocab", "Multilingual CLIP vocabulary", "clip-vit-b32-multilingual-vocab.txt",
        "https://huggingface.co/sentence-transformers/clip-ViT-B-32-multilingual-v1/resolve/main/vocab.txt",
        "fe0fda7c425b48c516fc8f160d594c8022a0808447475c1a7c6d6479763f310c", 995_526, "Apache-2.0");

    /// <summary>DeepFilterNet 3, version 0.5.6 (MIT/Apache-2.0): takes noise out of speech; the model is inside the program.</summary>
    public static readonly ModelInfo DeepFilter = new(
        "deepfilternet", "DeepFilterNet 3", "deep-filter-0.5.6-x86_64-pc-windows-msvc.exe",
        "https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-x86_64-pc-windows-msvc.exe",
        "75e11fa16445f560cb6b021521ddb89e89270d13b83089705d98776f58fd7915", 26_912_256, "MIT");

    /// <summary>Hybrid Transformer Demucs (Meta, MIT) as one ONNX file: splits songs into vocals, drums, bass and the rest.</summary>
    public static readonly ModelInfo Demucs = new(
        "htdemucs", "HT Demucs", "htdemucs_embedded.onnx",
        "https://huggingface.co/timcsy/demucs-web-onnx/resolve/main/htdemucs_embedded.onnx",
        "e5e425c17683f163a472462eb5f5a4ffcd11c31858d57fbd0833b012d8b88077", 180_534_758, "MIT");

    /// <summary>
    /// TIGER for Divide and Remaster (Tsinghua University, Apache-2.0), audiojs's 8-bit ONNX export at a fixed revision:
    /// splits film sound into dialogue, music and effects, so Dub can take the music out and keep the other sounds.
    /// </summary>
    public static readonly ModelInfo Tiger = new(
        "tiger-dnr", "TIGER sound separation", "tiger-dnr.int8.onnx",
        "https://huggingface.co/audiojs/tiger-dnr/resolve/65072a210374cac07ea2ac30a85a4a0b7c71a652/tiger.int8.onnx",
        "2189859ac7e89cf4d9d284c6f393e737aaa2b3233ed7ee424ad622012bce7bca", 7_320_735, "Apache-2.0");

    /// <summary>LaMa (big-lama, Apache-2.0), Carve's ONNX export: fills in what Erase Object removes.</summary>
    public static readonly ModelInfo Lama = new(
        "lama", "LaMa", "lama_fp32.onnx",
        "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx",
        "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6", 208_044_816, "Apache-2.0");

    /// <summary>Opus-MT English → Arabic (Helsinki-NLP, Apache-2.0), 8-bit ONNX: translates subtitles into Arabic.</summary>
    public static readonly ModelInfo OpusEnArEncoder = new(
        "opus-mt-en-ar-encoder", "Opus-MT English → Arabic encoder", "opus-mt-en-ar-encoder-quantized.onnx",
        "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/main/onnx/encoder_model_quantized.onnx",
        "53c354d87ced44b39085fe8a46a25f09ea4578d442dbce9663f3977228f8a04e", 51_773_854, "Apache-2.0");

    public static readonly ModelInfo OpusEnArDecoder = new(
        "opus-mt-en-ar-decoder", "Opus-MT English → Arabic", "opus-mt-en-ar-decoder-quantized.onnx",
        "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/main/onnx/decoder_model_quantized.onnx",
        "94e6bdf90866151787d014b7b77be0adb0f036418ee04d235ec01238aa8426b9", 58_707_418, "Apache-2.0");

    public static readonly ModelInfo OpusEnArSource = new(
        "opus-mt-en-ar-source", "Opus-MT English pieces", "opus-mt-en-ar-source.spm",
        "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/main/source.spm",
        "81ac8f4d4ee271f4109eac67560a31f9c2d555463ba5eb95c32c6deee8ae819d", 801_074, "Apache-2.0");

    public static readonly ModelInfo OpusEnArVocabulary = new(
        "opus-mt-en-ar-vocab", "Opus-MT vocabulary", "opus-mt-en-ar-vocab.json",
        "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/main/vocab.json",
        "8b74af9e4c1ec122b8d06362db6c38795e10ca2b0156b2110cf08d4716744b76", 2_243_581, "Apache-2.0");

    /// <summary>Opus-MT Arabic → English (Helsinki-NLP, Apache-2.0), 8-bit ONNX: puts Arabic speech's words into English.</summary>
    public static readonly ModelInfo OpusArEnEncoder = new(
        "opus-mt-ar-en-encoder", "Opus-MT Arabic → English encoder", "opus-mt-ar-en-encoder-quantized.onnx",
        "https://huggingface.co/Xenova/opus-mt-ar-en/resolve/main/onnx/encoder_model_quantized.onnx",
        "d9b0d612acff86cf87634b49bda566d1ef39b312837de549786ea18fecefc708", 51_790_238, "Apache-2.0");

    public static readonly ModelInfo OpusArEnDecoder = new(
        "opus-mt-ar-en-decoder", "Opus-MT Arabic → English", "opus-mt-ar-en-decoder-quantized.onnx",
        "https://huggingface.co/Xenova/opus-mt-ar-en/resolve/main/onnx/decoder_model_quantized.onnx",
        "66f2be0af1ad3d875315f7e8ad2de4f8248fba75dc7173b1a93bf48023041e80", 58_723_930, "Apache-2.0");

    public static readonly ModelInfo OpusArEnSource = new(
        "opus-mt-ar-en-source", "Opus-MT Arabic pieces", "opus-mt-ar-en-source.spm",
        "https://huggingface.co/Xenova/opus-mt-ar-en/resolve/main/source.spm",
        "484f7210e5f4466f7e5b99b660717c6ef2a6f90746196daba507795b69dc02a2", 917_407, "Apache-2.0");

    public static readonly ModelInfo OpusArEnVocabulary = new(
        "opus-mt-ar-en-vocab", "Opus-MT Arabic → English vocabulary", "opus-mt-ar-en-vocab.json",
        "https://huggingface.co/Xenova/opus-mt-ar-en/resolve/main/vocab.json",
        "87b78c097c331672ca22a522c3ba24c5dc2cdff90495dcb52cf742635439a3a0", 2_257_246, "Apache-2.0");

    /// <summary>
    /// Chatterbox Multilingual V3 (Resemble AI, MIT): speech in someone's own voice in Arabic, English and 21 other
    /// languages, for Dub. Its voice listener, text reader and speech writer come from the ONNX export by Masterx, and its
    /// voice maker from KitsuMate's, which has it in parts (the flow prepared, one step of it, the vocoder) that load in
    /// seconds instead of a minute and a half, and whose step can run on the graphics card. Both at fixed revisions; the
    /// files sit together in their own folder (the graphs find their weights beside them by name).
    /// </summary>
    private const string ChatterboxBase = "https://huggingface.co/Masterx/chatterbox-multilingual-v3-ONNX/resolve/19b94afe41de738624b2f3afc75b4cd56221fbeb/";

    private const string ChatterboxFlowBase = "https://huggingface.co/KitsuMate/chatterbox-multilingual-v3-onnx/resolve/3f44461db006cc1ffb553cda02d6f6841125288a/onnx/";

    public static readonly ModelInfo ChatterboxEncoder = new(
        "chatterbox-v3-encoder", "Chatterbox voice listener", @"chatterbox-v3\speech_encoder.onnx", ChatterboxBase + "onnx/speech_encoder.onnx",
        "012a9583c0a9560afa11307b585f1e799fca0e1701bdae53fa34766da78f8eae", 1_204_600, "MIT");

    public static readonly ModelInfo ChatterboxEncoderWeights = new(
        "chatterbox-v3-encoder-data", "Chatterbox Multilingual", @"chatterbox-v3\speech_encoder.onnx_data", ChatterboxBase + "onnx/speech_encoder.onnx_data",
        "f1f663a1a03aa885f8f0fcaa730dd8dfbce8caad514093b13c9682a4a2842990", 591_274_880, "MIT");

    public static readonly ModelInfo ChatterboxEmbeddings = new(
        "chatterbox-v3-embed", "Chatterbox text reader", @"chatterbox-v3\embed_tokens.onnx", ChatterboxBase + "onnx/embed_tokens.onnx",
        "c8dd692ee8dfc9099baf39c8074e9bcc0a760e8c25ae1cac4a8c62c7cf4536bc", 17_616, "MIT");

    public static readonly ModelInfo ChatterboxEmbeddingWeights = new(
        "chatterbox-v3-embed-data", "Chatterbox text reader weights", @"chatterbox-v3\embed_tokens.onnx_data", ChatterboxBase + "onnx/embed_tokens.onnx_data",
        "d263116bec8ae582953c99bdffa9ecd53914d1ae2732da8455e361ed5baa2a9a", 68_808_704, "MIT");

    public static readonly ModelInfo ChatterboxLanguage = new(
        "chatterbox-v3-language", "Chatterbox speech writer", @"chatterbox-v3\language_model_q4.onnx", ChatterboxBase + "onnx/language_model_q4.onnx",
        "91846c24706116f87b39aac5b13aa1a67243b6f973bc433327d143b8eff9fde6", 228_633, "MIT");

    public static readonly ModelInfo ChatterboxLanguageWeights = new(
        "chatterbox-v3-language-data", "Chatterbox speech writer weights", @"chatterbox-v3\language_model_q4.onnx_data", ChatterboxBase + "onnx/language_model_q4.onnx_data",
        "621a963492f871ccc841592c6ca117bf88dbea4c0c25d61be4391197c8b878b0", 353_621_248, "MIT");

    public static readonly ModelInfo ChatterboxFlowStart = new(
        "chatterbox-v3-flow-prepare", "Chatterbox voice maker", @"chatterbox-v3\flow_prepare_slim.onnx", ChatterboxFlowBase + "flow_prepare_slim.onnx",
        "017b89038172ba02a3aa099b8de0804fd7573ab34089a23947aa1fde4eed5d2d", 285_857, "MIT");

    public static readonly ModelInfo ChatterboxFlowStartWeights = new(
        "chatterbox-v3-flow-prepare-data", "Chatterbox voice maker weights", @"chatterbox-v3\flow_prepare_slim.onnx.data", ChatterboxFlowBase + "flow_prepare_slim.onnx.data",
        "63e02f938147256fda34cbfa80e0d03abecdbbe02cc64f96b6b5762ea586f469", 185_464_832, "MIT");

    public static readonly ModelInfo ChatterboxFlowStep = new(
        "chatterbox-v3-flow-step", "Chatterbox voice maker steps", @"chatterbox-v3\flow_step_slim.onnx", ChatterboxFlowBase + "flow_step_slim.onnx",
        "4c45789f929f7728bc1c0f6b7ec50a9b1e787847e708cc78bf615d837750ae96", 286_124_928, "MIT");

    public static readonly ModelInfo ChatterboxVocoder = new(
        "chatterbox-v3-vocoder", "Chatterbox vocoder", @"chatterbox-v3\vocoder_slim.onnx", ChatterboxFlowBase + "vocoder_slim.onnx",
        "cc59b61c5dba729d13b059ea8a47bb5ab61db3463027ecd25c73e21668d925c9", 293_882, "MIT");

    public static readonly ModelInfo ChatterboxVocoderWeights = new(
        "chatterbox-v3-vocoder-data", "Chatterbox vocoder weights", @"chatterbox-v3\vocoder_slim.onnx.data", ChatterboxFlowBase + "vocoder_slim.onnx.data",
        "d3e9a3a21a3e37ba5d5ef3fec3b18d20a91f984b39f929ff225a9598529f0ea3", 83_194_752, "MIT");

    public static readonly ModelInfo ChatterboxVocabulary = new(
        "chatterbox-v3-tokenizer", "Chatterbox vocabulary", @"chatterbox-v3\tokenizer.json", ChatterboxBase + "tokenizer.json",
        "8763bb615f5e0ad49fbb53b65d371e37961f64bb0a55b96c0205f4a59ba5be13", 72_765, "MIT");

    /// <summary>Everything Chatterbox needs, the largest first.</summary>
    public static IReadOnlyList<ModelInfo> Chatterbox { get; } =
        [ChatterboxEncoderWeights, ChatterboxLanguageWeights, ChatterboxFlowStep, ChatterboxFlowStartWeights, ChatterboxVocoderWeights, ChatterboxEmbeddingWeights,
         ChatterboxEncoder, ChatterboxVocoder, ChatterboxFlowStart, ChatterboxLanguage, ChatterboxVocabulary, ChatterboxEmbeddings];

    public static IReadOnlyList<ModelInfo> All { get; } =
        [Background, Upscaler, WhisperFast, WhisperBalanced, WhisperAccurate, Piper, VoiceKareem, VoiceKareemSettings, VoiceJoe, VoiceJoeSettings, LlamaCpp, Qwen, QwenAnswers, Passages, PassagesVocabulary, SmolVlm, SmolVlmProjector,
         ClipVision, ClipText, ClipTextProjection, ClipTextVocabulary, DeepFilter, Demucs, Tiger, Lama,
         OpusEnArEncoder, OpusEnArDecoder, OpusEnArSource, OpusEnArVocabulary, OpusArEnEncoder, OpusArEnDecoder, OpusArEnSource, OpusArEnVocabulary,
         .. Chatterbox];
}

/// <summary>Where models live, and the verified download of the ones that don't ship with Petal.</summary>
public static class ModelStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Downloaded models: %LOCALAPPDATA%\Petal\models (overridable for tests).</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "models");

    /// <summary>Bundled models, next to the app.</summary>
    public static string BundledFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "models");

    /// <summary>The model's file when it is on this PC (and complete), else null.</summary>
    public static string? Find(ModelInfo model)
    {
        if (model.Bundled)
        {
            var bundled = Path.Combine(BundledFolder, model.FileName);
            if (File.Exists(bundled))
                return bundled;
            // Development builds: tools\bin\models at the repository root.
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var dev = Path.Combine(dir.FullName, "tools", "bin", "models", model.FileName);
                if (File.Exists(dev))
                    return dev;
            }
            return null;
        }
        var path = Path.Combine(Folder, model.FileName);
        if (File.Exists(path) && new FileInfo(path).Length == model.Size)
            return path;
        // The Store's package brings the programs among the downloads (Piper, llama.cpp) with it.
        var shipped = Path.Combine(BundledFolder, model.FileName);
        return File.Exists(shipped) && new FileInfo(shipped).Length == model.Size ? shipped : null;
    }

    public static bool IsAvailable(ModelInfo model) => Find(model) is not null;

    /// <summary>
    /// Downloads the model, checks its SHA-256, and only then moves it into place. A download
    /// that is interrupted or doesn't match leaves nothing behind.
    /// </summary>
    public static async Task<string> DownloadAsync(ModelInfo model, IProgress<double>? progress = null, CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        if (model.Bundled)
            return Find(model) ?? throw new FileNotFoundException($"{model.Name} should come with DropWheel but is missing. Reinstall DropWheel to restore it.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Find(model) is { } existing)
                return existing;
            var target = Path.Combine(Folder, model.FileName);
            // A model of several files keeps them in a folder of its own.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            var http = client ?? Shared.Value;
            try
            {
                using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? model.Size;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        done += read;
                        progress?.Report(Math.Min(1, (double)done / Math.Max(1, total)));
                    }
                    var actual = Convert.ToHexString(hash.GetHashAndReset());
                    if (!actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The downloaded {model.Name} didn't match its checksum, so it wasn't used. Try again later.");
                }
                File.Move(part, target, overwrite: true);
                return target;
            }
            catch
            {
                try { File.Delete(part); } catch (IOException) { }
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes a downloaded model to free space (bundled models stay).</summary>
    public static void Delete(ModelInfo model)
    {
        // Only what was downloaded: a copy that ships with the app stays.
        if (!model.Bundled && Find(model) is { } path && !path.StartsWith(BundledFolder, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DropWheel/1.0");
        return client;
    });
}
