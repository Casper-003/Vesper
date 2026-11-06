using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<ListView, EntityDetailsView> entityOverviews = [];
    private readonly Dictionary<EntityDetailsView, (string Id, bool Album)> entityIdentities = [];
    private readonly DispatcherTimer entityRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private void InitializeEntityDetailsPresentation() => entityRefreshTimer.Tick += (_, _) =>
    {
        string? key = (ContentFrame.Content as ShellPage)?.PageKey;
        if (closed || key is null || pageContent.GetValueOrDefault(key) is not EntityDetailsView view)
        { entityRefreshTimer.Stop(); return; }
        if (overviewScrolling || view.IsInteracting) return;
        entityRefreshTimer.Stop();
        RefreshCurrentEntityDetails();
    };
    private void RequestEntityRefresh(bool artistAssetsChanged = false)
    {
        string? key = (ContentFrame.Content as ShellPage)?.PageKey;
        if (key == "首页")
        {
            if (artistAssetsChanged) homeArtistImageRevision++;
            ScheduleHomeArtwork();
        }
        if (!closed && key is not null && pageContent.GetValueOrDefault(key) is EntityDetailsView
            && !entityRefreshTimer.IsEnabled) entityRefreshTimer.Start();
    }
    internal DataTemplate EntitySongTemplate => (DataTemplate)Root.Resources["SongRowTemplate"];

    internal void OpenArtist(string id) => OpenCatalogEntity(id, false);
    internal void OpenAlbum(string id) => OpenCatalogEntity(id, true);
    private void OpenCatalogEntity(string id, bool isAlbum)
    {
        if (closed) return;
        var catalog = viewModel.GetCatalogSnapshot();
        bool exists = isAlbum ? catalog.Albums.Any(album => album.Id == id) : catalog.Artists.Any(artist => artist.Id == id);
        if (!exists) { viewModel.ReportProblem("资料已更新，请重新搜索。"); return; }
        string key = (isAlbum ? "专辑:" : "艺人:") + id;
        if ((ContentFrame.Content as ShellPage)?.PageKey == key) return;
        NavigateShell(new ShellNavigationRequest(key, this, EntityId: id, IsAlbum: isAlbum));
    }

    internal void OpenEntitySong(string id)
    {
        if (viewModel.FindSong(id) is null) return;
        NavigateShell(new ShellNavigationRequest("歌曲详情:" + id, this, id));
    }

    private EntityDetailsView CreateEntityDetails(string key, string id, bool album)
    {
        var view = new EntityDetailsView(this, album);
        entityIdentities[view] = (id, album);
        entityOverviews[view.SongList] = view;
        return view;
    }

    internal void RegisterEntityOverview(EntityDetailsView view)
    {
        if (!entityOverviews.ContainsKey(view.SongList)) entityOverviews[view.SongList] = view;
        view.SongList.ContainerContentChanging -= OnOverviewContainerChanging;
        view.SongList.ContainerContentChanging += OnOverviewContainerChanging;
        ConfigureOverview(view.SongList, rebind: true);
    }
    internal void ConfigureEntityOverview(EntityDetailsView view) => ConfigureOverview(view.SongList);

    private void RefreshCurrentEntityDetails()
    {
        string? key = (ContentFrame.Content as ShellPage)?.PageKey;
        if (key is null || pageContent.GetValueOrDefault(key) is not EntityDetailsView view
            || !entityIdentities.TryGetValue(view, out var identity)) return;
        RefreshEntityDetails(view, identity.Id, identity.Album);
    }

    private void RefreshEntityDetails(EntityDetailsView view, string id, bool isAlbum)
    {
        var catalog = viewModel.GetCatalogSnapshot();
        bool Visible(LibrarySongItem? song) => song is not null && (!viewModel.HideUnavailable || song.IsAvailable);
        if (isAlbum)
        {
            var album = catalog.Albums.FirstOrDefault(value => value.Id == id);
            if (album is null) { view.Apply("专辑", "", null, [], []); return; }
            var albumTracks = catalog.GetAlbumTracks(id);
            var tracks = albumTracks.Where(track => Visible(viewModel.FindSong(track.SongId)))
                .OrderBy(track => track.DiscNumber is > 0 ? track.DiscNumber.Value : int.MaxValue)
                .ThenBy(track => track.TrackNumber is > 0 ? track.TrackNumber.Value : int.MaxValue)
                .ThenBy(track => viewModel.FindSong(track.SongId)?.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(track => track.Id, StringComparer.Ordinal).ToArray();
            var rows = tracks.Select(track => new EntitySongRow(viewModel.FindSong(track.SongId)!, "",
                discNumber: track.DiscNumber, trackNumber: track.TrackNumber)).ToArray();
            object? cover = GetAlbumImage(album, albumTracks);
            view.Apply(album.Name, string.Join(" · ", new[] { album.Artist, album.Year }.Where(value => value.Length > 0)), cover, rows, []);
        }
        else
        {
            var artist = catalog.Artists.FirstOrDefault(value => value.Id == id);
            var rows = catalog.GetArtistSongIds(id).Select(viewModel.FindSong).Where(Visible).OfType<LibrarySongItem>()
                .OrderBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(song => song.Song.Id, StringComparer.Ordinal)
                .Select(song => new EntitySongRow(song, "")).ToArray();
            var tracksByAlbum = catalog.Tracks.ToLookup(track => track.AlbumId, StringComparer.Ordinal);
            var albums = catalog.GetArtistAlbums(id).Where(album => tracksByAlbum[album.Id].Any(track => Visible(viewModel.FindSong(track.SongId))))
                .OrderBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(album => album.Id, StringComparer.Ordinal)
                .Select(album => new EntityAlbumCard(album.Id, album.Name, album.Artist,
                    GetAlbumImage(album, tracksByAlbum[album.Id].ToArray()))).ToArray();
            view.Apply(artist?.Name ?? "艺人", "", artist is null ? null : GetArtistImage(artist), rows, albums);
        }
        if (view.IsLoaded) ConfigureOverview(view.SongList);
    }

}
