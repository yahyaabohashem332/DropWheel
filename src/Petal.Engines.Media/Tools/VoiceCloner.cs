using Microsoft.ML.OnnxRuntime;

namespace Petal.Engines.Media;

/// <summary>A voice as the speech model learnt it from a few seconds of someone speaking.</summary>
public sealed class ClonedVoice : IDisposable
{
    internal ClonedVoice(OrtValue conditioning, long[] promptTokens, float[] speaker, float[] features, long[] featureShape, long[] conditioningShape)
    {
        Conditioning = conditioning;
        PromptTokens = promptTokens;
        Speaker = speaker;
        Features = features;
        FeatureShape = featureShape;
        ConditioningShape = conditioningShape;
    }

    internal OrtValue Conditioning { get; }

    internal long[] ConditioningShape { get; }

    internal long[] PromptTokens { get; }

    /// <summary>The speaker's embedding (what their voice is like), 192 numbers.</summary>
    public float[] Speaker { get; }

    internal float[] Features { get; }

    internal long[] FeatureShape { get; }

    public void Dispose() => Conditioning.Dispose();
}

/// <summary>
/// Speech in someone's own voice, in another language: Chatterbox Multilingual (Resemble AI, MIT), run on this PC through
/// ONNX Runtime. A few seconds of the person speaking give the voice (<see cref="Learn"/>); the text is then spoken in it
/// (<see cref="Speak"/>), Arabic and English among 23 languages. The language model writes speech tokens one at a time
/// (sampled as the model's authors recommend, with a fixed seed so the same text sounds the same twice); a flow turns them,
/// in ten steps from noise, into the picture of the sound (on the graphics card when there is one), and the vocoder makes it
/// 24 kHz sound.
/// </summary>
public sealed class VoiceCloner : IDisposable
{
    public const int Rate = 24000;

    /// <summary>The languages the model speaks.</summary>
    public static IReadOnlyList<string> Languages { get; } =
        ["ar", "da", "de", "el", "en", "es", "fi", "fr", "he", "hi", "it", "ja", "ko", "ms", "nl", "no", "pl", "pt", "ru", "sv", "sw", "tr", "zh"];

    private const int Layers = 30;
    private const int Heads = 16;
    private const int HeadSize = 64;

    private readonly InferenceSession _encoder, _embed, _language, _prepare, _vocoder;
    // One step of the flow: on the graphics card when there is one, back on the processor if the card fails.
    private InferenceSession _step;
    private readonly SessionOptions _options;
    private readonly string _stepPath;
    private readonly ChatterboxTokenizer _tokenizer;
    private readonly string[] _languageInputs;
    private readonly string[] _languageOutputs;

