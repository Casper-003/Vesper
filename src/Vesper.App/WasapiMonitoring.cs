using System.Runtime.InteropServices;
using Vesper.Core;
using static Vesper.App.WasapiInterop;

namespace Vesper.App;

internal enum MonitoringMode { LowLatency, Exclusive }
internal sealed record MonitoringDevice(string Id, string Name) { public override string ToString() => Name; }
internal sealed record MonitoringInfo(int InputRate, int OutputRate, uint InputPeriod, uint OutputPeriod, bool Raw, bool Exclusive = false)
{
    public override string ToString() => $"返听中 · 输入周期 {InputPeriod * 1000d / InputRate:F1} ms / 输出周期 {OutputPeriod * 1000d / OutputRate:F1} ms（非总延迟）"
        + (Exclusive ? " · 独占干声返听" : Raw ? " · 即到即送，输出最多排队一个周期" : "");
}

// COM clients, events, buffers and MMCSS are owned by one dedicated MTA thread.
// No allocations, locks, file IO, decoding or UI calls on the steady-state path.
internal sealed class WasapiMonitoring : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource<MonitoringInfo> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string inputId, outputId;
    private readonly MonitoringMode mode;
    private readonly Action<Exception> failed;
    private int disposed, launched;
    private float gain = .2f;
    public Task<MonitoringInfo> Started => started.Task;
    public Task Finished => finished.Task;
    public long Underruns { get; private set; }
    public long DroppedSamples { get; private set; }
    public long CaptureDiscontinuities { get; private set; }
    public long OutputShortages { get; private set; }
    public float Gain { set => Volatile.Write(ref gain, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0); }

    internal WasapiMonitoring(string inputId, string outputId, MonitoringMode mode, Action<Exception> failed)
    { this.inputId = inputId; this.outputId = outputId; this.mode = mode; this.failed = failed; }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref launched, 1) != 0) throw new InvalidOperationException("返听线程已经启动。");
        var thread = new Thread(Run) { IsBackground = true, Name = "Vesper WASAPI monitoring" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    public void Dispose()
    {
        // The worker disposes its own stop handle after it no longer waits on it.
        if (Interlocked.Exchange(ref disposed, 1) == 0) { lock (stop) { if (!finished.Task.IsCompleted) stop.Set(); } }
    }
    internal static IReadOnlyList<MonitoringDevice> Devices(bool capture)
    {
        Check(CoInitializeEx(0, 0));
        IDevices? devices = null; ICollection? collection = null;
        try
        {
            devices = (IDevices)new Enumerator();
            Check(devices.EnumAudioEndpoints(capture ? 1 : 0, 1, out nint pointer));
            try { collection = (ICollection)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            Check(collection.GetCount(out uint count));
            var result = new List<MonitoringDevice>();
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var device));
                IPropertyStore? properties = null;
                try
                {
                    Check(device.GetId(out string id)); Check(device.OpenPropertyStore(0, out pointer));
                    try { properties = (IPropertyStore)Marshal.GetObjectForIUnknown(pointer); }
                    finally { Marshal.Release(pointer); }
                    var key = new PropertyKey { Id = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Index = 14 };
                    Check(properties.GetValue(ref key, out var value));
                    try { result.Add(new(id, value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "音频设备" : "音频设备")); }
                    finally { PropVariantClear(ref value); }
                }
                finally { Release(properties); Release(device); }
            }
            return result;
        }
        finally { Release(collection); Release(devices); CoUninitialize(); }
    }

    // Metadata only: does not initialize/start a capture stream or read samples.
    internal static object Probe(MonitoringDevice selected)
    {
        Check(CoInitializeEx(0, 0)); IDevices? devices = null; IDevice? device = null; IClient? client = null; nint format = 0;
        try
        {
            devices = (IDevices)new Enumerator(); Check(devices.GetDevice(selected.Id, out device));
            Guid iid = ClientId; Check(device.Activate(ref iid, 23, 0, out object instance)); client = (IClient)instance;
            var properties = new ClientProperties { Size = (uint)Marshal.SizeOf<ClientProperties>(), Category = 11, Options = 1 };
            int rawStatus = client.SetClientProperties(ref properties);
            if (rawStatus < 0) { properties.Options = 0; Check(client.SetClientProperties(ref properties)); }
            Check(client.GetMixFormat(out format));
            int rate = Marshal.ReadInt32(format, 4);
            int periodStatus = client.GetSharedModeEnginePeriod(format, out uint standard, out uint fundamental, out uint min, out uint max);
            Check(client.GetDevicePeriod(out long deviceDefault, out long deviceMinimum));
            return new { selected.Name, SampleRate = rate, RawSupported = rawStatus >= 0, RawStatus = rawStatus.ToString("X8"),
                PeriodSupported = periodStatus >= 0, PeriodStatus = periodStatus.ToString("X8"), DefaultFrames = standard,
                FundamentalFrames = fundamental, MinimumFrames = min, MaximumFrames = max,
                DeviceDefaultMilliseconds = deviceDefault / 10000d, DeviceMinimumMilliseconds = deviceMinimum / 10000d };
        }
        finally { if (format != 0) Marshal.FreeCoTaskMem(format); Release(client); Release(device); Release(devices); CoUninitialize(); }
    }

    private sealed class Endpoint : IDisposable
    {
        internal readonly IClient Client;
        internal readonly AutoResetEvent Event = new(false);
        internal readonly int Rate, Channels, Bits, Bytes;
        internal readonly bool Float;
        internal readonly uint Period, Capacity;
        internal Endpoint(IDevices devices, string id, MonitoringMode mode)
        {
            Check(devices.GetDevice(id, out var device));
            IClient? activated = null; nint format = 0;
            try
            {
                Guid iid = ClientId; Check(device.Activate(ref iid, 23, 0, out object value));
                activated = (IClient)value; Client = activated;
                var properties = new ClientProperties { Size = (uint)Marshal.SizeOf<ClientProperties>(), Category = 11,
                    Options = mode == MonitoringMode.LowLatency ? 1u : 0u }; // Media, RAW via IAudioClient2.
                Check(Client.SetClientProperties(ref properties));
                Check(Client.GetMixFormat(out format));
                if (mode == MonitoringMode.Exclusive)
                {
                    nint selected = SelectExclusiveFormat(Client, format);
                    Marshal.FreeCoTaskMem(format); format = selected;
                }
                int tag = (ushort)Marshal.ReadInt16(format); Channels = (ushort)Marshal.ReadInt16(format, 2);
                Rate = Marshal.ReadInt32(format, 4); Bytes = (ushort)Marshal.ReadInt16(format, 12);
                Bits = (ushort)Marshal.ReadInt16(format, 14);
                if (tag == 0xfffe)
                {
                    Guid sub = Marshal.PtrToStructure<Guid>(format + 24);
                    tag = sub == new Guid("00000003-0000-0010-8000-00AA00389B71") ? 3
                        : sub == new Guid("00000001-0000-0010-8000-00AA00389B71") ? 1 : 0;
                }
                Float = tag == 3;
                if (Rate <= 0 || Channels is < 1 or > 32 || Bytes != Channels * Bits / 8
                    || !(Float && Bits == 32 || tag == 1 && Bits is 16 or 24 or 32))
                    throw new NotSupportedException("设备音频格式暂不支持，请更换设备或系统默认格式。");
                if (mode == MonitoringMode.LowLatency)
                {
                    Check(Client.GetSharedModeEnginePeriod(format, out _, out uint fundamental, out uint min, out uint max));
                    if (fundamental == 0) throw new NotSupportedException("设备没有提供合法的低延迟周期。");
                    Period = checked((min + fundamental - 1) / fundamental * fundamental);
                    if (Period > max) throw new NotSupportedException("设备没有提供合法的低延迟周期。");
                    Check(Client.InitializeSharedAudioStream(EventCallback, Period, format, 0));
                }
                else
                {
                    Check(Client.GetDevicePeriod(out _, out long minimum));
                    long duration = Math.Max(1, minimum);
                    int result = Client.Initialize(1, EventCallback, duration, duration, format, 0);
                    if (result == unchecked((int)0x88890019)) // AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
                    {
                        Check(Client.GetBufferSize(out uint alignedFrames));
                        duration = (long)Math.Round(10000000d * alignedFrames / Rate);
                        Release(activated); activated = null;
                        Check(device.Activate(ref iid, 23, 0, out value)); Client = activated = (IClient)value;
                        Check(Client.SetClientProperties(ref properties));
                        result = Client.Initialize(1, EventCallback, duration, duration, format, 0);
                    }
                    Check(result);
                }
                Check(Client.GetBufferSize(out Capacity));
                if (mode == MonitoringMode.Exclusive) Period = Capacity;
                Check(Client.SetEventHandle(Event.SafeWaitHandle.DangerousGetHandle()));
            }
            catch { Release(activated); Event.Dispose(); throw; }
            finally { if (format != 0) Marshal.FreeCoTaskMem(format); Release(device); }
        }
        internal float Read(nint data, int frame)
        {
            double sample = 0;
            for (int c = 0; c < Channels; c++)
            {
                nint p = data + frame * Bytes + c * Bits / 8;
                sample += Float ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p))
                    : Bits == 16 ? Marshal.ReadInt16(p) / 32768d
                    : Bits == 32 ? Marshal.ReadInt32(p) / 2147483648d
                    : ((Marshal.ReadByte(p) | Marshal.ReadByte(p, 1) << 8 | Marshal.ReadByte(p, 2) << 16) << 8) / 2147483648d;
            }
            return (float)(sample / Channels);
        }
        internal void Write(nint data, int frame, float sample)
        {
            for (int c = 0; c < Channels; c++)
            {
                float value = c < 2 ? Math.Clamp(sample, -1, 1) : 0;
                nint p = data + frame * Bytes + c * Bits / 8;
                if (Float) Marshal.WriteInt32(p, BitConverter.SingleToInt32Bits(value));
                else if (Bits == 16) Marshal.WriteInt16(p, (short)Math.Clamp((int)(value * 32767), -32768, 32767));
                else if (Bits == 32) Marshal.WriteInt32(p, (int)(value * 2147483647d));
                else
                {
                    int pcm = (int)(value * 8388607); Marshal.WriteByte(p, (byte)pcm);
                    Marshal.WriteByte(p, 1, (byte)(pcm >> 8)); Marshal.WriteByte(p, 2, (byte)(pcm >> 16));
                }
            }
        }
        public void Dispose() { Client.Stop(); Release(Client); Event.Dispose(); }
    }

    internal static object ProbeInitialization(MonitoringDevice? source, MonitoringDevice destination, MonitoringMode selectedMode)
    {
        Check(CoInitializeEx(0, 0)); IDevices? devices = null;
        try
        {
            devices = (IDevices)new Enumerator();
            using var input = source is null ? null : new Endpoint(devices, source.Id, selectedMode);
            using var output = new Endpoint(devices, destination.Id, selectedMode);
            return new { Mode = selectedMode.ToString(), Available = input is not null, InputInitialized = input is not null, OutputInitialized = true,
                InputDevice = source?.Name, OutputDevice = destination.Name,
                InputRate = input?.Rate, OutputRate = output.Rate, InputFrames = input?.Period, OutputFrames = output.Period,
                InputMilliseconds = input is null ? (double?)null : input.Period * 1000d / input.Rate, OutputMilliseconds = output.Period * 1000d / output.Rate,
                OutputBufferCapacity = output.Capacity };
        }
        catch (Exception error) { return new { Mode = selectedMode.ToString(), Available = false, Error = error.Message, HResult = error.HResult.ToString("X8") }; }
        finally { Release(devices); CoUninitialize(); }
    }

    private static nint SelectExclusiveFormat(IClient client, nint mixFormat)
    {
        // Shared mix formats often use float, while USB hardware accepts PCM.
        int originalRate = Marshal.ReadInt32(mixFormat, 4);
        int channels = (ushort)Marshal.ReadInt16(mixFormat, 2);
        foreach (int rate in new[] { originalRate, 48000, 44100 })
        foreach (int channelCount in new[] { channels, Math.Min(2, channels) }.Distinct())
        foreach (int bits in new[] { 16, 32, 24 })
        {
            nint candidate = Marshal.AllocCoTaskMem(18);
            for (int b = 0; b < 18; b++) Marshal.WriteByte(candidate, b, 0);
            int bytes = channelCount * bits / 8;
            Marshal.WriteInt16(candidate, 1); Marshal.WriteInt16(candidate, 2, (short)channelCount);
            Marshal.WriteInt32(candidate, 4, rate); Marshal.WriteInt32(candidate, 8, rate * bytes);
            Marshal.WriteInt16(candidate, 12, (short)bytes); Marshal.WriteInt16(candidate, 14, (short)bits);
            int result = client.IsFormatSupported(1, candidate, out nint closest);
            if (closest != 0) Marshal.FreeCoTaskMem(closest);
            if (result == 0) return candidate;
            Marshal.FreeCoTaskMem(candidate);
        }
        throw new NotSupportedException("设备未提供受支持的独占 PCM 格式，请改用共享低延迟。");
    }

    private void Run()
    {
        IDevices? devices = null; Endpoint? input = null, output = null;
        ICapture? capture = null; IRender? render = null; nint mmcss = 0; bool com = false;
        try
        {
            Check(CoInitializeEx(0, 0)); com = true;
            if (stop.WaitOne(0)) throw new OperationCanceledException();
            devices = (IDevices)new Enumerator();
            input = new(devices, inputId, mode); output = new(devices, outputId, mode);
            Guid iid = CaptureId; Check(input.Client.GetService(ref iid, out object service)); capture = (ICapture)service;
            iid = RenderId; Check(output.Client.GetService(ref iid, out service)); render = (IRender)service;
            bool exclusive = mode == MonitoringMode.Exclusive;
            int renderInputFrames = checked((int)Math.Ceiling(output.Period * (double)input.Rate / output.Rate));
            int target = Math.Max(2, exclusive ? (int)input.Period + renderInputFrames : Math.Max((int)input.Period, renderInputFrames));
            int maximumQueued = exclusive ? target + Math.Max((int)input.Period, renderInputFrames) + input.Rate / 500 : 0;
            var buffer = new MonitoringBuffer(input.Rate, output.Rate, target, immediate: true, maximumQueuedFrames: maximumQueued);
            WaitHandle[] events = [stop, input.Event, output.Event];
            bool outputStarted = false;
            if (stop.WaitOne(0)) throw new OperationCanceledException();
            mmcss = AvSetMmThreadCharacteristics("Pro Audio", out _);
            Check(input.Client.Start());
            var smoother = new MonitoringSignalSmoother(output.Rate);
            bool outputGap = false;
            int signal;
            while ((signal = WaitHandle.WaitAny(events, 2000)) != 0)
            {
                if (stop.WaitOne(0)) break;
                if (signal == WaitHandle.WaitTimeout) throw new TimeoutException("音频设备没有响应。");
                Check(capture.GetNextPacketSize(out uint packet));
                int packets = 0;
                while (packet > 0 && packets++ < 64)
                {
                    Check(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                    try
                    {
                        if ((flags & 1) != 0)
                        { if (outputStarted) CaptureDiscontinuities++; buffer.DiscardQueued(); smoother.Discontinuity(); }
                        for (int f = 0; f < frames; f++) buffer.Push((flags & 2) != 0 ? 0 : input.Read(data, f));
                    }
                    finally { Check(capture.ReleaseBuffer(frames)); }
                    Check(capture.GetNextPacketSize(out packet));
                    if (stop.WaitOne(0)) break;
                }
                // Exclusive event-driven streams must acquire a complete hardware
                // buffer only on the render event, not on capture wakeups.
                if (exclusive && outputStarted && signal != 2) continue;
                uint padding = 0;
                if (!exclusive) Check(output.Client.GetCurrentPadding(out padding));
                if (!exclusive && (buffer.Count == 0 || padding >= Math.Min(output.Period, output.Capacity)))
                {
                    if (padding == 0 && outputStarted)
                    { if (!outputGap && signal == 2) OutputShortages++; outputGap = true; }
                    continue;
                }
                long droppedBefore = buffer.DroppedSamples;
                // Exclusive hardware cannot accept a partial output packet. Keep
                // one capture-packet reserve so unequal device periods don't cause
                // periodic gaps; these are the probed small hardware periods.
                if (exclusive && !outputStarted && buffer.Count < target) continue;
                buffer.BeginRender();
                if (outputGap || buffer.DroppedSamples != droppedBefore) { smoother.Discontinuity(); outputGap = false; }
                if (exclusive && !outputStarted && buffer.AvailableOutputFrames < output.Capacity) continue;
                uint available = exclusive ? output.Capacity
                    : MonitoringRenderQueue.FramesToWrite(output.Capacity, padding, output.Period, buffer.AvailableOutputFrames);
                if (available == 0) continue;
                Check(render.GetBuffer(available, out nint destination));
                try
                {
                    float level = Volatile.Read(ref gain);
                    int sourceFrames = buffer.AvailableOutputFrames;
                    bool shortage = false;
                    for (int f = 0; f < available; f++)
                    {
                        bool missing = f >= sourceFrames;
                        shortage |= missing;
                        if (missing && !outputGap) { smoother.Discontinuity(); outputGap = true; }
                        output.Write(destination, f, smoother.Process(missing ? 0 : buffer.Read(), level));
                    }
                    if (shortage) OutputShortages++;
                }
                finally { Check(render.ReleaseBuffer(available, 0)); }
                if (!outputStarted)
                {
                    // Start on actual microphone data rather than a full device
                    // buffer of silence that all later speech would sit behind.
                    Check(output.Client.Start()); outputStarted = true;
                    started.TrySetResult(new(input.Rate, output.Rate, input.Period, output.Period, !exclusive, exclusive));
                }
                Underruns = buffer.Underruns; DroppedSamples = buffer.DroppedSamples;
            }
            if (outputStarted && Volatile.Read(ref disposed) != 0)
            {
                // Bounded shutdown on the audio thread. Fade the last live sample
                // to zero before releasing the device; this is not steady-state buffering.
                input.Client.Stop(); smoother.Discontinuity();
                int tails = Math.Clamp((int)Math.Ceiling(output.Rate * .002 / output.Period), 1, 4);
                for (int block = 0; block < tails && output.Event.WaitOne(50); block++)
                {
                    uint queued = 0; if (!exclusive) Check(output.Client.GetCurrentPadding(out queued));
                    uint frames = exclusive ? output.Capacity
                        : Math.Min(output.Period, output.Capacity - Math.Min(queued, output.Capacity));
                    if (frames == 0) continue;
                    Check(render.GetBuffer(frames, out nint tail));
                    try { for (int f = 0; f < frames; f++) output.Write(tail, f, smoother.Process(0, 0)); }
                    finally { Check(render.ReleaseBuffer(frames, 0)); }
                }
                output.Event.WaitOne(50);
            }
        }
        catch (Exception error)
        {
            if (!started.TrySetException(error) && Volatile.Read(ref disposed) == 0) failed(error);
        }
        finally
        {
            try
            {
                input?.Client.Stop(); output?.Client.Stop();
                Release(capture); Release(render); input?.Dispose(); output?.Dispose(); Release(devices);
            }
            finally
            {
                if (mmcss != 0) AvRevertMmThreadCharacteristics(mmcss);
                if (com) CoUninitialize();
                started.TrySetCanceled();
                lock (stop) { finished.TrySetResult(); stop.Dispose(); }
            }
        }
    }
}
