using System.IO;

namespace Petal.App;

/// <summary>A small rolling log in %LOCALAPPDATA%\Petal\petal.log for diagnosing failed jobs.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    /// <summary>Beside the settings when PETAL_SETTINGS_DIR moves them (tests and design snapshots), so those runs stay out of the real log.</summary>
    public static string FilePath { get; set; } = Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR") is { Length: > 0 } own
        ? Path.Combine(own, "petal.log")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "petal.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception ex, string context) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
