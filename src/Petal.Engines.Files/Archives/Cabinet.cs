using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Petal.Engines.Files.Archives;

/// <summary>
/// Windows cabinets (.cab, [MS-CAB]). Read through Windows' own Cabinet API (cabinet.dll's FDI), which knows every
/// compression a cabinet may use (MSZIP, LZX, Quantum); written by DropWheel itself, MSZIP, names in UTF-8.
/// </summary>
internal static class Cabinet
{
    // ---- Reading (FDI) ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocFn(uint cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenFn(IntPtr name, int flags, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadFn(IntPtr hf, IntPtr pv, uint cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint WriteFn(IntPtr hf, IntPtr pv, uint cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr hf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SeekFn(IntPtr hf, int dist, int origin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NotifyFn(int type, IntPtr notification);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FDICreate(AllocFn alloc, FreeFn free, OpenFn open, ReadFn read, WriteFn write, CloseFn close, SeekFn seek, int cpu, IntPtr erf);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDICopy(IntPtr hfdi, string cabinet, string path, int flags, NotifyFn notify, IntPtr decrypt, IntPtr user);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDIDestroy(IntPtr hfdi);

    private const int CopyFile = 2, CloseFileInfo = 3;
    private const ushort NameIsUtf8 = 0x80;

    /// <summary>A file the cabinet held: where it was put, its path inside, and when it was last changed.</summary>
    public sealed record Extracted(string FullPath, string Path, DateTime? Modified);

