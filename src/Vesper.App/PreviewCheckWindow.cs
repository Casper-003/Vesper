using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Vesper.App;

internal sealed class PreviewCheckWindow : Window
{
    private readonly Grid root = new();
    private readonly MediaPlayerElement details = new();
    private readonly PreviewSession session;
    private readonly PlaybackCapsule capsule;
    private bool started;
    private long now;
    public PreviewCheckWindow(string reportPath)
    {
        Content = root; Title = "Vesper · 预览隔离检查";
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(details, 1); root.Children.Add(details);
        session = new(DispatcherQueue);
        PlaybackCapsule? current = null;
        capsule = new(root, session, WinRT.Interop.WindowNative.GetWindowHandle(this), () => session.Playback.Attach(current?.ShowingPreview == true ? current.Video : details), () => now);
        current = capsule;
        root.Loaded += async (_, _) => { if (!started) { started = true; await Run(reportPath); } };
        Closed += (_, _) => { capsule.Dispose(); session.Dispose(); };
    }
    private async Task Run(string reportPath)
    {
        var checks = new List<object>(); string? error = null;
        void Check(bool pass, string name) { checks.Add(new { Name = name, Passed = pass }); if (!pass) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> condition)
        {
            var start = Environment.TickCount64;
            while (!condition()) { if (Environment.TickCount64 - start > 15000) throw new TimeoutException(); await Task.Delay(30); }
        }
        try
        {
            var gesture = new CapsuleGesture();
            gesture.Press(0, 0, 0); Check(gesture.Release(80, true), "短按松开触发点击");
            gesture.Press(0, 0, 0); Check(!gesture.Release(600, true), "长按松开不展开");
            gesture.Press(0, 0, 0); Check(gesture.Move(12, 0) && !gesture.Release(80, true), "拖动后松开不误触点击");
            gesture.Press(0, 0, 0); gesture.Cancel(); Check(!gesture.Release(80, true), "失去捕获后不展开");
            gesture.Press(0, 0, 0); Check(!gesture.Release(80, false), "外部松开不展开");
            capsule.Open(false); Check(capsule.IsOpen && !capsule.ShowingPreview, "空闲胶囊先展开演唱卡，不开始播放");
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                Check(capsule.CardBounds.Width <= capsule.Bar.ActualWidth + 2 && capsule.CardBounds.Height <= 38,
                    "展开起点使用胶囊宽高，不是整张卡直接淡入");
            await Until(() => !capsule.IsTransitioning);
            Check(Math.Abs(capsule.CardBounds.Width - 420) < 1 && capsule.CardBounds.Height >= 150,
                "展开完成恢复控制卡真实尺寸");
            capsule.Close(); capsule.Open(false);
            await Until(() => !capsule.IsTransitioning);
            Check(capsule.IsOpen, "快速收起再展开不被旧动画回调关闭");
            capsule.Close();
            await Until(() => !capsule.IsOpen);
            double idleWidth = capsule.Bar.ActualWidth;
            string path = Environment.GetEnvironmentVariable("VESPER_PLAYBACK_CHECK_MEDIA") ?? throw new InvalidOperationException("Missing fixture");
            await session.StartAsync("one", string.Concat(Enumerable.Repeat("预览长标题测试", 12)), "合成媒体", null, path);
            await Until(() => session.Playback.Position.TotalMilliseconds > 250);
            Check(session.Error.Length == 0 && session.Playback.VideoWidth > 0, "真实本地 MV 预览播放");
            session.TogglePause(); await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            await Task.Delay(300); root.UpdateLayout();
            Check(capsule.Bar.ActualWidth > idleWidth && capsule.Bar.ActualWidth <= 420.5,
                "胶囊按内容扩宽并限制最大宽度");
            double left = capsule.Bar.TransformToVisual(root).TransformPoint(default).X;
            Check(Math.Abs(left + capsule.Bar.ActualWidth / 2 - root.ActualWidth / 2) < 1, "变宽后仍保持窗口居中");
            now = 3000; capsule.TickMarquee(); await Task.Delay(50);
            double beforeVolume = capsule.MarqueeOffset;
            for (int i = 0; i < 80; i++) session.SetVolume(i / 100d);
            capsule.TickMarquee(); await Task.Delay(50);
            Check(beforeVolume > 0 && Math.Abs(capsule.MarqueeOffset - beforeVolume) < 1,
                "同一时刻连续调音量不会推进或重置滚动");
            now += 1000; capsule.TickMarquee(); await Task.Delay(50);
            Check(Math.Abs(capsule.MarqueeOffset - beforeVolume - 25) < 1, "标题每经过一秒固定推进 25 单位");
            TimeSpan at = session.Playback.Position;
            capsule.Open(true); await Task.Delay(250); capsule.Close();
            await Until(() => !capsule.IsOpen);
            Check((session.Playback.Position - at).Duration().TotalMilliseconds < 80 && session.Playback.State == MediaPlaybackState.Paused,
                "详情与胶囊表面往返保留暂停和进度");
            capsule.Open(true); capsule.SwitchCard(120);
            Check(!capsule.ShowingPreview && session.Playback.State == MediaPlaybackState.Paused, "滚轮切到后卡不影响播放");
            await Task.Delay(250); capsule.SwitchCard(-120); Check(capsule.ShowingPreview, "反向滚轮循环切回预览卡");
            session.Playback.Seek(TimeSpan.FromSeconds(1)); Check(session.Playback.Position.TotalSeconds > .8, "进度控制可定位真实媒体");
            session.SetVolume(.25); session.SetMuted(true);
            Check(session.Playback.Volume == .25 && session.Playback.IsMuted, "预览音量与静音独立控制");
            await session.StartAsync("two", "另一首", "合成媒体", null, path);
            Check(session.SongId == "two" && !session.Loading && session.Playback.Position.TotalSeconds < 1, "新预览替换旧预览");
            session.Playback.Seek(session.Playback.Duration - TimeSpan.FromMilliseconds(250));
            await Until(() => session.SongId is null && !capsule.IsOpen);
            Check(session.Playback.State == MediaPlaybackState.None && session.Playback.Position == TimeSpan.Zero,
                "自然结束清理预览并回到开头播放入口");
            await session.StartAsync("two", "另一首", "合成媒体", null, path);
            session.SetSinging(true); Check(session.SongId is null && session.Playback.State == MediaPlaybackState.None, "开始演唱结束旧预览");
            await session.StartAsync("three", "演唱中预览", "合成媒体", null, path); session.SetMuted(false);
            Check(session.Playback.IsMuted, "演唱中预览无法解除静音");
            session.Close(); await Until(() => !capsule.IsOpen);
            Check(session.SongId is null, "关闭预览停止并收起预览卡");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Passed = error is null, Checks = checks, Error = error,
                Limits = "合成视频和程序调用；不代替真实鼠标拖窗、滚轮路由、玻璃像素及扬声器听感验收。" }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
