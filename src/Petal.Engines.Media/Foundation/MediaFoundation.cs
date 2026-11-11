using System.Runtime.InteropServices;

namespace Petal.Engines.Media.Foundation;

// Windows' Media Foundation, as far as DropWheel uses it: the Source Reader, which opens a file and gives decoded
// pictures and sound with Windows' own decoders, and the Sink Writer, which encodes with Windows' own encoders.
// The interfaces list every method in vtable order, as the SDK headers (mfidl.h, mfobjects.h, mfreadwrite.h)
// declare them; only the ones DropWheel calls are used.

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public long Long;
    [FieldOffset(8)] public ulong ULong;

    public const ushort VtI8 = 20;
    public const ushort VtUi8 = 21;

    public static PropVariant FromLong(long value) => new() { Type = VtI8, Long = value };
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfOffset
{
    public ushort Fract;
    public short Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfVideoArea
{
    public MfOffset OffsetX;
    public MfOffset OffsetY;
    public int Width;
    public int Height;
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, uint size, out uint written);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, uint size, out uint written);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);

    [PreserveSig] int GetMajorType(out Guid type);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType type, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
    [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
    [PreserveSig] int IsContiguousFormat(out int contiguous);
    [PreserveSig] int GetContiguousLength(out uint length);
    [PreserveSig] int ContiguousCopyTo(IntPtr destination, uint length);
    [PreserveSig] int ContiguousCopyFrom(IntPtr source, uint length);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, uint size, out uint written);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);

    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint stream, out int selected);
    [PreserveSig] int SetStreamSelection(uint stream, int selected);
    [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
    [PreserveSig] int SetCurrentPosition(in Guid timeFormat, in PropVariant position);
    [PreserveSig] int ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint stream);
    [PreserveSig] int GetServiceForStream(uint stream, in Guid service, in Guid iid, out IntPtr value);
    [PreserveSig] int GetPresentationAttribute(uint stream, in Guid attribute, out PropVariant value);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType targetType, out uint stream);
    [PreserveSig] int SetInputMediaType(uint stream, IMFMediaType inputType, IMFAttributes? encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(uint stream, IMFSample sample);
    [PreserveSig] int SendStreamTick(uint stream, long timestamp);
    [PreserveSig] int PlaceMarker(uint stream, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(uint stream);
    [PreserveSig] int Flush(uint stream);
    [PreserveSig] int Finalize_();
    [PreserveSig] int GetServiceForStream(uint stream, in Guid service, in Guid iid, out IntPtr value);
    [PreserveSig] int GetStatistics(uint stream, IntPtr statistics);
}

internal static class MF
{
    public const uint Version = 0x00020070;
    public const uint StartupFull = 0;

    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint FirstAudioStream = 0xFFFFFFFD;
    public const uint AllStreams = 0xFFFFFFFE;
    public const uint MediaSource = 0xFFFFFFFF;

    public const uint ReaderError = 0x1;
    public const uint ReaderEndOfStream = 0x2;
    public const uint ReaderNewStream = 0x4;
    public const uint ReaderNativeTypeChanged = 0x10;
    public const uint ReaderCurrentTypeChanged = 0x20;
    public const uint ReaderStreamTick = 0x100;

    public const int NoMoreTypes = unchecked((int)0xC00D36B9);
    public const int InvalidStreamNumber = unchecked((int)0xC00D36B3);
    public const int TopoCodecNotFound = unchecked((int)0xC00D5212);
    public const int InvalidMediaType = unchecked((int)0xC00D36B4);
    public const int UnsupportedByteStreamType = unchecked((int)0xC00D36C4);

    private static Guid Fourcc(string code) =>
        new((uint)(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");

    public static readonly Guid Nv12 = Fourcc("NV12");
    public static readonly Guid P010 = Fourcc("P010");
    public static readonly Guid AudioFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioPcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid Wmv3 = Fourcc("WMV3");
    public static readonly Guid WmaV8 = new("00000161-0000-0010-8000-00AA00389B71");

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid AudioSampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");

    public static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new WindowsMediaException(what, hr);
    }

    public static (uint High, uint Low) Split(ulong value) => ((uint)(value >> 32), (uint)value);

    public static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;
}

/// <summary>Windows' Media Foundation refused something; <see cref="HResult"/> says what.</summary>
internal sealed class WindowsMediaException(string what, int hr)
    : Exception($"{what} failed (0x{hr:X8})")
{
    public new int HResult { get; } = hr;

    /// <summary>Windows has no decoder or encoder for the format: an extension from the Microsoft Store may add one.</summary>
    public bool IsMissingCodec => HResult is MF.TopoCodecNotFound or MF.InvalidMediaType or MF.UnsupportedByteStreamType;
}

/// <summary>Media Foundation started once for the whole process, as it should be.</summary>
internal static class MediaFoundationRuntime
{
    private static readonly Lazy<int> Started = new(() => MF.MFStartup(MF.Version, MF.StartupFull), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts Media Foundation; throws when this Windows has none (an N edition without the Media Feature Pack).</summary>
    public static void Ensure()
    {
        int hr;
        try
        {
            hr = Started.Value;
        }
        catch (DllNotFoundException e)
        {
            throw new MediaException(WindowsMedia.MissingText, e);
        }
        if (hr < 0)
            throw new MediaException(WindowsMedia.MissingText, new WindowsMediaException("MFStartup", hr));
    }
}
