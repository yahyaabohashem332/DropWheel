namespace Petal.Core.Recording;

/// <summary>
/// The recording's time against the finished video's: what is kept between the trims, less the stretches cut
/// out, with the stretches sped up played faster. Where a cut and a speed-up overlap, the cut wins.
/// </summary>
public sealed class EditTimeline
{
    /// <summary>A stretch of the recording that is kept, played <see cref="Speed"/> times as fast, from <see cref="OutputStart"/> in the video.</summary>
    public readonly record struct Piece(double Start, double End, double Speed, double OutputStart)
    {
        public double OutputLength => (End - Start) / Speed;

        public double OutputEnd => OutputStart + OutputLength;
    }

    private readonly List<Piece> _pieces = [];

    public EditTimeline(double duration, StudioEdit edit) : this(duration, edit.TrimStart, edit.TrimEnd, edit.Edits)
    {
    }

    public EditTimeline(double duration, double trimStart, double trimEnd, IEnumerable<EditSegment> edits)
    {
        double from = Math.Clamp(trimStart, 0, duration);
        double to = Math.Clamp(trimEnd <= 0 ? duration : trimEnd, from, duration);
        var list = edits.Where(e => e.End > e.Start).ToList();
        // Every place something starts or ends cuts the kept span into stretches that are one thing each.
        var marks = new SortedSet<double> { from, to };
        foreach (var e in list)
        {
            if (e.Start > from && e.Start < to)
                marks.Add(e.Start);
            if (e.End > from && e.End < to)
                marks.Add(e.End);
        }
        var points = marks.ToList();
        double output = 0;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            double a = points[i], b = points[i + 1];
            if (b - a < 1e-6)
                continue;
            double middle = (a + b) / 2;
            var over = list.Where(e => e.Start <= middle && e.End >= middle).ToList();
            if (over.Any(e => e.Kind == EditKind.Cut))
                continue;
            double speed = over.Where(e => e.Kind == EditKind.Speed).Select(e => Math.Max(0.1, e.Speed)).DefaultIfEmpty(1).Max();
            // Next to one just like it: one piece.
            if (_pieces.Count > 0 && Math.Abs(_pieces[^1].End - a) < 1e-6 && Math.Abs(_pieces[^1].Speed - speed) < 1e-9)
            {
                var last = _pieces[^1];
                _pieces[^1] = last with { End = b };
                output = _pieces[^1].OutputEnd;
                continue;
            }
            var piece = new Piece(a, b, speed, output);
            _pieces.Add(piece);
            output = piece.OutputEnd;
        }
        Length = output;
    }

    public IReadOnlyList<Piece> Pieces => _pieces;

    /// <summary>How long the finished video is.</summary>
    public double Length { get; }

    /// <summary>The moment of the recording shown at a moment of the finished video.</summary>
    public double ToSource(double output)
    {
        if (_pieces.Count == 0)
            return 0;
        if (output <= 0)
            return _pieces[0].Start;
        foreach (var p in _pieces)
            if (output < p.OutputEnd)
                return p.Start + (output - p.OutputStart) * p.Speed;
        return _pieces[^1].End;
    }

    /// <summary>The moment of the finished video that shows a moment of the recording, or null when it was cut out.</summary>
    public double? ToOutput(double source)
    {
        foreach (var p in _pieces)
            if (source >= p.Start && source <= p.End)
                return p.OutputStart + (source - p.Start) / p.Speed;
        return null;
    }

    /// <summary>The piece a moment of the recording is in, if it is kept.</summary>
    public Piece? At(double source)
    {
        foreach (var p in _pieces)
            if (source >= p.Start && source < p.End)
                return p;
        return null;
    }

    /// <summary>The first moment kept at or after a moment of the recording (the end when nothing is).</summary>
    public double NextKept(double source)
    {
        foreach (var p in _pieces)
        {
            if (source < p.Start)
                return p.Start;
            if (source < p.End)
                return source;
        }
        return _pieces.Count > 0 ? _pieces[^1].End : source;
    }
}
