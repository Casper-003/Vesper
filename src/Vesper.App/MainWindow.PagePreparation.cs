using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly TaskCompletionSource libraryPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? pagePreparationCancellation;
    private Task pagePreparationTask = Task.CompletedTask;
    private long pagePreparationRevision;

    private static bool IsIsolatedUiCheck() => new[] { "VESPER_ARTWORK_CHECK_PATH", "VESPER_NAVIGATION_CHECK_PATH",
        "VESPER_SEARCH_CHECK_PATH", "VESPER_QUEUE_CHECK_PATH", "VESPER_RUNTIME_CHECK_PATH", "VESPER_COLOR_CHECK_PATH" }
        .Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private bool NeedsPagePreparation(ShellNavigationRequest request) => libraryInitialized && !IsIsolatedUiCheck()
        && (request.SongId is not null || request.EntityId is not null || request.Key is "曲库" or "收藏");

    private void CancelPagePreparation()
    {
        pagePreparationRevision++;
        pagePreparationCancellation?.Cancel();
        pagePreparationCancellation?.Dispose(); pagePreparationCancellation = null;
        PageLoading.Visibility = Visibility.Collapsed;
        ContentFrame.IsHitTestVisible = true;
    }

    private void BeginPagePreparation(ShellNavigationRequest request, Func<bool> commit)
    {
        CancelPagePreparation();
        if (request.Key != "搜索")
        { searchRequests.Leave(); scheduledSearch = null; deferredSearchRefresh = false; }
        long revision = pagePreparationRevision;
        pagePreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        ContentFrame.IsHitTestVisible = false;
        pagePreparationTask = PrepareAndCommitPageAsync(request, commit, revision, pagePreparationCancellation.Token);
    }

    private async Task PrepareAndCommitPageAsync(ShellNavigationRequest request, Func<bool> commit, long revision, CancellationToken token)
    {
        bool Current() => !closed && revision == pagePreparationRevision && !token.IsCancellationRequested;
        SetPagePreparationProgress(0, 0, "正在准备页面…");
        _ = RevealPageProgressAsync(revision, token);
        try
        {
            await libraryPrepared.Task.WaitAsync(token);
            await PreparePageResourcesAsync(request, token);
            token.ThrowIfCancellationRequested();
            if (Current()) commit();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) viewModel.ReportProblem("页面准备失败，请重试：" + ex.Message);
        }
        finally
        {
            if (revision == pagePreparationRevision)
            { PageLoading.Visibility = Visibility.Collapsed; ContentFrame.IsHitTestVisible = true; }
        }
    }

    private async Task RevealPageProgressAsync(long revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            if (!closed && pagePreparationRevision == revision && !pagePreparationTask.IsCompleted)
                PageLoading.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
    }

    private void SetPagePreparationProgress(int completed, int total, string phase)
    {
        PageLoadingLabel.Text = phase;
        PageLoadingBar.IsIndeterminate = total == 0;
        PageLoadingBar.Maximum = Math.Max(1, total);
        PageLoadingBar.Value = completed;
        PageLoadingCount.Text = total > 0 ? $"{completed} / {total}" : "";
    }

    private async Task PreparePageResourcesAsync(ShellNavigationRequest request, CancellationToken token)
    {
        var songs = new List<LibrarySongItem>();
        CatalogAlbum? album = null;
        CatalogArtist? artist = null;
        IReadOnlyList<CatalogTrack> tracks = [];
        ListView? retainedList = null;
        if (request.SongId is { } songId)
        {
            if (viewModel.FindSong(songId) is { } song) songs.Add(song);
        }
        else if (request.EntityId is { } entityId)
        {
            var catalog = viewModel.GetCatalogSnapshot();
            if (request.IsAlbum)
            {
                album = catalog.Albums.FirstOrDefault(value => value.Id == entityId);
                tracks = catalog.GetAlbumTracks(entityId);
                songs.AddRange(tracks.Select(track => viewModel.FindSong(track.SongId)).OfType<LibrarySongItem>());
            }
            else
            {
                artist = catalog.Artists.FirstOrDefault(value => value.Id == entityId);
                songs.AddRange(catalog.GetArtistSongIds(entityId).Select(viewModel.FindSong).OfType<LibrarySongItem>()
                    .OrderBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase));
            }
            retainedList = (pageContent.GetValueOrDefault(request.Key) as EntityDetailsView)?.SongList;
        }
        else
        {
            retainedList = request.Key == "收藏" ? FavoriteSongs : LibrarySongs;
            songs.AddRange(request.Key == "收藏" ? viewModel.FavoriteResults : viewModel.Results);
        }
        if (viewModel.HideUnavailable) songs.RemoveAll(song => !song.IsAvailable);
        int first = retainedList?.ItemsPanelRoot is ItemsStackPanel panel ? Math.Max(0, panel.FirstVisibleIndex) : 0;
        // Prepare a bounded first screen (or the retained return position), not the library.
        int count = Math.Clamp((int)Math.Ceiling(Math.Max(400, BrowserCard.ActualHeight) / 64) + 2, 8, 24);
        first = Math.Min(first, Math.Max(0, songs.Count - count));
        var visible = songs.Skip(first).Take(count).ToArray();
        int completed = 0, total = visible.Length * 2 + (album is null ? 0 : 1);
        SetPagePreparationProgress(0, total, "正在读取本地资料…");
        await PrepareSongsAsync(visible);
        // Newly read tags may change ordering or membership. Re-capture one bounded
        // first screen so the destination does not attach a different, unprepared set.
        if (request.SongId is null)
        {
            IEnumerable<LibrarySongItem> latest;
            if (request.EntityId is { } latestEntity)
            {
                var catalog = viewModel.GetCatalogSnapshot();
                if (request.IsAlbum)
                {
                    album = catalog.Albums.FirstOrDefault(value => value.Id == latestEntity);
                    tracks = catalog.GetAlbumTracks(latestEntity);
                    latest = tracks.Select(track => viewModel.FindSong(track.SongId)).OfType<LibrarySongItem>();
                }
                else latest = catalog.GetArtistSongIds(latestEntity).Select(viewModel.FindSong).OfType<LibrarySongItem>()
                    .OrderBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase);
            }
            else latest = request.Key == "收藏" ? viewModel.FavoriteResults : viewModel.Results;
            if (viewModel.HideUnavailable) latest = latest.Where(song => song.IsAvailable);
            int latestFirst = Math.Min(first, Math.Max(0, latest.Count() - count));
            var additional = latest.Skip(latestFirst).Take(count).Where(song => !visible.Contains(song)).ToArray();
            if (additional.Length > 0)
            {
                total += additional.Length * 2;
                await PrepareSongsAsync(additional);
            }
        }
        if (album is not null)
        {
            SetPagePreparationProgress(completed, total, "正在准备专辑…");
            await EnsureAlbumArtworkAsync(album, tracks, token);
            token.ThrowIfCancellationRequested();
            SetPagePreparationProgress(++completed, total, "页面已就绪");
        }
        if (artist is not null)
        {
            var profile = artist.ExternalIds.Select(viewModel.GetCachedArtistProfile).Select(value => value?.Profile)
                .FirstOrDefault(value => value?.ImageUrl.Length > 0);
            if (profile is not null && Uri.TryCreate(profile.ImageUrl, UriKind.Absolute, out var url) && ArtworkDiskCache.IsAllowed(url))
                await LoadArtistImageAsync(artworkCache.GetCachePath(url)).WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
        // Materialize the detached entity before committing Frame history, so its
        // first attached template already has its prepared hero and row snapshot.
        if (request.EntityId is { } preparedEntity)
        {
            if (pageContent.GetValueOrDefault(request.Key) is not EntityDetailsView view)
            {
                view = CreateEntityDetails(request.Key, preparedEntity, request.IsAlbum);
                pageContent[request.Key] = view;
            }
            RefreshEntityDetails(view, preparedEntity, request.IsAlbum);
        }
        TrimListCoverCache();

        async Task PrepareSongsAsync(IReadOnlyList<LibrarySongItem> items)
        {
            for (int offset = 0; offset < items.Count; offset += 2)
            {
                await Task.WhenAll(items.Skip(offset).Take(2).Select(async song =>
                {
                    token.ThrowIfCancellationRequested();
                    await LoadSongMetadataAsync(song).WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    SetPagePreparationProgress(++completed, total, "正在准备封面…");
                    if (request.SongId is not null) await LoadCoverAsync(song, token);
                    else await LoadListCoverAsync(song, prepare: true, token: token);
                    token.ThrowIfCancellationRequested();
                    SetPagePreparationProgress(++completed, total, "正在准备封面…");
                }));
            }
        }
    }
}
