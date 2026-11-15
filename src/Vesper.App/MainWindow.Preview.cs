using Microsoft.UI.Xaml.Controls;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private PreviewSession preview = null!;
    private SongDetailsView? previewDetails;
    private MediaPlayerElement? backgroundPreviewSurface;
    private bool previewEntryRequested;
    internal PreviewSession Preview => preview;

    private void InitializePreview()
    {
        preview = new(DispatcherQueue);
        preview.Changed += RoutePreviewSurface;
        viewModel.Queue.PropertyChanged += OnPreviewQueueStateChanged;
        Closed += (_, _) => { viewModel.Queue.PropertyChanged -= OnPreviewQueueStateChanged; preview.Dispose(); };
    }
    private void OnPreviewQueueStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        preview.SetSinging(viewModel.Queue.Current is not null);

    internal async Task StartPreviewAsync(LibrarySongItem song)
    {
        string? path = song.Song.AvailablePath;
        if (path is null) return;
        previewEntryRequested = true;
        try { await monitoring.ReleaseForPlaybackAsync(); }
        finally { previewEntryRequested = false; }
        if (closed) return;
        await preview.StartAsync(song.Song.Id, song.Title, song.ArtistDetail, song.Cover as Microsoft.UI.Xaml.Media.ImageSource, path);
    }
    internal void SetPreviewDetails(SongDetailsView details, bool loaded)
    {
        if (loaded) previewDetails = details;
        else if (ReferenceEquals(previewDetails, details))
        {
            previewDetails = null;
            if (!preview.BackgroundRetentionEnabled && preview.SongId == details.PreviewSongId)
                preview.Close();
        }
        RoutePreviewSurface();
    }
    // 预留给后续后台预览入口挂载表面与跳转详情。
    internal void SetBackgroundPreviewSurface(MediaPlayerElement? surface)
    {
        backgroundPreviewSurface = surface;
        RoutePreviewSurface();
    }
    internal void ReturnToPreviewDetails()
    {
        if (preview.SongId is { } id) NavigateShell(new ShellNavigationRequest("歌曲详情:" + id, this, id));
    }
    private void RoutePreviewSurface()
    {
        if (preview is null || closed) return;
        MediaPlayerElement? surface = previewDetails is { IsLoaded: true } details && details.PreviewSongId == preview.SongId
            ? details.PreviewSurface
            : preview.BackgroundRetentionEnabled ? backgroundPreviewSurface : null;
        preview.Playback.Attach(surface);
        previewDetails?.RefreshPreview();
    }
}
