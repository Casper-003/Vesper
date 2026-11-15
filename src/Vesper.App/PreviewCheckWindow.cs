using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace Vesper.App;

internal sealed class PreviewCheckWindow : Window
{
    private readonly Grid root = new();
    private readonly MediaPlayerElement details = new();
    private readonly PreviewTransport transport = new();
    private readonly PreviewSession session;
    private bool started;

    public PreviewCheckWindow(string reportPath)
    {
        Content = root; Title = "Vesper · 预览隔离检查";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 640));
        root.RequestedTheme = ElementTheme.Dark;
        root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 36));
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(details, 1); root.Children.Add(details);
        Grid.SetRow(transport, 1); root.Children.Add(transport);
        session = new(DispatcherQueue);
        transport.Bind(session);
        session.Playback.Attach(details);
        root.Loaded += async (_, _) => { if (!started) { started = true; await Run(reportPath); } };
        Closed += (_, _) => { transport.Bind(null); session.Dispose(); };
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
        try
        {
            Check(!session.BackgroundRetentionEnabled, "默认仅前台详情预览，后台留存入口预留且默认关闭");
            string path = Environment.GetEnvironmentVariable("VESPER_PLAYBACK_CHECK_MEDIA") ?? throw new InvalidOperationException("Missing fixture");
            await session.StartAsync("one", "详情预览测试", "合成媒体", null, path);
            await Until(() => session.Playback.Position.TotalMilliseconds > 250);
            Check(session.Error.Length == 0 && session.Playback.VideoWidth > 0, "真实本地 MV 前台预览播放");
            Invoke(transport.PauseButton);
            await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            Check(session.Playback.State == MediaPlaybackState.Paused, "详情控制条暂停预览");
            Invoke(transport.PauseButton);
            await Until(() => session.Playback.State == MediaPlaybackState.Playing);
            Check(session.Playback.State == MediaPlaybackState.Playing, "详情控制条继续预览");
            session.Playback.Seek(TimeSpan.FromSeconds(1));
            Check(session.Playback.Position.TotalSeconds > .8, "进度控制可定位真实媒体");
            session.SetVolume(.25); session.SetMuted(true);
            Check(session.Playback.Volume == .25 && session.Playback.IsMuted, "预览音量与静音独立控制");
            await session.StartAsync("two", "另一首", "合成媒体", null, path);
            Check(session.SongId == "two" && !session.Loading && session.Playback.Position.TotalSeconds < 1, "新预览替换旧预览");
            session.Playback.Seek(session.Playback.Duration - TimeSpan.FromMilliseconds(250));
            await Until(() => session.SongId is null);
            Check(session.Playback.State == MediaPlaybackState.None && session.Playback.Position == TimeSpan.Zero,
                "自然结束清理预览并回到开头播放入口");
            await session.StartAsync("two", "另一首", "合成媒体", null, path);
            session.SetSinging(true);
            Check(session.SongId is null && session.Playback.State == MediaPlaybackState.None, "开始演唱结束旧预览");
            await session.StartAsync("three", "演唱中预览", "合成媒体", null, path);
            session.SetMuted(false);
            Check(session.Playback.IsMuted, "演唱中预览无法解除静音");
            session.Close();
            Check(session.SongId is null && session.Playback.State == MediaPlaybackState.None, "离开详情页关闭前台预览会话");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Checks = checks, Error = error,
                Limits = "合成视频和程序调用；不代替真实详情页操作、玻璃观感及扬声器听感验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
