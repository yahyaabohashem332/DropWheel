namespace Petal.Core.Jobs;

/// <summary>
/// Where a file is going, and what that place accepts: "fit it into WhatsApp" rather than CRF and
/// kilobits. Limits leave a little room, since apps count megabytes differently.
/// </summary>
/// <param name="BytesPerFile">Each file must stay under this; null when the app has no hard limit.</param>
/// <param name="BytesInAll">All files together must stay under this (an email's attachments); null when per file.</param>
/// <param name="VideoShortSide">Videos are scaled to at most this many lines (720 = 720p).</param>
/// <param name="ImageLongEdge">Pictures are scaled so their longest side is at most this.</param>
public sealed record ShareTarget(string Id, string Name, long? BytesPerFile, long? BytesInAll, int VideoShortSide, int ImageLongEdge, string Hint)
{
    /// <summary>
    /// The size each file should aim for: the per-file limit, or its share of an overall budget
    /// in proportion to its original size. Null when nothing needs to fit.
    /// </summary>
    public long? TargetFor(long fileBytes, long allBytes)
    {
        long? target = BytesPerFile;
        if (BytesInAll is { } budget && allBytes > 0)
        {
            var share = (long)(budget * ((double)fileBytes / allBytes));
            target = target is { } t ? Math.Min(t, share) : share;
        }
        // Already small enough: no target, just the usual light compression.
        return target is { } value && fileBytes > value ? value : null;
    }
}

public static class ShareTargets
{
    public static readonly ShareTarget WhatsApp = new("whatsapp", "WhatsApp", 15_500_000, null, 720, 1600,
        "Videos and audio under 16 MB, pictures at 1600 px: sent as they are, not squeezed again by WhatsApp.");

    public static readonly ShareTarget Email = new("email", "Email", null, 18_000_000, 720, 2560,
        "Everything together under 18 MB, so it fits a 25 MB attachment limit (Gmail, Outlook).");

    public static readonly ShareTarget Discord = new("discord", "Discord", 9_500_000, null, 720, 2560,
        "Each file under 10 MB, Discord's limit without Nitro.");

    public static readonly ShareTarget Upload = new("upload", "Upload form", 1_900_000, null, 480, 2048,
        "Each file under 2 MB, a common limit on application and university forms.");

    public static readonly ShareTarget Instagram = new("instagram", "Instagram", null, null, 1080, 1080,
        "Pictures 1080 px wide and videos at 1080p, the sizes Instagram shows.");

    public static IReadOnlyList<ShareTarget> All { get; } = [WhatsApp, Email, Discord, Upload, Instagram];

    public static ShareTarget? Find(string id) =>
        All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
}
