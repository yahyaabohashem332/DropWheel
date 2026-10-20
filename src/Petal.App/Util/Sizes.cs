using System.Globalization;
using System.IO;

namespace Petal.App.Util;

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        if (unit == 0)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Total size of files; folders count as items without measuring them (which could be slow).</summary>
    public static string Describe(IReadOnlyList<string> paths)
    {
        long total = 0;
        int folders = 0;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    folders++;
                else if (File.Exists(p))
                    total += new FileInfo(p).Length;
            }
            catch
            {
                // Unreadable entry.
            }
        }
        if (paths.Count == 1 && folders == 1)
            return "Folder";
        if (folders == paths.Count)
            return $"{folders} folders";
        return Format(total);
    }
}
