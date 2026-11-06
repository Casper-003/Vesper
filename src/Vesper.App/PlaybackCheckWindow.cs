using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Vesper.App;

// Explicit opt-in harness: never opens the personal library or shell preferences.
internal sealed class PlaybackCheckWindow : Window
{
    private readonly string reportPath;
    private readonly MediaPlayerElement video = new();
    private readonly LocalMediaPlayback playback;
    private bool started;
    private bool closed;

    public PlaybackCheckWindow(string reportPath)
    {
        this.reportPath = reportPath;
        Title = "Vesper · 本地播放隔离检查";
        Content = video;
        playback = new(DispatcherQueue);
        playback.Attach(video);
        Closed += (_, _) => { closed = true; playback.Dispose(); };
        video.Loaded += async (_, _) => { if (!started) { started = true; await RunAsync(); } };
    }

    private async Task RunAsync()
    {
        var checks = new List<object>();
        string? error = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        async Task Until(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (closed) throw new OperationCanceledException();
                if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException();
                await Task.Delay(30);
            }
        }
        try
        {
            string path = Environment.GetEnvironmentVariable("VESPER_PLAYBACK_CHECK_MEDIA")
                ?? throw new InvalidOperationException("Set VESPER_PLAYBACK_CHECK_MEDIA to a local test clip.");
            // Frame-server mode proves the native decoder produced a frame, separately
            // from the normal MediaPlayerElement presentation path exercised below.
            using (var decoder = new MediaPlayer { AutoPlay = false, IsVideoFrameServerEnabled = true })
            using (var sample = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path))))
            {
                decoder.CommandManager.IsEnabled = false;
                decoder.IsMuted = true;
                var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                decoder.VideoFrameAvailable += (_, _) => firstFrame.TrySetResult();
                decoder.MediaFailed += (_, args) => firstFrame.TrySetException(new InvalidOperationException(args.Error.ToString()));
                decoder.Source = sample;
                decoder.Play();
                await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
                decoder.Pause();
                decoder.Source = null;
                Check(firstFrame.Task.IsCompletedSuccessfully, "原生解码器实际产生首帧（独立帧服务器）");
            }
            await playback.PrepareAsync(path);
            Check(playback.Duration > TimeSpan.Zero && playback.VideoWidth > 0 && playback.VideoHeight > 0,
                "真实视频打开，获得时长和视频尺寸");
            Check(playback.State != MediaPlaybackState.Playing, "准备完成不会自动播放");
            playback.Play();
            await Until(() => playback.Position > TimeSpan.FromMilliseconds(350));
            Check(playback.State == MediaPlaybackState.Playing, "播放时真实媒体时钟推进");
            playback.Pause();
            await Until(() => playback.State == MediaPlaybackState.Paused);
            var pausedAt = playback.Position;
            await Task.Delay(300);
            Check((playback.Position - pausedAt).Duration() < TimeSpan.FromMilliseconds(80), "暂停保持进度");
            playback.Attach(null);
            playback.Attach(video);
            Check(playback.State == MediaPlaybackState.Paused && playback.Position >= pausedAt,
                "切换视频表面不恢复暂停或重置进度");
            bool ended = false;
            playback.Ended += () => ended = true;
            playback.Play();
            await Until(() => ended);
            Check(ended, "真实媒体自然结束事件");
            playback.Stop();
            Check(playback.State == MediaPlaybackState.None, "停止释放播放器");
            bool rejected = false;
            try { await playback.PrepareAsync(path + ".missing"); }
            catch (Exception) { rejected = true; }
            Check(rejected && playback.State == MediaPlaybackState.None, "缺失文件失败且清理资源");
            using (var canceled = new CancellationTokenSource())
            {
                rejected = false;
                canceled.Cancel();
                try { await playback.PrepareAsync(path, canceled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected && playback.State == MediaPlaybackState.None, "取消准备后没有残留播放器");
            }
            string invalidPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "invalid-media.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(invalidPath)!);
            await File.WriteAllTextAsync(invalidPath, "This is not a media container.");
            rejected = false;
            try { await playback.PrepareAsync(invalidPath); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && playback.State == MediaPlaybackState.None, "损坏媒体通过原生失败事件报告并清理");
            Task replaced = playback.PrepareAsync(path);
            Task latest = playback.PrepareAsync(path);
            rejected = false;
            try { await replaced; }
            catch (OperationCanceledException) { rejected = true; }
            await latest;
            Check(rejected && playback.Duration > TimeSpan.Zero, "后发准备替换前发请求，旧请求不能覆盖新播放器");
            await playback.PrepareAsync(path);
            Check(playback.Duration > TimeSpan.Zero, "失败和取消后可以重新准备");
            playback.Stop();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(stream.Length > 0, "停止后媒体文件可独占打开");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (!closed) playback.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Checks = checks, Error = error,
                Limits = "验证真实媒体时钟和原生视频表面接线；首帧实际呈现和扬声器听感需人工验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!closed) Close();
        }
    }
}