    /// <param name="folder">
    /// Where speech_encoder.onnx, embed_tokens.onnx, language_model_q4.onnx, flow_prepare_slim.onnx, flow_step_slim.onnx,
    /// vocoder_slim.onnx and tokenizer.json are (each graph's weights beside it).
    /// </param>
    /// <param name="graphics">Take the flow's steps on the graphics card (DirectML) when there is one.</param>
    public VoiceCloner(string folder, bool graphics = false)
    {
        _options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8) };
        _encoder = new InferenceSession(Path.Combine(folder, "speech_encoder.onnx"), _options);
        _embed = new InferenceSession(Path.Combine(folder, "embed_tokens.onnx"), _options);
        _language = new InferenceSession(Path.Combine(folder, "language_model_q4.onnx"), _options);
        _prepare = new InferenceSession(Path.Combine(folder, "flow_prepare_slim.onnx"), _options);
        _stepPath = Path.Combine(folder, "flow_step_slim.onnx");
        _step = graphics && Graphics(_stepPath) is { } onCard ? onCard : new InferenceSession(_stepPath, _options);
        _vocoder = new InferenceSession(Path.Combine(folder, "vocoder_slim.onnx"), _options);
        _tokenizer = ChatterboxTokenizer.Load(Path.Combine(folder, "tokenizer.json"));
        _languageInputs = [.. _language.InputMetadata.Keys];
        _languageOutputs = [.. _language.OutputMetadata.Keys];
    }

    /// <summary>A session on the graphics card through DirectML, or null when there's no card it can use.</summary>
    private static InferenceSession? Graphics(string path)
    {
        try
        {
            var options = new SessionOptions
            {
                // DirectML wants these off.
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // Not the notice that the shape work stays on the processor, as it should.
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            options.AppendExecutionProvider_DML(0);
            OnGraphics = true;
            return new InferenceSession(path, options);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            OnGraphics = false;
            GraphicsProblem = ex.Message;
            return null;
        }
    }

    /// <summary>Whether the flow's steps are taken on the graphics card.</summary>
    public static bool OnGraphics { get; private set; }

    /// <summary>Why the graphics card couldn't be used, when it couldn't.</summary>
    public static string? GraphicsProblem { get; private set; }

    /// <summary>The model's inputs and outputs, for checking an export.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (name, session) in new[] { ("encoder", _encoder), ("embed", _embed), ("language", _language), ("prepare", _prepare), ("step", _step), ("vocoder", _vocoder) })
        {
            sb.Append(name).Append(':');
            foreach (var input in session.InputMetadata.Take(4))
                sb.Append(" in ").Append(input.Key).Append(' ').Append(input.Value.ElementDataType).Append('[').Append(string.Join(',', input.Value.Dimensions)).Append(']');
            foreach (var output in session.OutputMetadata.Take(4))
                sb.Append(" out ").Append(output.Key).Append(' ').Append(output.Value.ElementDataType).Append('[').Append(string.Join(',', output.Value.Dimensions)).Append(']');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The voice in a recording of someone speaking: 24 kHz mono samples, about 5 to 10 seconds of them (longer is cut;
    /// shorter works, less like them).
    /// </summary>
    public ClonedVoice Learn(ReadOnlySpan<float> speech)
    {
        var audio = speech.Length > Rate * 10 ? speech[..(Rate * 10)].ToArray() : speech.ToArray();
        using var input = OrtValue.CreateTensorValueFromMemory(audio, [1, audio.Length]);
        using var run = new RunOptions();
        var names = _encoder.OutputMetadata.Keys.ToArray();
        using var outputs = _encoder.Run(run, [_encoder.InputMetadata.Keys.First()], [input], names);
        // cond_emb, prompt_token, ref_x_vector, prompt_feat.
        var conditioningShape = outputs[0].GetTensorTypeAndShape().Shape;
        var conditioning = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        var prompt = outputs[1].GetTensorDataAsSpan<long>().ToArray();
        var speaker = outputs[2].GetTensorDataAsSpan<float>().ToArray();
        var features = outputs[3].GetTensorDataAsSpan<float>().ToArray();
        var featureShape = outputs[3].GetTensorTypeAndShape().Shape;
        // The decoder hears the voice's last few seconds only: it works on them again for every sentence, and a few
        // seconds give the voice as well as ten (the language model still has all of it).
        int keep = (int)Math.Min(prompt.Length, DecoderPromptSeconds * 25);
        if (keep < prompt.Length && featureShape[1] == prompt.Length * 2)
        {
            prompt = prompt[^keep..];
            int bins = (int)featureShape[2];
            features = features[^(keep * 2 * bins)..];
            featureShape = [1, keep * 2, bins];
        }
        return new ClonedVoice(OrtValue.CreateTensorValueFromMemory(conditioning, conditioningShape), prompt, speaker, features, featureShape, conditioningShape);
    }

    /// <summary>How much of the voice the decoder is given, in seconds.</summary>
    public double DecoderPromptSeconds { get; init; } = 4;

    /// <summary>
    /// The text said in the voice, as 24 kHz mono samples. <paramref name="exaggeration"/> is how much feeling (0.5 is
    /// natural; more is livelier and a little quicker). Null when the model finds nothing to say.
    /// </summary>
    public float[] Speak(string text, string language, ClonedVoice voice, double exaggeration = 0.5, int seed = 1, CancellationToken cancellationToken = default)
    {
        var ids = _tokenizer.Encode(text, language);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tokens = Generate(ids, voice, (float)exaggeration, MostTokens(text), new Random(seed), cancellationToken);
        LastWriting = watch.Elapsed;
        LastTokens = tokens.Count;
        watch.Restart();
        if (tokens.Count == 0)
            return [];
        var speech = new long[voice.PromptTokens.Length + tokens.Count];
        voice.PromptTokens.CopyTo(speech, 0);
        for (int i = 0; i < tokens.Count; i++)
            speech[voice.PromptTokens.Length + i] = tokens[i];
        var sound = Decode(speech, voice);
        LastDecoding = watch.Elapsed;
        // The last token's sound is the start of the stop, a click or a breath: left out (960 samples a token), as upstream does.
        return sound.Length > 960 * 2 ? sound[..^960] : sound;
    }

    /// <summary>
    /// How many steps the flow takes from noise to the sound's picture: ten, as the model's authors take them (six sound
    /// less like the speaker).
    /// </summary>
    public int FlowSteps { get; init; } = 10;

    /// <summary>How far each step is pushed away from what the voice would be without the speaker (classifier-free guidance).</summary>
    public float FlowGuidance { get; init; } = 0.7f;

    /// <summary>The speech tokens (the voice's own first) as 24 kHz sound.</summary>
    private float[] Decode(long[] speech, ClonedVoice voice)
    {
        try
        {
            return Flow(speech, voice);
        }
        catch (OnnxRuntimeException ex) when (OnGraphics)
        {
            // The graphics card gave out (a driver reset, its memory full): the rest is made on the processor.
            OnGraphics = false;
            GraphicsProblem = ex.Message;
            var card = _step;
            _step = new InferenceSession(_stepPath, _options);
            card.Dispose();
            return Flow(speech, voice);
        }
    }

    private float[] Flow(long[] speech, ClonedVoice voice)
    {
        using var tokensValue = OrtValue.CreateTensorValueFromMemory(speech, [1, speech.Length]);
        using var speakerValue = OrtValue.CreateTensorValueFromMemory(voice.Speaker, [1, voice.Speaker.Length]);
        using var featuresValue = OrtValue.CreateTensorValueFromMemory(voice.Features, voice.FeatureShape);
        using var run = new RunOptions();
        // The flow, prepared once: its starting noise, the tokens' picture (mu), the speaker and the voice's own frames.
        using var prepared = _prepare.Run(run, ["speech_tokens", "speaker_embeddings", "speaker_features"], [tokensValue, speakerValue, featuresValue],
            ["x", "mask", "mu", "speakers", "cond", "prefix"]);
        long prefix = prepared[5].GetTensorDataAsSpan<long>()[0];
        using var guidance = OrtValue.CreateTensorValueFromMemory(new[] { FlowGuidance }, [1]);
        // Steps along a cosine schedule, short at first and long at the end, as the model was trained.
        var x = prepared[0];
        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        try
        {
            for (int k = 0; k < FlowSteps; k++)
            {
                float t = (float)(1 - Math.Cos(k / (double)FlowSteps * Math.PI / 2)), next = (float)(1 - Math.Cos((k + 1) / (double)FlowSteps * Math.PI / 2));
                using var time = OrtValue.CreateTensorValueFromMemory(new[] { t }, [1]);
                using var delta = OrtValue.CreateTensorValueFromMemory(new[] { next - t }, [1]);
                var stepped = _step.Run(run, ["x", "mask", "mu", "speakers", "cond", "time", "delta", "guidance"],
                    [x, prepared[1], prepared[2], prepared[3], prepared[4], time, delta, guidance], ["next_x"]);
                previous?.Dispose();
                previous = stepped;
                x = stepped[0];
            }
            // Voiced without the voice's own frames at the start.
            using var prefixValue = OrtValue.CreateTensorValueFromMemory(new[] { prefix }, [1]);
            using var voiced = _vocoder.Run(run, ["x", "prefix"], [x, prefixValue], ["waveform"]);
            return voiced[0].GetTensorDataAsSpan<float>().ToArray();
        }
        finally
        {
            previous?.Dispose();
        }
    }

    /// <summary>How long the last <see cref="Speak"/> took to write its speech tokens, and to turn them into sound.</summary>
    public TimeSpan LastWriting { get; private set; }

    public TimeSpan LastDecoding { get; private set; }

    public int LastTokens { get; private set; }

    /// <summary>Speech tokens for the text: 25 a second, with room to spare; a runaway is stopped.</summary>
    private static int MostTokens(string text) => Math.Clamp(60 + text.Length * 4, 80, 1000);

    /// <summary>The speech tokens, one at a time, from the language model with its cache of what came before.</summary>
    private List<long> Generate(long[] ids, ClonedVoice voice, float exaggeration, int most, Random random, CancellationToken ct)
    {
        var generated = new List<long> { ChatterboxTokenizer.StartSpeech };
        var positions = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            positions[i] = ids[i] >= ChatterboxTokenizer.StartSpeech ? 0 : i - 1;
        var embedded = Embed(ids, positions, exaggeration);

        // The voice's conditioning, then the text.
        int conditioningLength = (int)voice.ConditioningShape[1], width = (int)voice.ConditioningShape[2];
        var first = new float[(conditioningLength + ids.Length) * width];
        voice.Conditioning.GetTensorDataAsSpan<float>().CopyTo(first);
        embedded.CopyTo(first.AsSpan(conditioningLength * width));
        int length = conditioningLength + ids.Length;

        var cache = new OrtValue[Layers * 2];
        for (int i = 0; i < cache.Length; i++)
            cache[i] = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [1, Heads, 0, HeadSize]);
        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        var tokens = new List<long>();
        try
        {
            var input = first;
            int inputLength = length, total = length;
            for (int step = 0; step < most; step++)
            {
                ct.ThrowIfCancellationRequested();
                var mask = new long[total];
                Array.Fill(mask, 1);
                using var embeds = OrtValue.CreateTensorValueFromMemory(input, [1, inputLength, width]);
                using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [1, total]);
                var values = new OrtValue[_languageInputs.Length];
                for (int k = 0; k < _languageInputs.Length; k++)
                    values[k] = _languageInputs[k] switch
                    {
                        "inputs_embeds" => embeds,
                        "attention_mask" => maskValue,
                        var name => cache[Index(name)],
                    };
                using var run = new RunOptions();
                var outputs = _language.Run(run, _languageInputs, values, _languageOutputs);
                // The cache moves on to this step's; the step before's can go.
                previous?.Dispose();
                if (step == 0)
                    foreach (var empty in cache)
                        empty.Dispose();
                previous = outputs;
                for (int k = 1; k < _languageOutputs.Length; k++)
                    cache[Index(_languageOutputs[k])] = outputs[k];

                var logits = outputs[0].GetTensorDataAsSpan<float>();
                int vocabulary = (int)outputs[0].GetTensorTypeAndShape().Shape[2];
                var last = logits[^vocabulary..].ToArray();
                long next = Sample(last, generated, random);
                if (next == ChatterboxTokenizer.StopSpeech)
                    break;
                generated.Add(next);
                tokens.Add(next);
                // A token said over and over is the model stuck: what came before it is kept.
                if (tokens.Count >= 40 && tokens.Skip(tokens.Count - 40).All(t => t == next))
                {
                    tokens.RemoveRange(tokens.Count - 40, 40);
                    break;
                }
                input = Embed([next], [step + 1], exaggeration);
                inputLength = 1;
                total++;
            }
        }
        finally
        {
            previous?.Dispose();
        }
        return tokens;
    }

    /// <summary>"past_key_values.12.value" as its place in the cache: 2 × layer + (0 key, 1 value).</summary>
    private static int Index(string name)
    {
        var parts = name.Split('.');
        int layer = int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
        return layer * 2 + (parts[^1] == "key" ? 0 : 1);
    }

    private float[] Embed(long[] ids, long[] positions, float exaggeration)
    {
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var positionsValue = OrtValue.CreateTensorValueFromMemory(positions, [1, positions.Length]);
        using var exaggerationValue = OrtValue.CreateTensorValueFromMemory(new[] { exaggeration }, [1]);
        using var run = new RunOptions();
        using var outputs = _embed.Run(run, ["input_ids", "position_ids", "exaggeration"], [idsValue, positionsValue, exaggerationValue], [_embed.OutputMetadata.Keys.First()]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>
    /// The next token, as Chatterbox samples it: repetition penalised (1.2), temperature 0.8, then only tokens at least a
    /// twentieth as likely as the best (min-p), within the top 95% (top-p).
    /// </summary>
    internal static long Sample(float[] logits, IReadOnlyCollection<long> said, Random random, double temperature = 0.8, double minP = 0.05, double topP = 0.95)
    {
        foreach (var token in said.Distinct())
            if (token >= 0 && token < logits.Length)
                logits[token] = logits[token] < 0 ? logits[token] * 1.2f : logits[token] / 1.2f;
        double highest = double.MinValue;
        foreach (var l in logits)
            highest = Math.Max(highest, l);
        var probabilities = new double[logits.Length];
        double sum = 0;
        for (int i = 0; i < logits.Length; i++)
            sum += probabilities[i] = Math.Exp((logits[i] - highest) / temperature);
        double best = 0;
        for (int i = 0; i < probabilities.Length; i++)
            best = Math.Max(best, probabilities[i] /= sum);
        var kept = Enumerable.Range(0, probabilities.Length).Where(i => probabilities[i] >= best * minP).OrderByDescending(i => probabilities[i]).ToList();
        double running = 0;
        int cut = kept.Count;
        for (int k = 0; k < kept.Count; k++)
        {
            running += probabilities[kept[k]];
            if (running >= topP)
            {
                cut = k + 1;
                break;
            }
        }
        kept = kept.Take(cut).ToList();
        double total = kept.Sum(i => probabilities[i]), pick = random.NextDouble() * total;
        foreach (var i in kept)
        {
            pick -= probabilities[i];
            if (pick <= 0)
                return i;
        }
        return kept[^1];
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _embed.Dispose();
        _language.Dispose();
        _prepare.Dispose();
        _step.Dispose();
        _vocoder.Dispose();
        _options.Dispose();
    }
}
