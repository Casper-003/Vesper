using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Vesper.App;

// Explicit isolated check: synthesized video, no personal library or preferences.
internal sealed class SingingCheckWindow : Window
{
    private readonly SingingView view = new();
    private readonly PlaybackQueue queue = new();
    private readonly List<Song> songs = [];
    private readonly SingingSession session;
    private bool started, closed, allowSeek = true, interruptPresentation;
    private int presentations, seekRequests;

    internal SingingCheckWindow(string reportPath)
    {
        Content = view; Title = "Vesper · 演唱流程隔离检查";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));
        session = new(DispatcherQueue, queue, () => songs, async () =>
        {
            presentations++;
            if (view.Visibility != Visibility.Visible) return;
            var origin = new Windows.Foundation.Rect(300, 54, 420, 150);
            var size = new Windows.Foundation.Size(view.ActualWidth, view.ActualHeight);
            view.PrepareExpansion(origin, size);
            await view.PresentFrameAsync(session!.FirstFrame);
            Task expansion = view.ExpandAsync(origin, size);
            if (interruptPresentation)
            {
                interruptPresentation = false;
                view.Visibility = Visibility.Collapsed;
                view.ResetTransition();
            }
            await expansion;
        });
        session.Playback.Attach(view.Video);
        session.Changed += Refresh;
        queue.PropertyChanged += (_, _) => Refresh();
        view.PauseRequested += session.TogglePause;
        view.NextRequested += async () => await session.NextAsync();
        view.StartRequested += async () => await session.NextAsync();
        view.SeekRequested += value => { seekRequests++; session.Seek(value); };
        view.Loaded += async (_, _) => { if (!started) { started = true; view.SetActive(true); await RunAsync(reportPath); } };
        Closed += (_, _) => { closed = true; session.Dispose(); view.Dispose(); };
    }

    private void Refresh()
    {
        view.UpdateState(queue.Current is not null, session.Loading, session.Ready,
            queue.State == PlaybackState.Paused, queue.Pending.Count, session.Message, allowSeek);
        view.UpdateTime(session.Playback.Position, session.Playback.Duration);
    }

    private async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (closed) throw new OperationCanceledException();
            if (watch.Elapsed > TimeSpan.FromSeconds(18)) throw new TimeoutException(
                $"Current={queue.Current?.SongId}, Pending={queue.Pending.Count}, Ready={session.Ready}, Loading={session.Loading}, State={session.Playback.State}, Position={session.Playback.Position}, Presentations={presentations}, Message={session.Message}");
            await Task.Delay(30);
        }
    }
    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private async Task RunAsync(string reportPath)
    {
        var checks = new List<object>(); string? error = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            Directory.CreateDirectory(directory);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
            StorageFile file = await folder.CreateFileAsync("singing-generated.mp4", CreationCollisionOption.ReplaceExisting);
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(Colors.CornflowerBlue, TimeSpan.FromSeconds(30)));
            var rendered = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise,
                MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga));
            composition.Clips.Clear();
            Check(rendered == TranscodeFailureReason.None, "生成无个人媒体的本地测试视频");
            songs.Add(new("singing-one", "第一首", [file.Path]));
            songs.Add(new("singing-two", "第二首", [file.Path]));
            queue.Add(songs[0]); queue.Add(songs[1]);
            interruptPresentation = true;
            await session.NextAsync();
            Check(session.Ready && !session.Loading && queue.State == PlaybackState.Playing
                && view.Visibility == Visibility.Collapsed,
                "首帧展开中返回浏览仍正常开播，不因动画停止超时而退回队首");
            view.Visibility = Visibility.Visible;
            await Until(() => session.Playback.Position > TimeSpan.FromMilliseconds(200));
            Check(session.Ready && presentations == 1 && session.FirstFrame is { PixelWidth: > 0, PixelHeight: > 0 },
                "实际解码首帧呈现后开始本地视频，队首成为当前歌曲");
            Check(queue.Current?.SongId == "singing-one" && queue.Pending.Count == 1 && queue.State == PlaybackState.Playing,
                "开始演唱只消耗队首，待播顺序保留");
            var animationOrigin = new Windows.Foundation.Rect(300, 54, 420, 150);
            var animationSize = new Windows.Foundation.Size(view.ActualWidth, view.ActualHeight);
            Task interruptedExpansion = view.ExpandAsync(animationOrigin, animationSize);
            view.ResetTransition();
            await interruptedExpansion.WaitAsync(TimeSpan.FromSeconds(1));
            Check(session.Ready && queue.Current?.SongId == "singing-one",
                "展开中返回选歌立即完成呈现等待，不误报超时或退回队首");
            Task replacedExpansion = view.ExpandAsync(animationOrigin, animationSize);
            Task latestExpansion = view.ExpandAsync(animationOrigin, animationSize);
            await replacedExpansion.WaitAsync(TimeSpan.FromSeconds(1));
            await latestExpansion.WaitAsync(TimeSpan.FromSeconds(1));
            Check(session.Ready && queue.State == PlaybackState.Playing,
                "连续展开时旧动画完成不打断新动画或当前播放");
            Check(JsonSerializer.Deserialize<ShellPreferences>("{\"PaneOpen\":true}")!.SingingSeekEnabled,
                "旧版偏好缺少进度设置时默认允许拖动");
            Invoke(view.Pause);
            await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            var position = session.Playback.Position;
            view.Visibility = Visibility.Collapsed; await Task.Delay(250); view.Visibility = Visibility.Visible;
            Check(queue.State == PlaybackState.Paused && (session.Playback.Position - position).Duration() < TimeSpan.FromMilliseconds(100),
                "暂停后隐藏及恢复画面不恢复播放或重置进度");
            view.SetActive(true); Refresh();
            Check(view.ControlsVisible, "暂停时操作栏保持显示");
            view.Progress.Value = 2;
            await Until(() => session.Playback.Position >= TimeSpan.FromSeconds(1.8));
            Check(queue.State == PlaybackState.Paused && seekRequests > 0, "可拖动模式定位且保留暂停状态");
            allowSeek = false; Refresh();
            int beforeSeek = seekRequests;
            view.Progress.Value = 3;
            Check(!view.Progress.IsEnabled && view.Progress.Visibility == Visibility.Collapsed && seekRequests == beforeSeek,
                "仅显示模式使用只读进度，拒绝定位操作");
            allowSeek = true; Refresh();
            Invoke(view.Pause);
            await Until(() => session.Playback.State == MediaPlaybackState.Playing);
            Check(!view.ControlsVisible, "继续演唱立即收起控制栏");
            position = session.Playback.Position;
            view.Visibility = Visibility.Collapsed;
            await Until(() => session.Playback.Position > position + TimeSpan.FromMilliseconds(250));
            view.Visibility = Visibility.Visible; view.SetActive(true);
            Check(presentations == 1 && queue.Current?.SongId == "singing-one", "返回浏览时同一播放器继续推进，不重新准备");
            await Until(() => !view.ControlsVisible);
            Check(!view.ControlsVisible, "播放中闲置三秒隐藏操作栏");
            view.Interact(); Check(view.ControlsVisible, "再次操作呼出控制栏");
            session.TogglePause();
            await Until(() => session.Playback.State == MediaPlaybackState.Paused);
            Check(queue.Current?.SongId == "singing-one", "手动切歌检查前锁定当前歌曲，隔离自然结束");
            Invoke(view.Next);
            await Until(() => queue.Current?.SongId == "singing-two" && session.Ready && !session.Loading);
            Check(queue.Pending.Count == 0 && presentations == 2, "切歌按钮进入待播下一首");
            queue.Add(songs[0]); view.Visibility = Visibility.Collapsed;
            session.Seek(session.Playback.Duration - TimeSpan.FromMilliseconds(250));
            await Until(() => queue.Current?.SongId == "singing-one" && session.Ready && !session.Loading);
            Check(view.Visibility == Visibility.Collapsed && presentations == 3, "浏览期间自然结束自动下一首，不主动显示演唱画面");
            view.Visibility = Visibility.Visible;
            session.Seek(session.Playback.Duration - TimeSpan.FromMilliseconds(250));
            await Until(() => queue.Current is null && !session.Ready);
            Check(queue.State == PlaybackState.Idle && queue.Pending.Count == 0, "自然结束且无待播进入等待");
            queue.Add(songs[0]); await Task.Delay(100);
            Check(queue.Current is null && !session.Ready, "等待时新增歌曲不自动开始");
            await session.NextAsync();
            await session.NextAsync();
            Check(queue.Current is null && queue.Pending.Count == 0 && !session.Ready, "无待播时切歌结束当前歌曲并等待");
            queue.Add(songs[0]); queue.Add(songs[1]);
            await session.NextAsync(); session.Exit();
            Check(queue.Current is null && queue.Pending.Select(item => item.SongId).SequenceEqual(new[] { "singing-one", "singing-two" })
                && session.Playback.State == MediaPlaybackState.None, "退出本场释放播放，当前歌曲回队首且保留待播顺序");
            await session.NextAsync();
            Check(session.Playback.Position < TimeSpan.FromSeconds(1), "退出后再次开始从头播放");
            session.Exit();
            Task preparing = session.NextAsync(); session.Exit(); await preparing;
            Check(queue.Current is null && !session.Ready && !session.Loading && queue.Pending.Count == 2,
                "准备期间退出，晚到结果不能复活播放或消耗队列");
            queue.Pending.Clear();
            queue.Add(new("missing", "已移除文件", [file.Path + ".missing"]));
            await session.NextAsync();
            Check(queue.Current is null && queue.Pending.Count == 0 && session.Message.Contains("没有可播放"), "不可用队首跳过，全部不可用时清楚提示");
            string invalidPath = Path.Combine(directory, "invalid-singing.mp4");
            await File.WriteAllTextAsync(invalidPath, "not media");
            songs.Add(new("singing-invalid", "损坏视频", [invalidPath])); queue.Add(songs[^1]);
            await session.NextAsync();
            Check(!session.Ready && queue.Current is null && queue.Pending.Count == 1 && session.Message.Length > 0,
                "损坏媒体失败后保留队首并提供错误提示");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            session.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Checks = checks, Error = error,
                Limits = "隔离验证会话、原生媒体及控件事件；整窗全屏往返、真实文件听感、磨砂玻璃和展开观感须用户人工验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!closed) Close();
        }
    }
}
