using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Vesper.App;

// Owned by the UI thread. A fresh player per source isolates late native events.
// MediaOpened means ready to play; it does not prove a frame reached the display.
internal sealed class LocalMediaPlayback(DispatcherQueue dispatcher) : IDisposable
{
    private MediaPlayer? player;
    private MediaSource? source;
    private MediaPlayerElement? surface;
    private TaskCompletionSource? preparing;
    private long revision;
    private bool disposed;

    public event Action? Ended;
    public event Action<string>? Failed;
    public TimeSpan Position => player?.PlaybackSession.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
    public MediaPlaybackState State => player?.PlaybackSession.PlaybackState ?? MediaPlaybackState.None;
    public uint VideoWidth => player?.PlaybackSession.NaturalVideoWidth ?? 0;
    public uint VideoHeight => player?.PlaybackSession.NaturalVideoHeight ?? 0;
    private double volume = .6;
    private bool muted;
    public double Volume
    {
        get => volume;
        set { VerifyAccess(); volume = Math.Clamp(value, 0, 1); if (player is not null) player.Volume = volume; }
    }
    public bool IsMuted
    {
        get => muted;
        set { VerifyAccess(); muted = value; if (player is not null) player.IsMuted = value; }
    }
    public void Seek(TimeSpan position)
    {
        VerifyAccess();
        var current = ReadyPlayer();
        if (!current.PlaybackSession.CanSeek) return;
        current.PlaybackSession.Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
    }

    public void Attach(MediaPlayerElement? target)
    {
        VerifyAccess();
        if (ReferenceEquals(surface, target)) return;
        surface?.SetMediaPlayer(null);
        surface = target;
        if (surface is not null)
        {
            surface.AreTransportControlsEnabled = false;
            surface.SetMediaPlayer(player);
        }
    }

    public async Task PrepareAsync(string path, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        Stop();
        long request = revision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // StorageFile opens a local file without converting # or % into URI syntax.
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (request != revision) throw new OperationCanceledException("Playback replaced.");
            var current = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false, Volume = volume, IsMuted = muted };
            player = current;
            current.CommandManager.IsEnabled = false;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            preparing = completion;
            current.MediaOpened += (_, _) => completion.TrySetResult();
            current.MediaFailed += (_, args) =>
            {
                var error = new InvalidOperationException($"无法播放本地媒体：{args.Error} ({args.ExtendedErrorCode?.HResult:X8})");
                completion.TrySetException(error);
                dispatcher.TryEnqueue(() =>
                {
                    if (request != revision || disposed) return;
                    Stop();
                    Failed?.Invoke(error.Message);
                });
            };
            current.MediaEnded += (_, _) => dispatcher.TryEnqueue(() =>
            {
                if (request == revision && !disposed) Ended?.Invoke();
            });
            source = MediaSource.CreateFromStorageFile(file);
            surface?.SetMediaPlayer(current);
            current.Source = source;
            await completion.Task.WaitAsync(timeout.Token);
            if (request != revision) throw new OperationCanceledException("Playback replaced.");
            preparing = null;
        }
        catch
        {
            if (request == revision) Stop();
            throw;
        }
    }

    public void Play() { VerifyAccess(); ReadyPlayer().Play(); }
    public void Pause() { VerifyAccess(); ReadyPlayer().Pause(); }
    private MediaPlayer ReadyPlayer() => player is not null && preparing is null
        ? player : throw new InvalidOperationException("媒体尚未准备完成。");

    public void Stop()
    {
        VerifyAccess();
        revision++;
        preparing?.TrySetCanceled();
        preparing = null;
        surface?.SetMediaPlayer(null);
        if (player is { } previous)
        {
            player = null;
            previous.Pause();
            previous.Source = null;
            previous.Dispose();
        }
        source?.Dispose();
        source = null;
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Playback requires the UI thread.");
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop();
        surface = null;
        disposed = true;
    }
}
