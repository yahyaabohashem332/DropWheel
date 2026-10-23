using Petal.Engines.Media;
using Whisper.net;

namespace Petal.Engines.Media.Tests;

/// <summary>The near-live captions engine's own rules, without a model.</summary>
public sealed class StreamingTranscriberTests
{
    private static List<StreamWord> Words(params (string Text, double Start)[] words) =>
        words.Select((w, i) => new StreamWord(w.Text, w.Start, i + 1 < words.Length ? words[i + 1].Start : w.Start + 0.3)).ToList();

    [Fact]
    public void Words_Whisper_makes_up_at_the_end_are_left_out()
    {
        // Where the sound stopped mid-word, the made-up words are all put at the same moment.
        var heard = Words(("Later,", 1.48), ("in", 2.04), ("the", 2.14), ("14th", 2.56), ("century,", 2.66), ("the", 2.66), ("moon", 2.66), ("was", 2.66));
        StreamingTranscriber.DropInvented(heard);
        Assert.Equal(["Later,", "in", "the", "14th"], heard.Select(w => w.Text));
    }

    [Fact]
    public void Words_said_one_after_another_all_stay()
    {
        var heard = Words(("Everything", 1.36), ("changed", 1.94), ("when", 2.06), ("Christiaan", 2.50), ("Huygens", 2.70), ("built", 3.14));
        StreamingTranscriber.DropInvented(heard);
        Assert.Equal(6, heard.Count);
        // One word alone at the end isn't enough to tell.
        var one = Words(("a", 1.0), ("clock", 1.0));
        one.RemoveAt(0);
        StreamingTranscriber.DropInvented(one);
        Assert.Single(one);
    }

    [Theory]
    [InlineData("ggml-base-q5_1.bin", WhisperAlignmentHeadsPreset.Base)]
    [InlineData("ggml-base.en.bin", WhisperAlignmentHeadsPreset.BaseEn)]
    [InlineData("ggml-small-q5_1.bin", WhisperAlignmentHeadsPreset.Small)]
    [InlineData("ggml-large-v3-turbo-q5_0.bin", WhisperAlignmentHeadsPreset.LargeV3Turbo)]
    [InlineData("ggml-large-v3-q5_0.bin", WhisperAlignmentHeadsPreset.LargeV3)]
    public void Each_model_gets_its_alignment_heads(string file, WhisperAlignmentHeadsPreset heads) =>
        Assert.Equal(heads, StreamingTranscriber.Heads(file));

    [Fact]
    public void A_model_of_unknown_shape_has_no_aligned_times() => Assert.Null(StreamingTranscriber.Heads("my-model.bin"));
}
