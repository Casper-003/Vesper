using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly HomeRecommendations homeRecommendations = new();
    private readonly Dictionary<(bool Artist, string Id), HomeCard> homeCards = [];
    private readonly DispatcherTimer homeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer homeArtworkTimer = new() { Interval = TimeSpan.FromMilliseconds(175) };
    private HashSet<HomeCard> homeVisibleCards = [];
    private CancellationTokenSource? homeArtworkCancellation;
    private bool homeDirty = true;
    private long homeArtistImageRevision;

    private void InitializeHome()
    {
        HomeSections.CardClicked += card =>
        {
            if (card.IsArtist) OpenArtist(card.Id); else OpenEntitySong(card.Id);
        };
        HomeSections.ViewportChanged += ScheduleHomeArtwork;
        HomeSections.VerticalWheel += delta =>
        {
            if (overviewMotions.TryGetValue(HomeOverview, out var motion))
                motion.Viewer.ChangeView(null, Math.Clamp(motion.Viewer.VerticalOffset - delta, 0, motion.Viewer.ScrollableHeight), null);
        };
        HomePage.Loaded += (_, _) => ScheduleHomeRefresh();
        HomePage.Unloaded += (_, _) =>
        {
            homeArtworkCancellation?.Cancel(); homeVisibleCards.Clear();
            foreach (var card in homeCards.Values) card.SetImage(null);
        };
        viewModel.Results.CollectionChanged += OnHomeCollectionChanged;
        viewModel.FavoriteResults.CollectionChanged += OnHomeCollectionChanged;
        viewModel.PropertyChanged += OnHomeModelChanged;
        viewModel.DetailsPresentationInvalidated += OnHomeDetailsChanged;
        homeRefreshTimer.Tick += (_, _) => { homeRefreshTimer.Stop(); if (HomePage.IsLoaded) RefreshHome(); };
        homeArtworkTimer.Tick += (_, _) => { homeArtworkTimer.Stop(); if (!overviewScrolling) QueueHomeArtwork(); };
        Closed += (_, _) =>
        {
            homeRefreshTimer.Stop(); homeArtworkTimer.Stop(); homeArtworkCancellation?.Cancel(); homeArtworkCancellation?.Dispose();
            viewModel.Results.CollectionChanged -= OnHomeCollectionChanged;
            viewModel.FavoriteResults.CollectionChanged -= OnHomeCollectionChanged;
            viewModel.PropertyChanged -= OnHomeModelChanged;
            viewModel.DetailsPresentationInvalidated -= OnHomeDetailsChanged;
        };
    }
    private void OnHomeCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleHomeRefresh();
    private void OnHomeDetailsChanged(object? sender, EventArgs args) => ScheduleHomeRefresh();
    private void OnHomeModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Busy) or nameof(MainViewModel.HideUnavailable)) ScheduleHomeRefresh();
    }
    private void ScheduleHomeRefresh()
    {
        homeDirty = true;
        if (!closed && HomePage.IsLoaded && !homeRefreshTimer.IsEnabled) homeRefreshTimer.Start();
    }
    private void RefreshHome()
    {
        if (closed || !homeDirty) return;
        homeDirty = false;
        var selected = homeRecommendations.Select(viewModel.Busy ? [] : viewModel.Results.Where(song => song.IsAvailable).Select(song => song.Song));
        var catalog = viewModel.GetCatalogSnapshot();
        var visibleIds = viewModel.Results.Select(song => song.Song.Id).ToHashSet(StringComparer.Ordinal);
        var performerIds = catalog.SongAssociations.Where(pair => visibleIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value.Credits).Where(credit => credit.Role == "performer").Select(credit => credit.ArtistId).ToHashSet(StringComparer.Ordinal);
        var artists = catalog.Artists.Where(artist => performerIds.Contains(artist.Id))
            .OrderBy(artist => artist.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(artist => artist.Id, StringComparer.Ordinal)
            .Select(ArtistCard).ToArray();
        var recommended = selected.Select(viewModel.FindSong).OfType<LibrarySongItem>()
            .Where(song => !viewModel.HideUnavailable || song.IsAvailable).Select(SongCard).ToArray();
        var favorites = viewModel.FavoriteResults.Select(SongCard).ToArray();
        HomeSections.Bind(artists, recommended, favorites);
        var retained = artists.Concat(recommended).Concat(favorites).ToHashSet();
        foreach (var key in homeCards.Where(pair => !retained.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) homeCards.Remove(key);
        ScheduleHomeArtwork();
    }
    private HomeCard SongCard(LibrarySongItem song)
    {
        var key = (false, song.Song.Id);
        if (!homeCards.TryGetValue(key, out var card)) homeCards[key] = card = new(song.Song.Id, false);
        card.Update(song); return card;
    }
    private HomeCard ArtistCard(CatalogArtist artist)
    {
        var key = (true, artist.Id);
        if (!homeCards.TryGetValue(key, out var card)) homeCards[key] = card = new(artist.Id, true);
        card.Update(artist); return card;
    }
    private void ScheduleHomeArtwork()
    {
        if (!closed && HomeSections.IsLoaded && !homeArtworkTimer.IsEnabled) homeArtworkTimer.Start();
    }
    private void QueueHomeArtwork()
    {
        if (closed || !HomeSections.IsLoaded || (ContentFrame.Content as ShellPage)?.PageKey != "首页") return;
        var visible = HomeSections.VisibleCards(BrowserCard).ToHashSet();
        if (!homeVisibleCards.SetEquals(visible))
        {
            homeArtworkCancellation?.Cancel(); homeArtworkCancellation?.Dispose();
            homeArtworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
            foreach (var card in homeVisibleCards.Except(visible)) card.SetImage(null);
            homeVisibleCards = visible;
        }
        if (homeArtworkCancellation is null || homeArtworkCancellation.IsCancellationRequested)
            homeArtworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        foreach (var card in visible)
        {
            if (card.Artist is { } artist)
            {
                if (card.Image is not null && card.ArtistImageRevision == homeArtistImageRevision) continue;
                var image = GetArtistImage(artist, true, out bool logo) as ImageSource;
                if (image is not null) { card.SetImage(image, logo); card.ArtistImageRevision = homeArtistImageRevision; }
            }
            else if (card.Song is { } song && (card.Image is null || card.SongImageIdentity != song.ArtworkIdentity))
                _ = LoadHomeSongArtworkAsync(card, homeArtworkCancellation.Token);
        }
    }
    private async Task LoadHomeSongArtworkAsync(HomeCard card, CancellationToken token)
    {
        try
        {
            var song = card.Song!;
            string identity = song.ArtworkIdentity;
            var image = await ReadHomeArtworkAsync(song, token);
            if (!closed && !token.IsCancellationRequested && homeVisibleCards.Contains(card) && song.ArtworkIdentity == identity)
            { card.SetImage(image); card.SongImageIdentity = image is null ? "" : identity; }
        }
        catch (OperationCanceledException) { }
    }
    private async Task PrepareHomeResourcesAsync(CancellationToken token)
    {
        homeDirty = true; RefreshHome();
        int count = Math.Clamp((int)Math.Ceiling(Math.Max(320, BrowserCard.ActualWidth) / 176), 2, 8);
        var songs = HomeSections.RecommendedCards.Take(count).Concat(HomeSections.FavoriteCards.Take(count))
            .Select(card => card.Song).OfType<LibrarySongItem>().Distinct().ToArray();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await Task.WhenAll(songs.Select(song => ReadHomeArtworkAsync(song, budget.Token)));
            foreach (var artist in HomeSections.ArtistCards.Take(Math.Max(1, count - 1)).Select(card => card.Artist!))
            {
                GetArtistImage(artist, true);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Settle placeholders; background assets continue. */ }
        token.ThrowIfCancellationRequested();
    }
}
