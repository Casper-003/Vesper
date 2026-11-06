namespace Vesper.Core;

// Single audio-thread ownership. Bounded storage, fractional resampling and a
// slow water-level servo keep independent capture/render clocks from drifting.
public sealed class MonitoringBuffer
{
    private readonly float[] samples;
    private readonly double nominalStep;
    private readonly int target;
    private long written, read;
    private double fraction, correction;
    private bool primed;
    public long Underruns { get; private set; }
    public long DroppedSamples { get; private set; }
    public int Count => (int)(written - read);
    public double RateCorrection => correction;

    public MonitoringBuffer(int inputRate, int outputRate, int targetFrames)
    {
        if (inputRate <= 0 || outputRate <= 0 || targetFrames < 2) throw new ArgumentOutOfRangeException();
        nominalStep = (double)inputRate / outputRate;
        target = targetFrames;
        samples = new float[Math.Max(target * 4, inputRate / 10)];
    }
    public void Push(float sample)
    {
        if (Count == samples.Length) { read++; DroppedSamples++; }
        samples[written++ % samples.Length] = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
    }
    public void BeginRender()
    {
        double desired = Math.Clamp((Count - target) / (double)target * .001, -.002, .002);
        correction += (desired - correction) * .01;
        // Recover from a scheduling stall by dropping old audio instead of
        // letting monitoring latency grow indefinitely.
        if (Count > target * 3)
        {
            int excess = Count - target;
            read += excess; DroppedSamples += excess; fraction = 0;
        }
        if (!primed && Count >= target) primed = true;
    }
    public float Read()
    {
        if (!primed) return 0;
        double step = nominalStep * (1 + correction);
        int advance = (int)(fraction + step);
        if (Count < Math.Max(2, advance + 1))
        {
            primed = false; fraction = 0; Underruns++; return 0;
        }
        float a = samples[read % samples.Length], b = samples[(read + 1) % samples.Length];
        float value = a + (b - a) * (float)fraction;
        fraction += step; advance = (int)fraction;
        read += advance; fraction -= advance;
        return value;
    }
}
