using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;
using Windows.System;

namespace Vesper.App;

// The shared song object owns favorite, cover, availability and metadata changes.
// This wrapper adds the release membership's real position and its virtualized presentation.
public sealed class EntitySongRow(LibrarySongItem song, string number, int? discNumber = null, int? trackNumber = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public LibrarySongItem Song { get; private set; } = song;
    public string Number { get; private set; } = number;
    public int? DiscNumber { get; private set; } = discNumber is > 0 ? discNumber : null;
    public int? TrackNumber { get; private set; } = trackNumber is > 0 ? trackNumber : null;
    public string DiscHeading { get; private set; } = "";
    public Visibility DiscHeadingVisibility => DiscHeading.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public DataTemplate? SongTemplate { get; private set; }
    public Visibility NumberVisibility => Number.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    internal string Key { get; set; } = song.Song.Id;

    internal void Update(LibrarySongItem value, string number, int? discNumber, int? trackNumber,
        string discHeading, DataTemplate template)
    {
        if (!ReferenceEquals(Song, value)) { Song = value; Notify(nameof(Song)); }
        if (Number != number) { Number = number; Notify(nameof(Number)); Notify(nameof(NumberVisibility)); }
        if (DiscNumber != discNumber) { DiscNumber = discNumber; Notify(nameof(DiscNumber)); }
        if (TrackNumber != trackNumber) { TrackNumber = trackNumber; Notify(nameof(TrackNumber)); }
        if (DiscHeading != discHeading)
        { DiscHeading = discHeading; Notify(nameof(DiscHeading)); Notify(nameof(DiscHeadingVisibility)); }
        if (!ReferenceEquals(SongTemplate, template)) { SongTemplate = template; Notify(nameof(SongTemplate)); }
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class EntityAlbumCard(string id, string name, string artist, object? cover) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; private set; } = name;
    public string Artist { get; private set; } = artist;
    public object? Cover { get; private set; } = cover;
    public string AccessibilityName => "专辑：" + Name;
    public Visibility CoverVisibility => Cover is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => Cover is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ArtistVisibility => Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void Update(EntityAlbumCard value)
    {
        if (Name != value.Name) { Name = value.Name; Notify(nameof(Name)); Notify(nameof(AccessibilityName)); }
        if (Artist != value.Artist) { Artist = value.Artist; Notify(nameof(Artist)); Notify(nameof(ArtistVisibility)); }
        if (!ReferenceEquals(Cover, value.Cover))
        { Cover = value.Cover; Notify(nameof(Cover)); Notify(nameof(CoverVisibility)); Notify(nameof(PlaceholderVisibility)); }
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class EntityDetailsView : UserControl
{
    private readonly MainWindow host;
    private readonly bool isAlbum;
    private readonly ObservableCollection<EntitySongRow> rows = [];
    private readonly ObservableCollection<EntityAlbumCard> albums = [];
    private string[] savedOrder = [];
    private string? anchor;
    private double anchorOffset, savedOffset;
    private bool hasPosition, restorePending;
    private long restorationGeneration;
    private readonly HashSet<uint> activePointers = [];
    private readonly HashSet<VirtualKey> activeKeys = [];
    private PendingDetails? pendingDetails;
    private bool interactionFlushQueued;
    private long interactionGeneration;

    private sealed record PendingDetails(string Title, string Subtitle, object? Cover,
        EntitySongRow[] Songs, EntityAlbumCard[] Albums);

    internal ListView SongList => EntitySongs;
    internal Grid PageGrid => EntityPage;
    internal Grid HeroLayer => EntityHeroLayer;
    internal Grid HeaderSpace => EntityHeaderSpace;
    internal Grid ScrollRail => EntityScrollRail;
    internal Canvas TitleMotionHost => EntityTitleMotion;
    internal TextBlock HeroTitle => EntityHeroTitle;
    internal FrameworkElement HeroArtwork => EntityCoverFrame;
    internal FrameworkElement HeroSubtitle => EntitySubtitle;
    internal ScrollBar ScrollBarPart => EntityScrollBar;
    internal bool IsAlbum => isAlbum;
    internal bool HasSubtitle => EntitySubtitle.Visibility == Visibility.Visible;
    internal LinearGradientBrush HeroBackground => (LinearGradientBrush)Resources["EntityHeroBrush"];
    internal uint? ArtworkColor { get; private set; }
    private HeroPaletteColors? heroPalette;
    internal void ApplyHeroPalette(bool dark, uint? artworkColor)
    {
        ArtworkColor = artworkColor;
        var palette = HeroPalette.Create(dark, artworkColor);
        if (heroPalette == palette) return;
        heroPalette = palette;
        HeroBackground.GradientStops[0].Color = MainWindow.HeroColor(palette.Top);
        HeroBackground.GradientStops[1].Color = MainWindow.HeroColor(palette.Middle);
        HeroBackground.GradientStops[2].Color = MainWindow.HeroColor(palette.Bottom);
        EntityHeroTitle.Foreground = new SolidColorBrush(MainWindow.HeroColor(palette.Foreground));
        EntitySubtitle.Foreground = new SolidColorBrush(MainWindow.HeroColor(palette.SecondaryForeground));
    }
    internal bool IsInteracting => activePointers.Count > 0 || activeKeys.Count > 0;

    public EntityDetailsView(MainWindow host, bool isAlbum)
    {
        this.host = host;
        this.isAlbum = isAlbum;
        InitializeComponent();
        host.UpdateEntityHeroColors(this, null);
        EntityCoverPlaceholder.Glyph = isAlbum ? "\uE93C" : "\uE716";
        EntityCoverFrame.CornerRadius = new CornerRadius(isAlbum ? 4 : 36);
        EntitySongs.ItemsSource = rows;
        RelatedAlbums.ItemsSource = albums;
        RelatedAlbums.Loaded += (_, _) => ResizeRelatedAlbumCards();
        RelatedAlbums.SizeChanged += (_, _) => ResizeRelatedAlbumCards();
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnInteractionPointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnInteractionPointerReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnInteractionPointerReleased), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnInteractionPointerReleased), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnInteractionKeyDown), true);
        AddHandler(KeyUpEvent, new KeyEventHandler(OnInteractionKeyUp), true);
        LostFocus += (_, _) => { activeKeys.Clear(); QueuePendingDetails(); };
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            // Frame.Navigating captures the usable position before detaching.
            // Unload must not overwrite it with a reset/partially detached viewport.
            restorationGeneration++;
            interactionGeneration++;
            activePointers.Clear(); activeKeys.Clear();
            pendingDetails = null;
            interactionFlushQueued = false;
        };
    }

    public void Apply(string title, string subtitle, object? cover, IReadOnlyList<EntitySongRow> songs,
        IReadOnlyList<EntityAlbumCard> relatedAlbums)
    {
        if (IsInteracting)
        {
            // Keep the latest complete membership snapshot. A background index
            // update cannot move a pressed row underneath the pending click.
            pendingDetails = new(title, subtitle, cover, songs.ToArray(), relatedAlbums.ToArray());
            return;
        }
        pendingDetails = null;
        ApplyDetails(title, subtitle, cover, songs, relatedAlbums);
    }

    private void ApplyDetails(string title, string subtitle, object? cover, IReadOnlyList<EntitySongRow> songs,
        IReadOnlyList<EntityAlbumCard> relatedAlbums)
    {
        IReadOnlyList<AlbumTrackLabel> trackLabels = isAlbum
            ? AlbumTrackLayout.Build(songs.Select(song => new AlbumTrackPosition(song.DiscNumber, song.TrackNumber)).ToArray()) : [];
        string RowNumber(int index) => !isAlbum ? "" : songs[index].TrackNumber is > 0 ? trackLabels[index].Number
            : songs[index].Number.Length > 0 ? songs[index].Number : "—";
        bool layoutChanged = EntityHeroTitle.Text != title || EntitySubtitle.Text != subtitle
            || !rows.Select(row => row.Song.Song.Id).SequenceEqual(songs.Select(row => row.Song.Song.Id))
            || isAlbum && !rows.Select(row => (row.Number, row.DiscHeading)).SequenceEqual(
                songs.Select((_, index) => (RowNumber(index), trackLabels[index].DiscHeading)))
            || !albums.Select(album => album.Id).SequenceEqual(isAlbum ? Enumerable.Empty<string>() : relatedAlbums.Select(album => album.Id).Distinct());
        // Cover/favorite/availability notifications should keep the existing
        // viewport untouched instead of repeatedly scrolling an anchor into view.
        if (layoutChanged)
        {
            if (IsLoaded) CapturePosition();
            restorationGeneration++;
        }
        EntityHeroTitle.Text = title;
        EntitySubtitle.Text = subtitle;
        EntitySubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(EntitySubtitle, subtitle);
        EntityCover.Source = cover as ImageSource;
        host.UpdateEntityHeroColors(this, cover);
        EntityCover.Visibility = cover is null ? Visibility.Collapsed : Visibility.Visible;
        EntityCoverPlaceholder.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;

        // Reconcile by song and occurrence, allowing the same recording to occupy
        // distinct positions while retaining existing row objects and bindings.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var previousRows = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var orderedRows = new List<EntitySongRow>(songs.Count);
        for (int index = 0; index < songs.Count; index++)
        {
            var desired = songs[index];
            string songId = desired.Song.Song.Id;
            int occurrence = occurrences.GetValueOrDefault(songId);
            occurrences[songId] = occurrence + 1;
            string key = songId + ":" + occurrence;
            var row = previousRows.GetValueOrDefault(key) ?? new EntitySongRow(desired.Song, desired.Number) { Key = key };
            row.Update(desired.Song, RowNumber(index), desired.DiscNumber, desired.TrackNumber,
                isAlbum ? trackLabels[index].DiscHeading : "", host.EntitySongTemplate);
            orderedRows.Add(row);
        }
        Reconcile(rows, orderedRows);

        var previousAlbums = albums.ToDictionary(album => album.Id, StringComparer.Ordinal);
        var orderedAlbums = new List<EntityAlbumCard>();
        if (!isAlbum)
            foreach (var desired in relatedAlbums.DistinctBy(album => album.Id))
            {
                var album = previousAlbums.GetValueOrDefault(desired.Id) ?? desired;
                album.Update(desired);
                orderedAlbums.Add(album);
            }
        Reconcile(albums, orderedAlbums);
        RelatedAlbumsSection.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EntitySongHeading.Visibility = rows.Count > 0 && !isAlbum ? Visibility.Visible : Visibility.Collapsed;
        AlbumColumnHeader.Visibility = rows.Count > 0 && isAlbum ? Visibility.Visible : Visibility.Collapsed;
        EntityEmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        restorePending |= layoutChanged && hasPosition;
        if (IsLoaded)
        {
            host.ConfigureEntityOverview(this);
            if (restorePending) _ = RestorePositionAsync();
        }
    }

    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        if (target.Count == desired.Count)
        {
            bool unchanged = true;
            for (int index = 0; index < desired.Count; index++)
                if (!ReferenceEquals(target[index], desired[index])) { unchanged = false; break; }
            if (unchanged) return;
        }
        var wanted = new HashSet<T>(desired);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!wanted.Contains(target[index])) target.RemoveAt(index);
        if (target.Count == 0)
        {
            foreach (var item in desired) target.Add(item);
            return;
        }

        // As in MainViewModel.SyncRows, preserve the largest ordered subset.
        // Rotating one song to the end emits one Move rather than moving every
        // other realized row and disturbing their shared song bindings.
        var oldIndices = target.Select((item, index) => (item, index)).ToDictionary(pair => pair.item, pair => pair.index);
        int[] positions = desired.Select(item => oldIndices.GetValueOrDefault(item, -1)).ToArray();
        int[] tails = new int[desired.Count], previous = new int[desired.Count];
        Array.Fill(previous, -1);
        int length = 0;
        for (int index = 0; index < desired.Count; index++)
        {
            if (positions[index] < 0) continue;
            int low = 0, high = length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (positions[tails[middle]] < positions[index]) low = middle + 1; else high = middle;
            }
            if (low > 0) previous[index] = tails[low - 1];
            tails[low] = index;
            if (low == length) length++;
        }
        var keep = new HashSet<T>();
        if (length > 0)
            for (int index = tails[length - 1]; index >= 0; index = previous[index]) keep.Add(desired[index]);
        T? next = null;
        for (int index = desired.Count - 1; index >= 0; index--)
        {
            var item = desired[index];
            if (!keep.Contains(item))
            {
                int from = target.IndexOf(item);
                int to = next is null ? target.Count : target.IndexOf(next);
                if (from < 0) target.Insert(to, item);
                else
                {
                    if (from < to) to--;
                    if (from != to) target.Move(from, to);
                }
            }
            next = item;
        }
    }

    private void OnInteractionPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        activePointers.Add(args.Pointer.PointerId);
        restorationGeneration++;
        restorePending = false;
    }

    private void OnInteractionPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        activePointers.Remove(args.Pointer.PointerId);
        QueuePendingDetails();
    }

    private void OnDiscHeadingPointer(object sender, PointerRoutedEventArgs args) => args.Handled = true;

    private void OnInteractionKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        activeKeys.Add(args.Key);
        restorationGeneration++;
        restorePending = false;
    }

    private void OnInteractionKeyUp(object sender, KeyRoutedEventArgs args)
    {
        activeKeys.Remove(args.Key);
        QueuePendingDetails();
    }

    private void QueuePendingDetails()
    {
        if (IsInteracting || pendingDetails is null || interactionFlushQueued) return;
        interactionFlushQueued = true;
        long generation = interactionGeneration;
        // Native Button.Click/ItemClick must resolve their original song before
        // a metadata-driven reorder is allowed to recycle its row container.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != interactionGeneration) return;
            interactionFlushQueued = false;
            if (!IsLoaded || IsInteracting || pendingDetails is not { } details) return;
            pendingDetails = null;
            ApplyDetails(details.Title, details.Subtitle, details.Cover, details.Songs, details.Albums);
        });
    }

    internal void RefreshHeaderMetrics(double heroHeight)
    {
        EntityHeaderSpace.Height = Math.Max(0, heroHeight - 48 + 16);
        double width = Math.Max(1, EntityPage.ActualWidth);
        double padding = Math.Clamp(width * .045, 24, 96);
        double gap = Math.Clamp(width * .03, 20, 64);
        // The image leads the expanded header. Reserve a readable text column
        // when the browser is narrowed by either sidebar.
        double artworkSize = Math.Max(64, Math.Min(Math.Clamp(heroHeight * .70, 112, 352),
            Math.Max(64, (width - padding * 2 - gap) * .46)));
        artworkSize = Math.Min(artworkSize, Math.Max(48, heroHeight - 24));
        EntityCoverFrame.Width = EntityCoverFrame.Height = artworkSize;
        EntityCoverFrame.CornerRadius = new CornerRadius(isAlbum ? 4 : artworkSize / 2);
        EntityCoverFrame.Margin = new Thickness(padding, Math.Max(0, (heroHeight - artworkSize) / 2), 0, 0);
        EntityCoverFrame.CenterPoint = new((float)(artworkSize / 2), (float)(artworkSize / 2), 0);
        EntityCoverPlaceholder.FontSize = Math.Clamp(artworkSize * .32, 28, 88);
        double textLeft = padding + artworkSize + gap;
        EntityTitleMotion.Margin = new Thickness(textLeft, 0, 0, 0);
        EntitySubtitle.MaxWidth = Math.Max(1, width - textLeft - padding);
        ResizeRelatedAlbumCards();
    }

    private void ResizeRelatedAlbumCards()
    {
        if (RelatedAlbums.ItemsPanelRoot is WrapGrid panel && RelatedAlbums.ActualWidth > 0)
            panel.ItemWidth = Math.Max(1, Math.Min(292, RelatedAlbums.ActualWidth));
    }

    internal double RefreshTextMetrics(double heroHeight, double titleHeight)
    {
        const double subtitleGap = 16;
        double groupHeight = titleHeight + (HasSubtitle ? subtitleGap + 20 : 0);
        double titleTop = Math.Max(12, (heroHeight - groupHeight) / 2);
        EntitySubtitle.Margin = new Thickness(EntityTitleMotion.Margin.Left + Canvas.GetLeft(EntityHeroTitle),
            titleTop + titleHeight + subtitleGap, 24, 0);
        return titleTop;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        host.RegisterEntityOverview(this);
        if (restorePending || hasPosition) { restorePending = true; await RestorePositionAsync(); }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => host.ConfigureEntityOverview(this);

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EntitySongRow row) return;
        CapturePosition();
        host.OpenEntitySong(row.Song.Song.Id);
    }

    private void OnAlbumClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EntityAlbumCard album }) return;
        CapturePosition();
        host.OpenAlbum(album.Id);
    }

    internal void CapturePosition()
    {
        if (FindScroll() is not { } scroll) return;
        hasPosition = true;
        savedOffset = scroll.VerticalOffset;
        savedOrder = rows.Select(row => row.Key).ToArray();
        anchor = null;
        if (EntitySongs.ItemsPanelRoot is ItemsStackPanel panel && rows.Count > 0)
        {
            int index = Math.Clamp(panel.FirstVisibleIndex, 0, rows.Count - 1);
            if (EntitySongs.ContainerFromIndex(index) is FrameworkElement container)
            {
                double top = container.TransformToVisual(EntitySongs).TransformPoint(default).Y;
                // At the page header or related albums, preserve the actual offset
                // rather than pulling the first/last song into the viewport.
                if (top < EntitySongs.ActualHeight && top + container.ActualHeight > 0)
                { anchor = rows[index].Key; anchorOffset = top; }
            }
        }
        restorePending = true;
    }

    internal async Task RestorePositionAsync()
    {
        if (!restorePending || !IsLoaded || IsInteracting) return;
        long generation = ++restorationGeneration;
        restorePending = false;
        int index = anchor is null ? -1 : rows.ToList().FindIndex(row => row.Key == anchor);
        if (index < 0 && anchor is not null)
        {
            int oldIndex = Array.IndexOf(savedOrder, anchor);
            var currentIndex = rows.Select((row, position) => (row.Key, position)).ToDictionary(pair => pair.Key, pair => pair.position);
            index = Enumerable.Range(0, savedOrder.Length).OrderBy(position => Math.Abs(position - oldIndex))
                .Select(position => currentIndex.GetValueOrDefault(savedOrder[position], -1)).FirstOrDefault(position => position >= 0, -1);
        }
        if (index < 0)
        {
            EntitySongs.UpdateLayout();
            if (FindScroll() is { } rawScroll)
                rawScroll.ChangeView(null, Math.Clamp(savedOffset, 0, rawScroll.ScrollableHeight), null, true);
            return;
        }
        var target = rows[index];
        EntitySongs.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        EntitySongs.UpdateLayout();
        await Task.Yield();
        if (!IsLoaded || generation != restorationGeneration || index >= rows.Count || !ReferenceEquals(rows[index], target)) return;
        if (EntitySongs.ContainerFromIndex(index) is FrameworkElement container && FindScroll() is { } scroll)
        {
            double offset = scroll.VerticalOffset + container.TransformToVisual(EntitySongs).TransformPoint(default).Y - anchorOffset;
            scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, true);
        }
    }

    private ScrollViewer? FindScroll() => MainWindow.FindSearchPart<ScrollViewer>(EntitySongs);
}
