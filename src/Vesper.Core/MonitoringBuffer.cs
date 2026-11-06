namespace Vesper.Core;

// Single audio-thread ownership. Bounded storage, fractional resampling and a
// slow water-level servo keep independent capture/render clocks from drifting.
public sealed class MonitoringBuffer
{
    private readonly float[] samples;
    private readonly double nominalStep;
    private readonly int target;
    private readonly bool immediate;
    private readonly int catchUpLimit;
    private readonly int servoTarget;
    private long written, read;
    private double fraction, correction;
    private bool primed;
    public long Underruns { get; private set; }
    public long DroppedSamples { get; private set; }
    public int Count => (int)(written - read);
    public double RateCorrection => correction;
    public int AvailableOutputFrames => (int)Math.Max(0, Math.Floor((Count - fraction) / (nominalStep * (1 + correction))));

    public MonitoringBuffer(int inputRate, int outputRate, int targetFrames, bool immediate = false, int maximumQueuedFrames = 0)
    {
        if (inputRate <= 0 || outputRate <= 0 || targetFrames < 2) throw new ArgumentOutOfRangeException();
        nominalStep = (double)inputRate / outputRate;
        target = targetFrames;
        this.immediate = immediate;
        int reserve = Math.Max(2, inputRate / 1000);
        servoTarget = immediate ? target + reserve : target;
        catchUpLimit = maximumQueuedFrames > 0 ? Math.Max(target, maximumQueuedFrames)
            : immediate ? target + reserve * 2 : target * 3;
        samples = new float[Math.Max(target * 4, inputRate / 10)];
    }
    public void Push(float sample)
    {
        if (Count == samples.Length) { read++; DroppedSamples++; }
        samples[written++ % samples.Length] = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
    }
    public void BeginRender()
    {
        double desired = Math.Clamp((Count - servoTarget) / (double)target * (immediate ? .004 : .001), -.002, .002);
        correction += (desired - correction) * .01;
        // Recover from a scheduling stall by dropping old audio instead of
        // letting monitoring latency grow indefinitely.
        if (Count > catchUpLimit)
        {
            int excess = Count - target;
            read += excess; DroppedSamples += excess; fraction = 0;
        }
        if (!primed && Count >= (immediate ? 1 : target)) primed = true;
    }
    public float Read()
    {
        if (!primed) return 0;
        double step = nominalStep * (1 + correction);
        int advance = (int)(fraction + step);
        if (Count < (immediate ? Math.Max(1, advance) : Math.Max(2, advance + 1)))
        {
            primed = false; fraction = 0; Underruns++; return 0;
        }
        float a = samples[read % samples.Length], b = Count > 1 ? samples[(read + 1) % samples.Length] : a;
        float value = a + (b - a) * (float)fraction;
        fraction += step; advance = (int)fraction;
        read += advance; fraction -= advance;
        return value;
    }
    public void DiscardQueued()
    {
        DroppedSamples += Count; read = written; fraction = 0; primed = false;
    }
}
