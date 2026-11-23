using System.Runtime.InteropServices;
using Joveler.Compression.XZ;

namespace Petal.Engines.Files.Archives;

/// <summary>XZ compression through liblzma (XZ Utils 5.6.4, 0BSD), loaded once from beside DropWheel.</summary>
internal static class Xz
{
    private static readonly Lock Gate = new();
    private static bool _loaded;

    /// <summary>A stream that compresses what is written to it into <paramref name="output"/> as XZ (level 6, CRC64), on every core.</summary>
    public static Stream Compressing(Stream output)
    {
        Load();
        return new XZStream(output,
            new XZCompressOptions { Level = LzmaCompLevel.Default, Check = LzmaCheck.Crc64, LeaveOpen = true },
            new XZThreadedCompressOptions { Threads = Math.Max(1, Environment.ProcessorCount) });
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                Architecture.X86 => "win-x86",
                _ => "win-x64",
            };
            // A published DropWheel has it beside the program; a build from source, under runtimes\.
            var path = new[] { Path.Combine(AppContext.BaseDirectory, "liblzma.dll"), Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", "liblzma.dll") }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("liblzma.dll, which writes .tar.xz files, is missing from DropWheel's folder.");
            XZInit.GlobalInit(path);
            _loaded = true;
        }
    }
}
