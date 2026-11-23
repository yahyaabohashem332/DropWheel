namespace Petal.Engines.Files.Models;

/// <summary>What kind of picture a texture's bytes are.</summary>
internal static class Pictures
{
    public static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    public static bool IsPng(ReadOnlySpan<byte> b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
}
