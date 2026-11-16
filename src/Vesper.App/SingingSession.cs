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
    private readonly Func<Song, CancellationToken, Task>? ensureLyrics;
    private CancellationTokenSource? preparation;
    private long revision;
    private bool disposed;
    public LocalMediaPlayback Playback { get; }
    public bool Loading { get; private set; }
    public bool Ready { get; private set; }
    public bool IsLyricsMode => Ready && Playback.VideoWidth == 0;
    public Song? CurrentSong { get; private set; }
    public string Message { get; private set; } = "";
    public BitmapImage? FirstFrame { get; private set; }
    public event Action? Changed;

    public SingingSession(DispatcherQueue dispatcher, PlaybackQueue queue,
        Func<IEnumerable<Song>> library, Func<Task> present, Func<Song, CancellationToken, Task>? ensureLyrics = null)
    {
        this.queue = queue; this.library = library; this.present = present; this.ensureLyrics = ensureLyrics;
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
        CurrentSong = song;
        Loading = song is not null;
        if (skipped > 0) Message = song is null ? "没有可播放的待播歌曲" : $"已跳过 {skipped} 首不可用歌曲";
        Changed?.Invoke();
        if (song is null) { preparation = null; return; }
        try
        {
            string? path = FindVideoPath(song) ?? song.AvailablePath;
            if (path is null) throw new FileNotFoundException();
            Task? lyricsTask = null;
            if (FindVideoPath(song) is null && ensureLyrics is not null)
                lyricsTask = EnsureLyricsSafeAsync(song, cancellation.Token);

            await Playback.PrepareAsync(path, cancellation.Token);
            if (request != revision || disposed) return;

            if (Playback.VideoWidth > 0)
            {
                // Decode an actual frame into a XAML image before expanding. MediaOpened
                // alone is not evidence that the native video surface has painted.
                var decodedFrame = await ReadFirstFrameAsync(path, cancellation.Token);
                if (request != revision || disposed) return;
                FirstFrame = decodedFrame;
            }
            else
            {
                FirstFrame = null;
                if (lyricsTask is not null && !lyricsTask.IsCompleted)
                {
                    try { await lyricsTask.WaitAsync(TimeSpan.FromMilliseconds(3500), cancellation.Token); }
                    catch (TimeoutException) { /* 本地音乐不阻塞进场，歌词就绪后会实时刷新到舞台。 */ }
                }
                if (request != revision || disposed) return;
            }

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
            Playback.Stop(); Ready = Loading = false; FirstFrame = null; CurrentSong = null;
            queue.ExitSession();
            Message = error is NotSupportedException ? error.Message
                : error is OperationCanceledException ? "播放准备超时，请重试。歌曲仍在待播队首。"
                : "无法播放当前文件，请检查文件、编码或系统解码支持。歌曲仍在待播队首。";
            Changed?.Invoke();
        }
        finally { if (ReferenceEquals(preparation, cancellation)) preparation = null; }
    }

    private async Task EnsureLyricsSafeAsync(Song song, CancellationToken token)
    {
        if (ensureLyrics is null) return;
        try { await ensureLyrics(song, token); }
        catch (OperationCanceledException) { }
        catch { /* 自动歌词补全失败不阻断本地音频演唱。 */ }
    }

    internal static async Task<BitmapImage> ReadFirstFrameAsync(string path, CancellationToken token)
    {
        var (frame, _) = await ReadFirstFrameWithColorAsync(path, token);
        return frame;
    }

    internal static async Task<(BitmapImage Frame, uint? Color)> ReadFirstFrameWithColorAsync(string path, CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(token);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        try
        {
            using var frame = await composition.GetThumbnailAsync(TimeSpan.Zero, 1280, 0,
                VideoFramePrecision.NearestFrame).AsTask(token);
            byte[] bytes;
            using (var input = frame.AsStreamForRead())
            {
                using var copy = new MemoryStream();
                await input.CopyToAsync(copy, token);
                bytes = copy.ToArray();
            }
            uint? color = null;
            try
            {
                color = await Task.Run(async () =>
                {
                    using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(token);
                    var pixels = await decoder.GetPixelDataAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                        new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 64, ScaledHeight = 64 },
                        Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                        Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask(token);
                    return CoverPalette.DominantArgb(pixels.DetachPixelData());
                }, token);
            }
            catch { /* Frame color sampling is optional. */ }
            using var imageStream = new MemoryStream(bytes).AsRandomAccessStream();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(imageStream).AsTask(token);
            token.ThrowIfCancellationRequested();
            return (bitmap, color);
        }
        finally { composition.Clips.Clear(); }
    }

    internal static string? FindVideoPath(Song song) =>
        song.Sources.FirstOrDefault(path => IsVideoFile(path) && File.Exists(path));

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
        revision++; Ready = false; FirstFrame = null; CurrentSong = null;
        queue.ExitSession(); Message = "播放中断，歌曲已保留在待播队首。";
        Changed?.Invoke();
    }

    public void Exit()
    {
        revision++; preparation?.Cancel(); preparation = null;
        Playback.Stop(); Ready = Loading = false; FirstFrame = null; CurrentSong = null; Message = "";
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
