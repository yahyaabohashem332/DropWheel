namespace Petal.Engines.Media.Internal;

/// <summary>
/// Meets a file size with Windows' H.264 encoder. Its bitrate modes miss short videos and small sizes by up to several
/// times, and no setting takes it below what its lowest quality makes; its quality setting, though, gives the same size
/// every time. So the setting that fills the size is searched for on windows spread over the video (all of it when it
/// is short), the picture is made smaller when only a very low setting would fit, and the whole is encoded once with
/// it, and again only if it still came out over.
/// </summary>
internal static class SizeFit
{
    /// <summary>Videos up to this long, and this many pixels in all, are tried whole; others on windows spread over them.</summary>
    private const double WholeSeconds = 45;
    private const double WholePixels = 45 * 30 * 1920.0 * 1080;
    private const double WindowSeconds = 3;

    /// <summary>Below this setting a smaller picture looks better than a blockier one.</summary>
    internal const int LowestGoodQuality = 15;

    /// <summary>The smallest picture (its shorter side) a size is met with.</summary>
    internal const int SmallestSide = 144;

    private const int TriesPerSide = 7;

    /// <summary>
    /// Encodes <paramref name="plan"/> into <paramref name="output"/> at no more than <paramref name="wanted"/> bytes,
    /// starting from a picture whose shorter side is <paramref name="shortSide"/>; <paramref name="filterFor"/> gives the
    /// video filter for a shorter side. Throws <see cref="SizeTooSmallException"/> when even the smallest picture at the
    /// lowest setting is too big.
    /// </summary>
    public static async Task RunAsync(EncodePlan plan, string output, long wanted, int shortSide, Func<int, string?> filterFor,
        SafeProgress progress, CancellationToken cancellationToken)
    {
        var info = plan.Source;
        var duration = plan.Duration;
        var fps = info.FrameRate is > 0 and < 240 ? Math.Min(info.FrameRate, 60) : 30;
        int tracks = !info.HasAudio || plan.Audio == StreamMode.Drop ? 0
            : plan.Target.MultipleAudio && plan.AllAudioTracks ? info.AudioStreams.Count() : 1;
        // Windows' AAC encoder keeps to its bitrate; MP4's tables take about 12 bytes a frame.
        var sound = tracks * plan.AudioKbps * 125.0 * duration;
        var tables = 4096 + 12 * duration * (fps + tracks * 47);
        var budget = wanted * 0.98 - sound - tables;

        List<(double Start, double Length)>? windows = null;
        if (duration > WholeSeconds || info.Width * (double)info.Height * fps * duration > WholePixels)
        {
            var count = Math.Clamp((int)Math.Round(duration / 40), 6, 12);
            windows = [.. Enumerable.Range(0, count).Select(k =>
            {
                var start = Math.Max(0, (k + 0.5) * duration / count - WindowSeconds / 2);
                return (start, Math.Min(WindowSeconds, duration - start));
            })];
        }

        using var scratch = new ScratchFolder();
        var curves = new Dictionary<int, SortedDictionary<int, double>>();
        var tries = 0;

        // The picture's bytes for the whole video at a setting and size, from the windows (or exactly, from all of it).
        async Task<double> TryAsync(int quality, int side)
        {
            var filter = filterFor(side);
            double bytes = 0, seconds = 0;
            foreach (var (start, length) in windows ?? [(0, duration)])
            {
                var file = scratch.File($"try{tries}-{(int)(start * 1000)}.mp4");
                var trial = plan with
                {
                    InputOptions = windows is null ? plan.InputOptions : ["-ss", Inv.N(start), "-t", Inv.N(length), .. plan.InputOptions],
                    Audio = StreamMode.Drop,
                    Subtitles = false,
                    Chapters = false,
                    Metadata = false,
                    VideoFilter = filter,
                    Quality = plan.Quality with { BitrateKbps = null, WindowsQuality = quality },
                    Duration = length,
                };
                await MediaEncoder.RunAsync(trial, file, null, cancellationToken).ConfigureAwait(false);
                bytes += Math.Max(1, new FileInfo(file).Length - (1024 + 12 * length * fps));
                seconds += length;
                File.Delete(file);
            }
            tries++;
            progress.Report(Math.Min(0.3, tries * 0.025));
            return bytes * duration / Math.Max(seconds, 0.001);
        }

        SortedDictionary<int, double> CurveOf(int side) =>
            curves.TryGetValue(side, out var known) ? known : curves[side] = [];

        // How much the size grows for each step of the setting, in natural log: measured once two are known.
        static double Slope(SortedDictionary<int, double> curve)
        {
            if (curve.Count >= 2)
            {
                var (first, last) = (curve.First(), curve.Last());
                var slope = (Math.Log(last.Value) - Math.Log(first.Value)) / (last.Key - first.Key);
                if (slope > 0.004)
                    return slope;
            }
            return 0.03;
        }

        static double Estimate(SortedDictionary<int, double> curve, int quality)
        {
            if (curve.TryGetValue(quality, out var known))
                return known;
            var nearest = curve.MinBy(p => Math.Abs(p.Key - quality));
            return nearest.Value * Math.Exp(Slope(curve) * (quality - nearest.Key));
        }

        // The highest setting whose picture fits the budget at this size, or none.
        async Task<int?> SearchAsync(int side, int start)
        {
            var curve = CurveOf(side);
            int? fit = curve.Where(p => p.Value <= budget).Select(p => (int?)p.Key).Max();
            int? over = curve.Where(p => p.Value > budget).Select(p => (int?)p.Key).Min();
            var quality = Math.Clamp(start, 1, 100);
            if (fit is { } f0 && over is { } o0)
                quality = Math.Clamp(quality, f0, o0);
            for (var i = 0; i < TriesPerSide; i++)
            {
                if (!curve.TryGetValue(quality, out var size))
                    curve[quality] = size = await TryAsync(quality, side).ConfigureAwait(false);
                if (size <= budget)
                {
                    fit = Math.Max(fit ?? quality, quality);
                    if (size >= budget * 0.95 && quality == fit)
                        break;
                }
                else
                {
                    over = Math.Min(over ?? quality, quality);
                }

                int? next;
                if (fit is { } low && over is { } high)
                {
                    if (high - low <= 1)
                        break;
                    var (a, b) = (Math.Log(curve[low]), Math.Log(curve[high]));
                    var t = b > a ? (Math.Log(budget) - a) / (b - a) : 0.5;
                    next = Math.Clamp(low + (int)Math.Floor(t * (high - low)), low + 1, high - 1);
                }
                else if (fit is { } up)
                {
                    next = up >= 100 ? null : Math.Clamp(up + (int)Math.Floor(Math.Log(budget / curve[up]) / Slope(curve)), up + 1, 100);
                }
                else
                {
                    var down = over!.Value;
                    next = down <= 1 ? null : Math.Clamp(down - (int)Math.Ceiling(Math.Log(curve[down] / budget) / Slope(curve)), 1, down - 1);
                }
                if (next is null)
                    break;
                quality = next.Value;
            }
            return fit;
        }

        // The size and setting for the budget: the largest picture at which a good enough setting fits.
        var original = shortSide;
        async Task<(int Side, int Quality)> ChooseAsync(int side, int start)
        {
            for (var round = 0; ; round++)
            {
                var chosen = budget > 0 ? await SearchAsync(side, start).ConfigureAwait(false) : null;
                if (chosen is { } q && (q >= LowestGoodQuality || side <= SmallestSide))
                    return (side, q);
                if (side <= SmallestSide)
                {
                    var curve = CurveOf(side);
                    var least = curve.Count == 0 ? await TryAsync(1, side).ConfigureAwait(false) : Estimate(curve, 1);
                    // A little over what the estimate says, so that asking for that size again works.
                    throw new SizeTooSmallException((long)((least + sound + tables) / 0.98 * 1.03));
                }
                // A smaller picture: the size goes about as the shorter side to the power 1.4.
                var smaller = SmallestSide;
                var known = CurveOf(side);
                if (budget > 0 && known.Count > 0 && round < 4)
                {
                    var ratio = Math.Clamp(Math.Pow(budget / Estimate(known, LowestGoodQuality), 1 / 1.4), 0.5, 0.9);
                    smaller = Math.Max(SmallestSide, (int)(side * ratio) / 2 * 2);
                }
                side = Math.Min(Math.Min(side - 2, original), smaller);
                start = LowestGoodQuality;
            }
        }

        var (finalSide, finalQuality) = await ChooseAsync(shortSide, MediaEncoders.QualityFor(plan.Quality.Crf, hardware: false)).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var final = plan with
            {
                VideoFilter = filterFor(finalSide),
                Quality = plan.Quality with { BitrateKbps = null, WindowsQuality = finalQuality },
            };
            var slice = attempt == 0 ? progress.Slice(0.3, 0.62) : progress.Slice(0.92 + 0.04 * (attempt - 1), 0.04);
            await MediaEncoder.RunAsync(final, output, slice, cancellationToken).ConfigureAwait(false);
            var made = new FileInfo(output).Length;
            if (made <= wanted || attempt >= 2)
                return;

            // Over: either the picture's estimate or the rest's was short. Plan for the worse of the two.
            var estimated = Estimate(CurveOf(finalSide), finalQuality);
            var picture = made - sound - tables;
            var room = wanted * 0.985 - sound - tables;
            var ifPicture = picture > estimated ? room * estimated / picture : room;
            var ifRest = wanted * 0.985 - (made - estimated);
            budget = Math.Min(Math.Min(ifPicture, ifRest), budget * 0.97);
            File.Delete(output);
            (finalSide, finalQuality) = await ChooseAsync(finalSide, finalQuality - 1).ConfigureAwait(false);
        }
    }
}
