using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Vesper.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;

namespace Vesper.App;

// One window-owned session; browsing never replaces or prepares its player.
internal sealed class SingingSession : IDisposable
{
    private readonly PlaybackQueue queue;
    private readonly Func<IEnumerable<Song>> library;
    private readonly Func<Task> present;
    private CancellationTokenSource? preparation;
    private long revision;
    private bool disposed;
    public LocalMediaPlayback Playback { get; }
    public bool Loading { get; private set; }
    public bool Ready { get; private set; }
    public string Message { get; private set; } = "";
    public BitmapImage? FirstFrame { get; private set; }
    public event Action? Changed;

    public SingingSession(DispatcherQueue dispatcher, PlaybackQueue queue,
        Func<IEnumerable<Song>> library, Func<Task> present)
    {
        this.queue = queue; this.library = library; this.present = present;
        Playback = new(dispatcher);
        Playback.Ended += OnEnded;
        Playback.Failed += OnFailed;
    }

    public async Task NextAsync()
    {
        if (disposed) return;
        long request = ++revision;
        preparation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        preparation = cancellation;
        Playback.Stop(); Ready = false; FirstFrame = null; Message = "";
        Song? song = queue.StartNext(library(), out int skipped);
        Loading = song is not null;
        if (skipped > 0) Message = song is null ? "没有可播放的待播歌曲" : $"已跳过 {skipped} 首不可用歌曲";
        Changed?.Invoke();
        if (song is null) { preparation = null; return; }
        try
        {
            string? path = song.Sources.FirstOrDefault(path => IsVideoFile(path) && File.Exists(path)) ?? song.AvailablePath;
            if (path is null) throw new FileNotFoundException();
            await Playback.PrepareAsync(path, cancellation.Token);
            if (request != revision || disposed) return;
            if (Playback.VideoWidth == 0)
                throw new NotSupportedException("这首歌没有可用的本地视频；歌词演唱尚未开放，歌曲已保留在待播队首。");

            // Decode an actual frame into a XAML image before expanding. MediaOpened
            // alone is not evidence that the native video surface has painted.
            var decodedFrame = await ReadFirstFrameAsync(path, cancellation.Token);
            if (request != revision || disposed) return;
            FirstFrame = decodedFrame;
            Ready = true; Changed?.Invoke();
            await present();
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != revision || disposed) return;
            Playback.Play(); queue.MarkPlaying(); Loading = false;
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            if (request != revision || disposed) return;
            Playback.Stop(); Ready = Loading = false; FirstFrame = null;
            queue.ExitSession();
            Message = error is NotSupportedException ? error.Message
                : error is OperationCanceledException ? "播放准备超时，请重试。歌曲仍在待播队首。"
                : "无法准备本地视频，请检查文件、编码或系统解码支持。歌曲仍在待播队首。";
            Changed?.Invoke();
        }
        finally { if (ReferenceEquals(preparation, cancellation)) preparation = null; }
    }

    internal static async Task<BitmapImage> ReadFirstFrameAsync(string path, CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(token);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        try
        {
            using var frame = await composition.GetThumbnailAsync(TimeSpan.Zero, 1280, 0,
                VideoFramePrecision.NearestFrame).AsTask(token);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(frame).AsTask(token);
            token.ThrowIfCancellationRequested();
            return bitmap;
        }
        finally { composition.Clips.Clear(); }
    }

    private static bool IsVideoFile(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".webm" or ".ts" or ".mpeg" or ".mpg";

    public void TogglePause()
    {
        if (!Ready || Loading || disposed) return;
        if (queue.State == PlaybackState.Paused) { Playback.Play(); queue.MarkPlaying(); }
        else { Playback.Pause(); queue.MarkPaused(); }
        Changed?.Invoke();
    }

    public void Seek(TimeSpan value)
    {
        if (Ready && !Loading && !disposed) Playback.Seek(value);
    }

    private async void OnEnded()
    {
        if (Ready && !Loading && !disposed) await NextAsync();
    }

    private void OnFailed(string message)
    {
        // PrepareAsync handles its own failure. Runtime failure retains the current
        // entry at the head instead of pretending it naturally finished.
        if (Loading || disposed) return;
        revision++; Ready = false; FirstFrame = null;
        queue.ExitSession(); Message = "播放中断，歌曲已保留在待播队首。";
        Changed?.Invoke();
    }

    public void Exit()
    {
        revision++; preparation?.Cancel(); preparation = null;
        Playback.Stop(); Ready = Loading = false; FirstFrame = null; Message = "";
        queue.ExitSession(); Changed?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; revision++; preparation?.Cancel(); preparation = null;
        Playback.Ended -= OnEnded; Playback.Failed -= OnFailed;
        Playback.Dispose(); Changed = null;
    }
}
