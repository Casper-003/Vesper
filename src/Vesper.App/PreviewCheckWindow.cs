using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
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
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task<(int MinX, int MaxX, int MinY, int MaxY, int RightPixels, bool ArrowTip)> CaptureIcon(Button button, string name)
        {
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)button.Content, 180, 180);
            var buffer = await bitmap.GetPixelsAsync();
            byte[] pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            int minX = width, maxX = -1, minY = height, maxY = -1;
            bool tip = false;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] < 128) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (x >= width * .85 && y < height * .5) tip = true;
            }
            int rightPixels = 0;
            if (maxX >= 0) for (int y = 0; y < height; y++) if (pixels[(y * width + maxX) * 4 + 3] >= 128) rightPixels++;
            // Store an enlarged native render on an opaque theme background for review.
            byte background = root.RequestedTheme == ElementTheme.Light ? (byte)245 : (byte)24;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double alpha = pixels[i + 3] / 255d;
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Clamp(pixels[i + c] + background * (1 - alpha), 0, 255);
                pixels[i + 3] = 255;
            }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync(); stream.Seek(0);
            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            Directory.CreateDirectory(directory);
            await using var output = File.Create(Path.Combine(directory, name + ".png"));
            using var input = stream.AsStreamForRead(); await input.CopyToAsync(output);
            return (minX, maxX, minY, maxY, rightPixels, tip);
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
            Check(capsule.Caption == "🪩", "空闲胶囊只显示 disco 球灯 emoji");
            string path = Environment.GetEnvironmentVariable("VESPER_PLAYBACK_CHECK_MEDIA") ?? throw new InvalidOperationException("Missing fixture");
            await session.StartAsync("one", string.Concat(Enumerable.Repeat("预览长标题测试", 12)), "合成媒体", null, path);
            await Until(() => session.Playback.Position.TotalMilliseconds > 250);
            Check(session.Error.Length == 0 && session.Playback.VideoWidth > 0, "真实本地 MV 预览播放");
            Check(capsule.Caption.StartsWith("🎵 ", StringComparison.Ordinal), "正在听取使用音乐 emoji 前缀");
            session.TogglePause(); await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            await Task.Delay(300); root.UpdateLayout();
            Check(capsule.Bar.ActualWidth > idleWidth && capsule.Bar.ActualWidth <= 420.5,
                "胶囊按内容扩宽并限制最大宽度");
            double left = capsule.Bar.TransformToVisual(root).TransformPoint(default).X;
            Check(Math.Abs(left + capsule.Bar.ActualWidth / 2 - root.ActualWidth / 2) < 1, "变宽后仍保持窗口居中");
            now = 3000; capsule.TickMarquee(); await Task.Delay(50);
            double beforeVolume = capsule.MarqueeOffset;
            capsule.Open(true); await Until(() => !capsule.IsTransitioning);
            int unloads = 0;
            capsule.Transport.Progress.Unloaded += (_, _) => unloads++;
            capsule.Transport.Progress.Focus(FocusState.Programmatic);
            object focused = FocusManager.GetFocusedElement(root.XamlRoot);
            for (int i = 0; i < 80; i++) session.SetVolume(i / 100d);
            Check(unloads == 0 && focused is not null && ReferenceEquals(focused, FocusManager.GetFocusedElement(root.XamlRoot)),
                "连续状态刷新不重挂载控件或丢失滑块焦点");
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
            await Until(() => !capsule.IsTransitioning);
            root.UpdateLayout();
            double x1 = capsule.Transport.ReturnButton.TransformToVisual(root).TransformPoint(default).X;
            double x2 = capsule.Transport.PauseButton.TransformToVisual(root).TransformPoint(default).X;
            double x3 = capsule.Transport.StopButton.TransformToVisual(root).TransformPoint(default).X;
            Check(x1 < x2 && x2 < x3, $"胶囊按钮从左至右为返回、暂停／继续、停止 ({x1:F1}, {x2:F1}, {x3:F1})");
            int sliders = 0;
            void CountSliders(DependencyObject node)
            {
                if (node is Slider) sliders++;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CountSliders(VisualTreeHelper.GetChild(node, i));
            }
            CountSliders(capsule.Transport);
            Check(sliders == 1, "简易胶囊只有进度条，没有音量入口");
            root.RequestedTheme = ElementTheme.Light;
            await Until(() => capsule.Transport.ReturnButton.Content is IconElement { Foreground: SolidColorBrush b } && b.Color == Microsoft.UI.Colors.Black);
            var arrow = await CaptureIcon(capsule.Transport.ReturnButton, "return-light");
            Check(arrow.ArrowTip && arrow.MaxX > arrow.MinX, "原生像素包含返回箭头右上箭头尖端");
            var play = await CaptureIcon(capsule.Transport.PauseButton, "play-light");
            Check(play.MaxX > play.MinX && play.RightPixels < (play.MaxY - play.MinY) / 3, "原生播放三角有完整尖端而非截平");
            var stop = await CaptureIcon(capsule.Transport.StopButton, "stop-light");
            Check(Math.Abs((stop.MinX + stop.MaxX) / 2d - 89.5) < 2 && Math.Abs((stop.MinY + stop.MaxY) / 2d - 89.5) < 2,
                "原生停止方块完整且在图标框居中");
            root.RequestedTheme = ElementTheme.Dark;
            await Until(() => capsule.Transport.ReturnButton.Content is IconElement { Foreground: SolidColorBrush b } && b.Color == Microsoft.UI.Colors.White);
            Check(capsule.Transport.StopButton.Content is PathIcon && capsule.Transport.StopButton.BorderThickness == new Thickness(0),
                "深浅主题使用黑白实心图形，无圆圈边框");
            Invoke(capsule.Transport.PauseButton); await Until(() => session.Playback.State == MediaPlaybackState.Playing);
            await Until(() => Equals(capsule.Transport.PauseButton.Tag, "pause"));
            var paused = await CaptureIcon(capsule.Transport.PauseButton, "pause-dark");
            Check(Math.Abs((paused.MinX + paused.MaxX) / 2d - 89.5) < 2, "暂停双竖条未单侧裁切且居中");
            Invoke(capsule.Transport.PauseButton); await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            Check(true, "单次按钮调用即可继续或暂停预览");
            int returns = 0; capsule.ReturnToDetails += () => returns++;
            Invoke(capsule.Transport.ReturnButton); await Until(() => !capsule.IsOpen);
            Check(returns == 1 && session.Playback.State == MediaPlaybackState.Paused, "单次返回详情调用保持暂停状态");
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
            capsule.Open(true); await Until(() => !capsule.IsTransitioning);
            Invoke(capsule.Transport.StopButton); await Until(() => session.SongId is null && !capsule.IsOpen);
            Check(session.SongId is null, "单次停止按钮调用清理预览并收起卡片");
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
