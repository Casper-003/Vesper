using System.Runtime.InteropServices;

namespace Vesper.App;

internal static class WasapiInterop
{
    internal static readonly Guid ClientId = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    internal static readonly Guid CaptureId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    internal static readonly Guid RenderId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal const uint EventCallback = 0x40000;
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] internal static extern nint AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AvRevertMmThreadCharacteristics(nint handle);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class Enumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevices
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        [PreserveSig] int OpenPropertyStore(uint access, out nint store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid Id; internal uint Index; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropertyValue
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropertyValue value);
    [StructLayout(LayoutKind.Sequential)] internal struct ClientProperties
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.Bool)] internal bool Offload;
        internal int Category;
        internal uint Options;
    }
    // Full inherited vtable: IAudioClient, IAudioClient2, then IAudioClient3.
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long standard, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool capable);
        [PreserveSig] int SetClientProperties(ref ClientProperties properties);
        [PreserveSig] int GetBufferSizeLimits(nint format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long min, out long max);
        [PreserveSig] int GetSharedModeEnginePeriod(nint format, out uint standard, out uint fundamental, out uint min, out uint max);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out nint format, out uint frames);
        [PreserveSig] int InitializeSharedAudioStream(uint flags, uint period, nint format, nint session);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICapture
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IRender
    {
        [PreserveSig] int GetBuffer(uint frames, out nint data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
