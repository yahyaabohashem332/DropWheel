using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using Petal.App.Shell;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Petal.Tests;

/// <summary>
/// Drags that carry files by name and contents, the way Outlook, browsers and chat apps offer them: the
/// names are read, the contents written to Petal's own folder, and kept only when dropped on a petal.
/// </summary>
public sealed class VirtualDropTests : IDisposable
{
    private readonly List<string> _made = [];

    public void Dispose()
    {
        foreach (var path in _made)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_descriptor_gives_names_sizes_and_folders()
    {
        var bytes = Descriptor([("report.pdf", 1234, false), ("Trip", null, true), (@"Trip\day 1.jpg", 99, false)]);
        var files = VirtualDrop.ParseDescriptor(bytes, unicode: true);
        Assert.Equal(["report.pdf", "Trip", @"Trip\day 1.jpg"], files.Select(f => f.Name));
        Assert.Equal([1234L, null, 99L], files.Select(f => f.Size));
        Assert.Equal([false, true, false], files.Select(f => f.IsFolder));
        // A count that runs past the data, or none at all, is nothing.
        Assert.Empty(VirtualDrop.ParseDescriptor(bytes[..600], unicode: true));
        Assert.Empty(VirtualDrop.ParseDescriptor([], unicode: true));
    }

    [Fact]
    public void Files_another_app_offers_are_read_then_kept_in_Downloads()
    {
        var entries = new (string Name, byte[] Data)[]
        {
            ("invoice.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 test")),
            (@"..\..\escape.txt", Encoding.ASCII.GetBytes("never outside")),
            (@"Photos\beach.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]),
        };
        var staged = OnSta(() => VirtualDrop.Read(new DataObject(new FakeSource(entries))));
        Assert.NotNull(staged);
        Assert.True(VirtualDrop.IsStaged(staged));
        Assert.All(staged, p => Assert.StartsWith(VirtualDrop.StagingRoot, p));
        // The name that tried to climb out stays inside, as a plain name.
        Assert.Equal(["invoice.pdf", "escape.txt", "Photos"], staged.Select(Path.GetFileName));
        Assert.Equal("%PDF-1.4 test", File.ReadAllText(staged[0]));
        Assert.Equal(7, new FileInfo(Path.Combine(staged[2], "beach.jpg")).Length);

        var kept = VirtualDrop.Keep(staged);
        _made.AddRange(kept);
        Assert.All(kept, p => Assert.StartsWith(VirtualDrop.Downloads, p));
        Assert.True(File.Exists(kept[0]) && Directory.Exists(kept[2]));
        Assert.False(Directory.Exists(Path.GetDirectoryName(staged[0])));
    }

    [Fact]
    public void A_drag_that_goes_elsewhere_leaves_nothing_behind()
    {
        var staged = OnSta(() => VirtualDrop.Read(new DataObject(new FakeSource([("note.txt", [65, 66])]))));
        Assert.NotNull(staged);
        var folder = Path.GetDirectoryName(staged[0])!;
        Assert.True(Directory.Exists(folder));
        VirtualDrop.Forget(staged);
        Assert.False(Directory.Exists(folder));
        // Paths that aren't Petal's are never touched.
        var mine = Path.Combine(Path.GetTempPath(), "petal-not-staged.txt");
        File.WriteAllText(mine, "x");
        _made.Add(mine);
        VirtualDrop.Forget([mine]);
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void Ordinary_text_and_too_much_are_not_taken()
    {
        OnSta(() =>
        {
            var text = new DataObject(DataFormats.UnicodeText, "hello");
            Assert.False(VirtualDrop.Offers(text));
            Assert.Null(VirtualDrop.Read(text));
            var huge = new DataObject(new FakeSource([("big.bin", [1])], claimedSize: VirtualDrop.MostBytes + 1));
            Assert.True(VirtualDrop.Offers(huge));
            Assert.Null(VirtualDrop.Read(huge));
            return 0;
        });
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("On the STA thread: " + error);
        return result;
    }

    internal static byte[] Descriptor(IReadOnlyList<(string Name, long? Size, bool Folder)> entries)
    {
        var bytes = new byte[4 + entries.Count * 592];
        BitConverter.GetBytes(entries.Count).CopyTo(bytes, 0);
        for (int i = 0; i < entries.Count; i++)
        {
            int at = 4 + i * 592;
            var (name, size, folder) = entries[i];
            BitConverter.GetBytes(size is null ? 0 : 0x40).CopyTo(bytes, at);
            BitConverter.GetBytes(folder ? 0x10 : 0x80).CopyTo(bytes, at + 36);
            if (size is { } s)
            {
                BitConverter.GetBytes((int)(s >> 32)).CopyTo(bytes, at + 64);
                BitConverter.GetBytes((int)(s & 0xFFFFFFFF)).CopyTo(bytes, at + 68);
            }
            Encoding.Unicode.GetBytes(name).CopyTo(bytes, at + 72);
        }
        return bytes;
    }

    /// <summary>An app's drag source: a descriptor in memory and each file's contents as a stream, by index.</summary>
    private sealed class FakeSource((string Name, byte[] Data)[] entries, long? claimedSize = null) : ComDataObject
    {
        private readonly short _descriptor = (short)DataFormats.GetDataFormat("FileGroupDescriptorW").Id;
        private readonly short _contents = (short)DataFormats.GetDataFormat("FileContents").Id;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if (format.cfFormat == _descriptor)
            {
                var bytes = Descriptor(entries.Select(e => (e.Name, (long?)(claimedSize ?? e.Data.Length), false)).ToList());
                var global = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, global, bytes.Length);
                medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = global };
                return;
            }
            if (format.cfFormat == _contents && format.lindex >= 0 && format.lindex < entries.Length)
            {
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
                var data = entries[format.lindex].Data;
                stream.Write(data, data.Length, IntPtr.Zero);
                stream.Seek(0, 0, IntPtr.Zero);
                medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetIUnknownForObject(stream) };
                return;
            }
            Marshal.ThrowExceptionForHR(unchecked((int)0x80040064)); // DV_E_FORMATETC
        }

        public int QueryGetData(ref FORMATETC format) =>
            format.cfFormat == _descriptor || format.cfFormat == _contents ? 0 : unchecked((int)0x80040064);

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return unchecked((int)0x80040130); }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotImplementedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotImplementedException();
        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotImplementedException();
        public int EnumDAdvise(out IEnumSTATDATA? advise) { advise = null; return unchecked((int)0x80040003); }

        [DllImport("ole32.dll")]
        private static extern int CreateStreamOnHGlobal(IntPtr global, bool deleteOnRelease, out IStream stream);
    }
}
