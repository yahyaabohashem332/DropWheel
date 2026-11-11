namespace Petal.Engines.Media;

/// <summary>
/// A size asked for that the video can't be made as small as, even with the smallest picture at the lowest quality;
/// <see cref="SmallestBytes"/> is about the smallest it can be.
/// </summary>
public sealed class SizeTooSmallException(long smallestBytes)
    : Exception($"This size is too small for this video. The smallest it can be is about {smallestBytes} bytes.")
{
    public long SmallestBytes { get; } = smallestBytes;
}
