namespace Vesper.Core;

public static class MonitoringRenderQueue
{
    // Device buffer capacity is spare capacity, not a latency target. Only queue
    // one engine period of available live data, never fill empty space with silence.
    public static uint FramesToWrite(uint capacity, uint padding, uint period, int availableSourceFrames)
    {
        if (availableSourceFrames <= 0 || padding >= capacity || padding >= period) return 0;
        return Math.Min(Math.Min(capacity - padding, period - padding), (uint)availableSourceFrames);
    }
}
