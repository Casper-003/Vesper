using Vesper.Core;

internal static class MonitoringBufferChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var buffer = new MonitoringBuffer(48000, 48000, 8);
        check(buffer.Read() == 0, "返听启动缓冲不足时输出静音");
        for (int i = 0; i < 16; i++) buffer.Push(.25f);
        buffer.BeginRender();
        check(Math.Abs(buffer.Read() - .25f) < .00001, "返听环形缓冲保留人声幅度");
        for (int i = 0; i < 40; i++) buffer.Read();
        check(buffer.Read() == 0 && buffer.Underruns > 0, "返听欠载补静音且记录，不重复陈旧音频");
        for (int i = 0; i < 10000; i++) buffer.Push(.5f);
        buffer.BeginRender();
        check(buffer.Count <= 8 && buffer.DroppedSamples > 0, "调度停顿后丢弃过期人声，缓冲与延迟保持有界");
        var invalid = new MonitoringBuffer(48000, 48000, 8);
        for (int i = 0; i < 16; i++) invalid.Push(float.NaN);
        invalid.BeginRender(); check(invalid.Read() == 0, "无效音频样本不会扩散至输出");
        var ramp = new MonitoringBuffer(44100, 48000, 1000);
        for (int i = 0; i < 2000; i++) ramp.Push(i / 4000f);
        ramp.BeginRender();
        float last = 0;
        for (int i = 0; i <= 480; i++) last = ramp.Read();
        check(Math.Abs(last - 441 / 4000f) < .0002,
            "44.1k→48k 转换按真实采样率推进人声，未改变音高或播放速度");
        foreach (var pair in new[] { (44100, 48000), (48000, 44100), (48000, 48000) })
        {
            var stream = new MonitoringBuffer(pair.Item1, pair.Item2, pair.Item1 / 100 * 2);
            double captureFrames = 0; bool finite = true;
            for (int block = 0; block < 20000; block++) // 200 seconds, +100 ppm capture clock.
            {
                captureFrames += pair.Item1 * .01 * 1.0001;
                int frames = (int)captureFrames; captureFrames -= frames;
                for (int f = 0; f < frames; f++) stream.Push(.125f);
                stream.BeginRender();
                for (int f = 0; f < pair.Item2 / 100; f++) finite &= float.IsFinite(stream.Read());
            }
            check(finite && stream.Underruns == 0 && stream.DroppedSamples == 0 && Math.Abs(stream.RateCorrection) <= .002,
                $"{pair.Item1}→{pair.Item2} 采样率转换及双时钟漂移合成检查无欠载与溢出");
        }
    }
}
