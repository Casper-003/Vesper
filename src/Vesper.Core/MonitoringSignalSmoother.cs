namespace Vesper.Core;

// Only amplitude envelopes; no look-ahead or delayed audio storage.
public sealed class MonitoringSignalSmoother
{
    private readonly float gainStep;
    private readonly int transitionFrames;
    private float gain, last, anchor;
    private int remaining;
    public MonitoringSignalSmoother(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        gainStep = (float)(1 - Math.Exp(-1d / (.003 * sampleRate)));
        transitionFrames = Math.Max(1, sampleRate / 500); // 2ms crossfade after discontinuity.
    }
    public void Discontinuity() { anchor = last; remaining = transitionFrames; }
    public float Process(float sample, float targetGain)
    {
        gain += (Math.Clamp(float.IsFinite(targetGain) ? targetGain : 0, 0, 1) - gain) * gainStep;
        float value = Math.Clamp(float.IsFinite(sample) ? sample : 0, -1, 1) * gain;
        if (remaining > 0) { value = anchor + (value - anchor) * (1 - remaining / (float)transitionFrames); remaining--; }
        return last = value;
    }
}
