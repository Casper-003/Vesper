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
        var fast = new MonitoringBuffer(48000, 48000, 480, immediate: true);
        for (int f = 0; f < 480; f++) fast.Push(.25f);
        fast.BeginRender();
        check(fast.AvailableOutputFrames == 480 && fast.Read() == .25f,
            "低延迟首包立即可用，无两周期蓄水等待");
        for (int f = 1; f < 480; f++) fast.Read();
        check(fast.Count <= 1 && fast.Underruns == 0,
            "低延迟完整发送单包，不因插值前瞻每包制造欠载");
        check(MonitoringRenderQueue.FramesToWrite(4800, 0, 480, 4800) == 480,
            "大容量设备输出缓冲只排队一个周期，不填满容量");
        check(MonitoringRenderQueue.FramesToWrite(4800, 480, 480, 4800) == 0
            && MonitoringRenderQueue.FramesToWrite(4800, 0, 480, 0) == 0,
            "已有一周期或没有新的人声时不额外写入音频和静音");
        check(MonitoringRenderQueue.FramesToWrite(4800, 240, 480, 100) == 100,
            "输出补充同时受真实人声量和已有排队量限制");
        for (int f = 0; f < 1440; f++) fast.Push(.5f);
        fast.BeginRender();
        check(fast.Count == 480 && fast.DroppedSamples is >= 960 and <= 961,
            "低延迟调度落后三包时立即丢弃过期两包，不累积回声");
        var smoother = new MonitoringSignalSmoother(48000);
        float first = smoother.Process(1, 1), level = first;
        for (int f = 0; f < 2400; f++) level = smoother.Process(1, 1);
        check(first < .01 && level > .99, "开启及调音量渐变不引入等待缓冲，避免瞬间跳到满幅");
        smoother.Discontinuity();
        check(smoother.Process(-1, 1) > .9, "采集中断或丢弃过期人声后从上一采样值短交叉渐变，抑制硬跳变");
        for (int f = 0; f < 100; f++) level = smoother.Process(-1, 1);
        check(level < -.99, "中断渐变两毫秒内结束，不长时间掩盖或拖延人声");
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
        foreach (var pair in new[] { (44100, 48000), (48000, 44100), (48000, 48000) })
        foreach (double clockRatio in new[] { .9999, 1.0001 })
        {
            int inputPeriod = pair.Item1 / 100, outputPeriod = pair.Item2 / 100;
            var stream = new MonitoringBuffer(pair.Item1, pair.Item2, inputPeriod, immediate: true);
            double captureFrames = 0; long outputFrames = 0; bool audioValid = true;
            for (int block = 0; block < 20000; block++)
            {
                captureFrames += inputPeriod * clockRatio;
                int frames = (int)captureFrames; captureFrames -= frames;
                for (int f = 0; f < frames; f++) stream.Push(.125f);
                stream.BeginRender();
                uint write = MonitoringRenderQueue.FramesToWrite((uint)(outputPeriod * 4), 0, (uint)outputPeriod, stream.AvailableOutputFrames);
                outputFrames += write;
                for (int f = 0; f < write; f++) audioValid &= Math.Abs(stream.Read() - .125f) < .00001f;
            }
            check(audioValid && stream.Underruns == 0 && stream.DroppedSamples == 0
                && outputFrames >= 20000L * outputPeriod - outputPeriod * 2,
                $"即时发送 {pair.Item1}→{pair.Item2}、时钟比 {clockRatio} 持续200秒（输出={outputFrames}，欠载={stream.Underruns}，丢弃={stream.DroppedSamples}，修正={stream.RateCorrection:F6}），保持音频连续");
        }
        foreach (var periods in new[] { (160, 160), (192, 160), (480, 160), (160, 192), (144, 160) })
        {
            int capturePeriod = periods.Item1, renderPeriod = periods.Item2;
            int target = capturePeriod + renderPeriod;
            var stream = new MonitoringBuffer(48000, 48000, target, immediate: true,
                maximumQueuedFrames: target + Math.Max(capturePeriod, renderPeriod) + 96);
            bool running = false; int nextRender = int.MaxValue; long missing = 0;
            for (int frame = 1; frame <= 48000 * 10; frame++)
            {
                if (frame % capturePeriod == 0)
                    for (int f = 0; f < capturePeriod; f++) stream.Push(.25f);
                if (!running && stream.Count >= target) { running = true; nextRender = frame; }
                if (frame != nextRender) continue;
                stream.BeginRender(); int available = stream.AvailableOutputFrames;
                for (int f = 0; f < renderPeriod; f++) { if (f < available) stream.Read(); else missing++; }
                nextRender += renderPeriod;
            }
            check(running && missing == 0 && stream.DroppedSamples == 0,
                $"独占输入/输出周期 {capturePeriod}/{renderPeriod} 帧相位交错运行，无周期性缺帧（缺帧={missing}，丢弃={stream.DroppedSamples}）");
        }
    }
}
