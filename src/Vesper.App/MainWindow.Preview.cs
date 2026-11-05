using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private PreviewSession preview = null!;
    private PlaybackCapsule capsule = null!;
    private SongDetailsView? previewDetails;
    internal PreviewSession Preview => preview;

    private void InitializePreview()
    {
        preview = new(DispatcherQueue);
        capsule = new(Root, preview, WinRT.Interop.WindowNative.GetWindowHandle(this), RoutePreviewSurface);
        capsule.ReturnToDetails += () =>
        {
            if (preview.SongId is { } id) NavigateShell(new ShellNavigationRequest("歌曲详情:" + id, this, id));
        };
        preview.Changed += RoutePreviewSurface;
        viewModel.Queue.PropertyChanged += OnPreviewQueueStateChanged;
        capsule.Bar.SizeChanged += (_, _) => UpdateTitleLayout();
        Closed += (_, _) => { viewModel.Queue.PropertyChanged -= OnPreviewQueueStateChanged; capsule.Dispose(); preview.Dispose(); };
    }
    private void OnPreviewQueueStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        preview.SetSinging(viewModel.Queue.Current is not null);

    internal async Task StartPreviewAsync(LibrarySongItem song)
    {
        string? path = song.Song.AvailablePath;
        if (path is null) return;
        capsule.Close();
        await preview.StartAsync(song.Song.Id, song.Title, song.ArtistDetail, song.Cover as Microsoft.UI.Xaml.Media.ImageSource, path);
    }
    internal void SetPreviewDetails(SongDetailsView details, bool loaded)
    {
        if (loaded) previewDetails = details;
        else if (ReferenceEquals(previewDetails, details)) previewDetails = null;
        RoutePreviewSurface();
    }
    private void RoutePreviewSurface()
    {
        if (preview is null || capsule is null || closed) return;
        MediaPlayerElement? surface = capsule.ShowingPreview ? capsule.Video
            : previewDetails is { IsLoaded: true } details && details.PreviewSongId == preview.SongId ? details.PreviewSurface : null;
        preview.Playback.Attach(surface);
        previewDetails?.RefreshPreview();
    }
}
