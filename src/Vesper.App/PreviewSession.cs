using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace Vesper.App;

// Window lifetime, independent from cached/loaded detail pages and video surfaces.
internal sealed class PreviewSession : IDisposable
{
    public LocalMediaPlayback Playback { get; }
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private long revision;
    public string? SongId { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource? Cover { get; private set; }
    public bool Loading { get; private set; }
    public bool Ended { get; private set; }
    public string Error { get; private set; } = "";
    public bool Singing { get; private set; }
    public event Action? Changed;
    public PreviewSession(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        Playback = new(dispatcher);
        timer.Tick += (_, _) => Changed?.Invoke();
        // Natural completion returns the detail page to its original play entry.
        Playback.Ended += Close;
        Playback.Failed += message => { Error = message; Loading = false; Changed?.Invoke(); };
    }
    public async Task StartAsync(string id, string title, string artist, ImageSource? cover, string path)
    {
        long request = ++revision;
        SongId = id; Title = title; Artist = artist; Cover = cover;
        Loading = true; Ended = false; Error = "";
        Playback.IsMuted = Singing;
        timer.Start(); Changed?.Invoke();
        try
        {
            await Playback.PrepareAsync(path);
            if (request != revision) return;
            Loading = false; Playback.Play(); Changed?.Invoke();
        }
        catch (OperationCanceledException) { if (request == revision) { Loading = false; Error = "预览准备已取消或超时，请重试。"; Changed?.Invoke(); } }
        catch (Exception) { if (request == revision) { Loading = false; Error = "无法预览此文件，请检查文件是否可用或编码是否受支持。"; Changed?.Invoke(); } }
    }
    public void TogglePause()
    {
        if (SongId is null || Loading || Error.Length > 0) return;
        if (Ended) { Playback.Seek(TimeSpan.Zero); Ended = false; Playback.Play(); }
        else if (Playback.State == MediaPlaybackState.Playing) Playback.Pause(); else Playback.Play();
        Changed?.Invoke();
    }
    public void SetSinging(bool value)
    {
        if (value && !Singing) Close();
        Singing = value;
        if (value) Playback.IsMuted = true;
        Changed?.Invoke();
    }
    public void SetMuted(bool value) { Playback.IsMuted = Singing || value; Changed?.Invoke(); }
    public void SetVolume(double value) { if (!Singing) Playback.Volume = value; Changed?.Invoke(); }
    public void Close()
    {
        revision++; timer.Stop(); Playback.Stop(); SongId = null; Cover = null;
        Loading = false; Ended = false; Error = ""; Changed?.Invoke();
    }
    public void Dispose() { timer.Stop(); Playback.Dispose(); Changed = null; }
}
