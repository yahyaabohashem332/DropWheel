using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Petal.App.Util;

/// <summary>
/// Sends files to the Recycle Bin and brings them back. Restoring reads the bin's own records
/// ($I files: where each item came from), so it works in every Windows language.
/// </summary>
internal static class RecycleBin
{
    public static void Send(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    /// <summary>
    /// Puts recycled files back where they were (the most recently recycled one, for a path
    /// recycled more than once). A path that is taken meanwhile is left alone.
    /// </summary>
    /// <returns>The paths restored.</returns>
    public static IReadOnlyList<string> Restore(IReadOnlyList<string> originals)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null || originals.Count == 0)
            return [];
        var wanted = new HashSet<string>(originals.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, (string Info, DateTime Deleted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in wanted.Select(Path.GetPathRoot).Where(r => r is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.Combine(root!, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin))
                continue;
            IEnumerable<string> records;
            try
            {
                records = Directory.EnumerateFiles(bin, "$I*").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var record in records)
            {
                if (Read(record) is not { } item || !wanted.Contains(item.Original))
                    continue;
                if (!found.TryGetValue(item.Original, out var best) || item.Deleted > best.Deleted)
                    found[item.Original] = (record, item.Deleted);
            }
        }
        var restored = new List<string>();
        foreach (var (original, (info, _)) in found)
        {
            var content = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
            if (File.Exists(original) || Directory.Exists(original) || !File.Exists(content) && !Directory.Exists(content))
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (Directory.Exists(content))
                    Directory.Move(content, original);
                else
                    File.Move(content, original);
                File.Delete(info);
                restored.Add(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, $"Restoring {original} from the Recycle Bin");
            }
        }
        return restored;
    }

    /// <summary>A $I record: version 1 (Vista to 8.1, fixed 260 characters) or 2 (Windows 10 and later).</summary>
    internal static (string Original, DateTime Deleted)? Read(string record)
    {
        try
        {
            var bytes = File.ReadAllBytes(record);
            if (bytes.Length < 28)
                return null;
            long version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
            string path;
            if (version == 2)
            {
                int length = BitConverter.ToInt32(bytes, 24);
                if (length <= 0 || 28 + length * 2 > bytes.Length)
                    return null;
                path = Encoding.Unicode.GetString(bytes, 28, length * 2);
            }
            else if (version == 1 && bytes.Length >= 24 + 520)
            {
                path = Encoding.Unicode.GetString(bytes, 24, 520);
            }
            else
            {
                return null;
            }
            int end = path.IndexOf('\0');
            return (end >= 0 ? path[..end] : path, deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