    /// <summary>Extracts every file of the cabinet into <paramref name="folder"/>, keeping its folders.</summary>
    public static List<Extracted> Extract(string cabinet, string folder)
    {
        var handles = new Dictionary<IntPtr, Stream>();
        var next = 1L;
        var extracted = new List<Extracted>();
        var pending = new Dictionary<IntPtr, (string Full, string Path)>();
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Exception? failure = null;

        IntPtr Register(Stream stream)
        {
            var handle = (IntPtr)next++;
            handles[handle] = stream;
            return handle;
        }

        AllocFn alloc = cb => Marshal.AllocHGlobal((int)cb);
        FreeFn free = Marshal.FreeHGlobal;
        // FDI asks only for the cabinet itself, by the token passed to FDICopy; the real path may hold any letters.
        OpenFn open = (_, _, _) =>
        {
            try
            {
                return Register(new FileStream(cabinet, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            catch (Exception e)
            {
                failure = e;
                return -1;
            }
        };
        ReadFn read = (hf, pv, cb) =>
        {
            if (!handles.TryGetValue(hf, out var s)) return unchecked((uint)-1);
            var buffer = new byte[cb];
            var n = s.Read(buffer, 0, (int)cb);
            Marshal.Copy(buffer, 0, pv, n);
            return (uint)n;
        };
        WriteFn write = (hf, pv, cb) =>
        {
            if (!handles.TryGetValue(hf, out var s)) return unchecked((uint)-1);
            var buffer = new byte[cb];
            Marshal.Copy(pv, buffer, 0, (int)cb);
            s.Write(buffer, 0, (int)cb);
            return cb;
        };
        CloseFn close = hf =>
        {
            if (handles.Remove(hf, out var s))
                s.Dispose();
            return 0;
        };
        SeekFn seek = (hf, dist, origin) => handles.TryGetValue(hf, out var s) ? (int)s.Seek(dist, (SeekOrigin)origin) : -1;
        NotifyFn notify = (type, info) =>
        {
            try
            {
                if (type == CopyFile)
                {
                    var attribs = (ushort)Marshal.ReadInt16(info, 52);
                    var namePtr = Marshal.ReadIntPtr(info, 8);
                    var name = (attribs & NameIsUtf8) != 0 ? Marshal.PtrToStringUTF8(namePtr) : Marshal.PtrToStringAnsi(namePtr);
                    if (string.IsNullOrEmpty(name))
                        return 0;
                    var relative = ArchiveIO.Normalize(name);
                    var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    // A name that would land outside the folder is skipped.
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return 0;
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    var handle = Register(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None));
                    pending[handle] = (full, relative);
                    return handle;
                }
                if (type == CloseFileInfo)
                {
                    var hf = Marshal.ReadIntPtr(info, 40);
                    var date = (ushort)Marshal.ReadInt16(info, 48);
                    var time = (ushort)Marshal.ReadInt16(info, 50);
                    close(hf);
                    if (pending.Remove(hf, out var file))
                        extracted.Add(new Extracted(file.Full, file.Path, DosTime(date, time)));
                    return 1;
                }
                return 0;
            }
            catch (Exception e)
            {
                failure = e;
                return -1;
            }
        };

        var erf = Marshal.AllocHGlobal(16);
        try
        {
            var fdi = FDICreate(alloc, free, open, read, write, close, seek, -1, erf);
            if (fdi == IntPtr.Zero)
                throw new InvalidOperationException("Windows' Cabinet API could not start.");
            try
            {
                if (!FDICopy(fdi, "cabinet", "", 0, notify, IntPtr.Zero, IntPtr.Zero))
                {
                    if (failure is not null)
                        throw failure;
                    throw new InvalidDataException(Marshal.ReadInt32(erf) switch
                    {
                        2 or 3 => "This is not a Windows cabinet, or it is damaged.",
                        4 => "This cabinet is from a version Windows doesn't know.",
                        5 or 6 or 8 => "This cabinet is damaged.",
                        _ => "This cabinet could not be read.",
                    });
                }
            }
            finally
            {
                FDIDestroy(fdi);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(erf);
            foreach (var s in handles.Values)
                s.Dispose();
            GC.KeepAlive(alloc);
            GC.KeepAlive(free);
            GC.KeepAlive(open);
            GC.KeepAlive(read);
            GC.KeepAlive(write);
            GC.KeepAlive(close);
            GC.KeepAlive(seek);
            GC.KeepAlive(notify);
        }
        return extracted;
    }

    private static DateTime? DosTime(ushort date, ushort time)
    {
        try
        {
            return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 0xF), Math.Max(1, date & 0x1F), time >> 11, (time >> 5) & 0x3F, Math.Min(59, (time & 0x1F) * 2));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // ---- Writing (MSZIP) ----

    private const int BlockSize = 32768;
    private const int MostBlocksInFolder = 60000;

    /// <summary>
    /// Writes a cabinet of the entries' files (a cabinet keeps no empty folders): MSZIP, each 32 KB block deflated on
    /// its own, with its checksum; a new folder every 60,000 blocks, as the format counts them in 16 bits.
    /// </summary>
    public static void Write(Stream output, IEnumerable<ArchiveEntry> entries, CancellationToken ct)
    {
        var files = new List<(string Name, uint Size, uint Offset, ushort Folder, DateTime Modified)>();
        var folders = new List<(long DataStart, int Blocks)>();
        using var data = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        var block = new byte[BlockSize];
        var filled = 0;
        long folderOffset = 0;
        var blocks = 0;
        folders.Add((0, 0));

        void Flush()
        {
            if (filled == 0)
                return;
            if (blocks == ushort.MaxValue)
                throw new NotSupportedException("A cabinet's file is under 2 GB.");
            using var compressed = new MemoryStream();
            compressed.Write("CK"u8);
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(block, 0, filled);
            var bytes = compressed.ToArray();
            var header = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)filled);
            BinaryPrimitives.WriteUInt32LittleEndian(header, Checksum(header.AsSpan(4, 4), Checksum(bytes, 0)));
            data.Write(header);
            data.Write(bytes);
            filled = 0;
            blocks++;
            folders[^1] = (folders[^1].DataStart, blocks);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                continue;
            if (blocks >= MostBlocksInFolder)
            {
                Flush();
                folders.Add((data.Position, 0));
                blocks = 0;
                folderOffset = 0;
            }
            using var source = entry.Open();
            var start = folderOffset;
            int n;
            while ((n = source.Read(block, filled, BlockSize - filled)) > 0)
            {
                filled += n;
                folderOffset += n;
                if (filled == BlockSize)
                    Flush();
            }
            var size = folderOffset - start;
            if (size > uint.MaxValue || folderOffset > uint.MaxValue)
                throw new NotSupportedException("A cabinet holds files of under 4 GB.");
            if (files.Count == ushort.MaxValue)
                throw new NotSupportedException("A cabinet holds at most 65,535 files.");
            files.Add((entry.Path, (uint)size, (uint)start, (ushort)(folders.Count - 1), (entry.Modified ?? DateTimeOffset.Now).LocalDateTime));
        }
        Flush();
        if (files.Count == 0)
            throw new InvalidDataException("There are no files to put in a cabinet.");

        // The header, the folders and the files' list come first; the blocks after them.
        var names = files.Select(f => Encoding.UTF8.GetBytes(f.Name.Replace('/', '\\'))).ToList();
        var headerSize = 36 + 8 * folders.Count;
        var listSize = names.Sum(n => 16 + n.Length + 1);
        var dataStart = headerSize + listSize;
        var total = dataStart + data.Length;
        if (total > uint.MaxValue)
            throw new NotSupportedException("A cabinet is under 4 GB.");
        var head = new byte[dataStart];
        "MSCF"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), (uint)headerSize);
        head[24] = 3;
        head[25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(26), (ushort)folders.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(28), (ushort)files.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(32), (ushort)Random.Shared.Next(1, 0xFFFF));
        for (var i = 0; i < folders.Count; i++)
        {
            var at = 36 + 8 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(at), (uint)(dataStart + folders[i].DataStart));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(at + 4), (ushort)folders[i].Blocks);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(at + 6), 1); // MSZIP
        }
        var pos = headerSize;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(pos), f.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(pos + 4), f.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(pos + 8), f.Folder);
            var year = Math.Clamp(f.Modified.Year, 1980, 2107);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(pos + 10), (ushort)(((year - 1980) << 9) | (f.Modified.Month << 5) | f.Modified.Day));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(pos + 12), (ushort)((f.Modified.Hour << 11) | (f.Modified.Minute << 5) | (f.Modified.Second / 2)));
            var ascii = names[i].All(b => b < 0x80);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(pos + 14), (ushort)(0x20 | (ascii ? 0 : NameIsUtf8)));
            names[i].CopyTo(head, pos + 16);
            pos += 16 + names[i].Length + 1;
        }
        output.Write(head);
        data.Position = 0;
        data.CopyTo(output);
    }

    /// <summary>[MS-CAB]'s checksum: the data's 32-bit little-endian words XORed together, the last few bytes taken high first.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> data, uint seed)
    {
        var sum = seed;
        var words = data.Length / 4;
        for (var i = 0; i < words; i++)
            sum ^= BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        uint last = 0;
        var rest = data[(words * 4)..];
        switch (rest.Length)
        {
            case 3: last = ((uint)rest[0] << 16) | ((uint)rest[1] << 8) | rest[2]; break;
            case 2: last = ((uint)rest[0] << 8) | rest[1]; break;
            case 1: last = rest[0]; break;
        }
        return sum ^ last;
    }
}
