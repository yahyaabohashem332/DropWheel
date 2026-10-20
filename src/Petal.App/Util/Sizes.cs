using System.Globalization;
using System.IO;
using Petal.App.Localization;

namespace Petal.App.Util;

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = [L.T("bytes"), L.T("KB"), L.T("MB"), L.T("GB"), L.T("TB")];
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        if (unit == 0)
            return L.P((int)bytes, "{0} byte", "{0} bytes");
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
            return L.T("Folder");
        if (folders == paths.Count)
            return L.P(folders, "{0} folder", "{0} folders");
        return Format(total);
    }
}
