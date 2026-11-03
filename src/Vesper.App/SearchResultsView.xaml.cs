using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed class SearchResultRow(SongGroupHit hit, LibrarySongItem? song)
{
    public SongGroupHit Hit { get; } = hit;
    public LibrarySongItem? Song { get; } = song;
    public string Title => Hit.Title;
    public string Subtitle => string.Join(" · ", new[] { Song?.Resolution ?? "", Hit.Artist.Length > 0 ? Hit.Artist : "未知艺人", IsAvailable ? "" : "文件不可用" }.Where(value => value.Length > 0));
    public bool IsAvailable => Hit.IsAvailable;
    public bool IsGroup => Hit.Versions.Count > 1;
    public double Opacity => IsAvailable ? 1 : .5;
    public Visibility GroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DirectVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    public string MatchLabel => Hit.BestMatchedVersionId != Hit.RepresentativeVersionId
        ? "命中：" + (Hit.Versions.FirstOrDefault(version => version.VersionId == Hit.BestMatchedVersionId)?.VersionName ?? "演唱版本") : "";
    public Visibility MatchVisibility => MatchLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class SearchResultsView : UserControl
{
    private readonly MainWindow host;
    private readonly List<SearchResultRow> rows = [];
    private SearchResponse? response;
    private string? nextSongs;
    private string[] savedOrder = [];
    private string? anchor;
    private double anchorOffset;
    private bool restorePending;
    private long restorationGeneration;
    private int operations;
    private bool pointerActive;
    private Flyout? versionsFlyout;
    public string Query { get; private set; } = "";
    public long Revision => response?.IndexRevision ?? -1;
    public int LoadedSongCount => rows.Count;
    internal FrameworkElement? VersionChoices => versionsFlyout?.Content as FrameworkElement;
    public bool IsInteracting => pointerActive || operations > 0 || versionsFlyout?.IsOpen == true;
    internal Action? InteractionEnded;

    public SearchResultsView(MainWindow host)
    {
        this.host = host;
        InitializeComponent();
        Songs.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => pointerActive = true), true);
        void ReleasePointer(object sender, PointerRoutedEventArgs args)
        { pointerActive = false; if (!IsInteracting) InteractionEnded?.Invoke(); }
        Songs.AddHandler(PointerReleasedEvent, new PointerEventHandler(ReleasePointer), true);
        Songs.AddHandler(PointerCanceledEvent, new PointerEventHandler(ReleasePointer), true);
        Songs.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(ReleasePointer), true);
        Loaded += async (_, _) => { if (restorePending) await RestorePositionAsync(); };
    }

    public void ShowStatus(string message, bool retry = false)
    {
        SearchState.Text = message;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Clear(string query = "")
    {
        restorationGeneration++;
        Query = query; response = null; rows.Clear(); nextSongs = null;
        Songs.ItemsSource = null;
        EntityCards.Visibility = ArtistCard.Visibility = AlbumCard.Visibility = SongHeading.Visibility = MoreSongs.Visibility = Visibility.Collapsed;
        anchor = null; savedOrder = []; restorePending = false;
        ShowStatus(""); versionsFlyout?.Hide();
    }

    public void Apply(SearchResponse result, bool restore)
    {
        restorationGeneration++;
        if (restore) CapturePosition();
        response = result; Query = result.Query;
        rows.Clear();
        foreach (var hit in result.Songs.Items) rows.Add(new(hit, host.Model.FindSong(hit.Versions.FirstOrDefault(version => version.VersionId == hit.RepresentativeVersionId)?.SongId ?? "")));
        nextSongs = result.Songs.ContinuationToken;
        var artist = result.Artists.Items.FirstOrDefault();
        var album = result.Albums.Items.FirstOrDefault();
        ArtistCard.Visibility = artist is null ? Visibility.Collapsed : Visibility.Visible;
        AlbumCard.Visibility = album is null ? Visibility.Collapsed : Visibility.Visible;
        ArtistName.Text = artist?.Name ?? ""; AlbumName.Text = album?.Name ?? "";
        AlbumArtist.Text = album?.Artist ?? "";
        EntityCards.Visibility = artist is not null || album is not null ? Visibility.Visible : Visibility.Collapsed;
        Songs.ItemsSource = rows.ToArray();
        SongHeading.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreSongs.Visibility = result.Songs.HasMore ? Visibility.Visible : Visibility.Collapsed;
        ShowStatus(result.Status == SearchStatus.NoMatch ? "无匹配" : "");
        restorePending = restore;
        if (restore && IsLoaded) _ = RestorePositionAsync();
        else if (!restore) FindScroll()?.ChangeView(null, 0, null, true);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (response is null || nextSongs is null || Revision != host.Model.IndexRevision) return;
        var page = response.GetSongsPage(nextSongs);
        CapturePosition();
        foreach (var hit in page.Items) rows.Add(new(hit, host.Model.FindSong(hit.Versions.FirstOrDefault(version => version.VersionId == hit.RepresentativeVersionId)?.SongId ?? "")));
        nextSongs = page.ContinuationToken;
        Songs.ItemsSource = rows.ToArray();
        MoreSongs.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _ = RestorePositionAsync();
    }

    public void CapturePosition()
    {
        savedOrder = rows.Select(row => row.Hit.GroupId).ToArray();
        if (Songs.ItemsPanelRoot is not ItemsStackPanel panel || rows.Count == 0) return;
        int index = Math.Clamp(panel.FirstVisibleIndex, 0, rows.Count - 1);
        anchor = rows[index].Hit.GroupId;
        if (Songs.ContainerFromIndex(index) is FrameworkElement container)
            anchorOffset = container.TransformToVisual(Songs).TransformPoint(default).Y;
        restorePending = true;
    }

    public async Task RestorePositionAsync()
    {
        if (!restorePending || !IsLoaded) return;
        long generation = ++restorationGeneration;
        var expectedResponse = response;
        restorePending = false;
        int index = rows.FindIndex(row => row.Hit.GroupId == anchor);
        if (index < 0 && anchor is not null)
        {
            int previous = Array.IndexOf(savedOrder, anchor);
            index = Enumerable.Range(0, savedOrder.Length).OrderBy(i => Math.Abs(i - previous))
                .Select(i => rows.FindIndex(row => row.Hit.GroupId == savedOrder[i])).FirstOrDefault(i => i >= 0, -1);
        }
        if (index < 0) { FindScroll()?.ChangeView(null, 0, null, true); return; }
        Songs.ScrollIntoView(rows[index], ScrollIntoViewAlignment.Leading);
        Songs.UpdateLayout();
        string expectedId = rows[index].Hit.GroupId;
        await Task.Yield();
        if (!IsLoaded || generation != restorationGeneration || !ReferenceEquals(response, expectedResponse)
            || index >= rows.Count || rows[index].Hit.GroupId != expectedId) return;
        if (Songs.ContainerFromIndex(index) is FrameworkElement container && FindScroll() is { } scroll)
            scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + container.TransformToVisual(Songs).TransformPoint(default).Y - anchorOffset), null, true);
    }

    private ScrollViewer? FindScroll() => MainWindow.FindSearchPart<ScrollViewer>(Songs);

    private void OnResultsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 520;
        Grid.SetColumn(AlbumCard, narrow ? 0 : 1); Grid.SetRow(AlbumCard, narrow ? 1 : 0);
        Grid.SetColumnSpan(ArtistCard, narrow ? 2 : 1); Grid.SetColumnSpan(AlbumCard, narrow ? 2 : 1);
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultRow row) return;
        if (row.IsGroup && Songs.ContainerFromItem(row) is FrameworkElement target) ShowVersions(row, target);
        else if (row.Hit.Versions.FirstOrDefault() is { } version) host.OpenSearchSong(version.SongId);
    }
    private void OnVersions(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: SearchResultRow row } target) ShowVersions(row, target); }

    private void ShowVersions(SearchResultRow row, FrameworkElement target)
    {
        versionsFlyout?.Hide();
        var list = new StackPanel { Spacing = 6, Width = Math.Max(220, Math.Min(600, Songs.ActualWidth - 60)) };
        FrameworkElement? best = null;
        foreach (var version in row.Hit.Versions)
        {
            var line = new Grid { ColumnSpacing = 10 };
            line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            string label = version.VersionName.Length > 0 ? version.VersionName : row.Title;
            var open = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Resources["SearchVersionButtonStyle"] };
            open.Click += (_, _) => { versionsFlyout?.Hide(); host.OpenSearchSong(version.SongId); };
            var add = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 16 }, IsEnabled = version.IsAvailable, Tag = version };
            ToolTipService.SetToolTip(add, "加入待播");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "加入待播 · " + label);
            add.Click += async (_, _) => { operations++; try { await host.QueueSearchVersionAsync(version); } finally { EndOperation(); } };
            Grid.SetColumn(add, 1); line.Children.Add(open); line.Children.Add(add); list.Children.Add(line);
            if (version.VersionId == row.Hit.BestMatchedVersionId) best = open;
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        versionsFlyout = new Flyout { Content = scroll };
        versionsFlyout.Closed += (_, _) => InteractionEnded?.Invoke();
        var match = best;
        versionsFlyout.Opened += (_, _) => { match?.StartBringIntoView(); (match as Control)?.Focus(FocusState.Programmatic); };
        versionsFlyout.ShowAt(target);
    }

    private async void OnQueue(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchResultRow row } || row.IsGroup || row.Hit.Versions.FirstOrDefault() is not { } version) return;
        operations++;
        try { await host.QueueSearchVersionAsync(version); }
        finally { EndOperation(); }
    }
    private async void OnFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchResultRow { Song: { } song } }) return;
        operations++; try { await host.Model.ToggleFavoriteAsync(song); } finally { EndOperation(); }
    }
    private void EndOperation() { operations = Math.Max(0, operations - 1); if (!IsInteracting) InteractionEnded?.Invoke(); }
    private void OnRetry(object sender, RoutedEventArgs e) => host.RetrySearch();
}
