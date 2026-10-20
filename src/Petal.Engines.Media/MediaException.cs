namespace Petal.Engines.Media;

/// <summary>A media file cannot be processed as asked, e.g. it has no audio track. The message is user-facing.</summary>
public sealed class MediaException(string message, Exception? inner = null) : Exception(message, inner);
