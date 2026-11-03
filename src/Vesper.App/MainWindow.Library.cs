using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Vesper.Core;

namespace Vesper.App;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed partial class MainWindow
{
    private readonly MainViewModel viewModel = new(SearchCheckStorePath());
    private bool libraryInitialized;
    private bool pickingFolder;
    private readonly DispatcherTimer libraryFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer detailsRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ActivityStatusPresenter activityDisplay = new();
    private readonly DispatcherTimer activityRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Diagnostics.Stopwatch activityClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly SemaphoreSlim metadataSlots = new(2, 2);
    private readonly CancellationTokenSource metadataCancellation = new();
    private readonly Dictionary<string, Task> metadataTasks = [];
    private readonly HashSet<string> explicitMetadataRequests = [];
    private LibrarySongItem? favoriteHero;

    private void InitializeLibraryPresentation()
    {
        DirectoryPanel.DataContext = viewModel;
        ActivityNavigationItem.DataContext = activityDisplay;
        activityRefreshTimer.Tick += (_, _) => activityDisplay.Flush(activityClock.Elapsed);
        activityRefreshTimer.Start();
        overviewIdleTimer.Tick += OnOverviewIdle;
        metadataRowsTimer.Tick += OnMetadataRowsReady;
        detailsRefreshTimer.Tick += (_, _) => { detailsRefreshTimer.Stop(); viewModel.FlushDetailsPresentation(); };
        viewModel.DetailsPresentationInvalidated += OnDetailsPresentationInvalidated;
        libraryFeedbackTimer.Tick += (_, _) =>
        {
            libraryFeedbackTimer.Stop();
            LibraryFeedback.Visibility = Visibility.Collapsed;
        };
        viewModel.PropertyChanged += OnLibraryPresentationChanged;
        viewModel.Results.CollectionChanged += OnOverviewRowsChanged;
        viewModel.FavoriteResults.CollectionChanged += OnOverviewRowsChanged;
    }

    private void OnLibraryPresentationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (closed) return;
        if (e.PropertyName == nameof(MainViewModel.LatestFavorite) && (ContentFrame.Content as ShellPage)?.PageKey == "收藏"
            && viewModel.LatestFavorite is { } latest && !ReferenceEquals(favoriteHero, latest))
        { favoriteHero = latest; _ = LoadDetailsAsync(latest); }
        if (e.PropertyName is nameof(MainViewModel.HasBackgroundActivity) or nameof(MainViewModel.ActivityLabel)
            or nameof(MainViewModel.ActivityTooltip) or nameof(MainViewModel.ActivityGlyph))
            RequestLibraryActivityStatus();
        if (e.PropertyName != nameof(MainViewModel.Status)) return;
        if (viewModel.Status.StartsWith("已加入待播", StringComparison.Ordinal)) return;
        libraryFeedbackTimer.Stop();
        LibraryFeedback.Visibility = Visibility.Visible;
        libraryFeedbackTimer.Start();
    }

    private void CloseLibraryPresentation()
    {
        libraryFeedbackTimer.Stop();
        activityRefreshTimer.Stop();
        overviewIdleTimer.Stop();
        metadataRowsTimer.Stop();
        detailsRefreshTimer.Stop();
        viewModel.DetailsPresentationInvalidated -= OnDetailsPresentationInvalidated;
        pendingMetadataRows.Clear();
        viewModel.PropertyChanged -= OnLibraryPresentationChanged;
        viewModel.Results.CollectionChanged -= OnOverviewRowsChanged;
        viewModel.FavoriteResults.CollectionChanged -= OnOverviewRowsChanged;
        metadataCancellation.Cancel();
        viewModel.Dispose();
    }
    private void OnDetailsPresentationInvalidated(object? sender, EventArgs e)
    { if (!closed && !detailsRefreshTimer.IsEnabled) detailsRefreshTimer.Start(); }

    private void OnLibraryPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        viewModel.HideUnavailable = HideUnavailableSwitch.IsOn;
        SavePreferences();
    }

    private void OnDirectoryFlyoutOpening(object sender, object e) => UpdateDirectoryPanelSize();

    private void UpdateDirectoryPanelSize()
    {
        if (Root.XamlRoot is not { } root || !ManageFoldersButton.IsLoaded) return;
        const double windowMargin = 24, flyoutGap = 8, presenterInsets = 50;

        var pageOrigin = LibraryPage.TransformToVisual(Root).TransformPoint(default);
        var buttonOrigin = ManageFoldersButton.TransformToVisual(LibraryActions).TransformPoint(default);
        // LibraryActions moves on the composition thread. Reuse its exact expression
        // rather than treating the unshifted XAML layout slot as the visible anchor.
        double headerHeight = LibraryHeroLayer.Height;
        double scrollOffset = overviewMotions.TryGetValue(LibrarySongs, out var motion) ? motion.Viewer.VerticalOffset : 0;
        double progress = Math.Clamp(scrollOffset / Math.Max(1, headerHeight - 48), 0, 1);
        double buttonTop = pageOrigin.Y + LibraryActions.Margin.Top + buttonOrigin.Y + headerHeight * (1 - progress);
        double spaceBelow = Math.Max(0, root.Size.Height - windowMargin - buttonTop - ManageFoldersButton.ActualHeight - flyoutGap);
        double spaceAbove = Math.Max(0, buttonTop - AppTitleBar.ActualHeight - windowMargin - flyoutGap);
        bool openBelow = spaceBelow >= 500 || spaceBelow >= spaceAbove;
        DirectoryFlyout.Placement = openBelow ? FlyoutPlacementMode.BottomEdgeAlignedRight : FlyoutPlacementMode.TopEdgeAlignedRight;

        double width = Math.Max(0, Math.Min(800, root.Size.Width - 2 * windowMargin) - presenterInsets);
        double height = Math.Max(0, Math.Min(500, openBelow ? spaceBelow : spaceAbove) - presenterInsets);
        if (DirectoryPanel.Width != width) DirectoryPanel.Width = width;
        if (DirectoryPanel.Height != height) DirectoryPanel.Height = height;
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (pickingFolder || !viewModel.CanManage) return;
        DirectoryFlyout.Hide();
        pickingFolder = true;
        viewModel.ChoosingDirectory = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { CommitButtonText = "添加文件夹" };
            var result = await picker.PickSingleFolderAsync();
            viewModel.ChoosingDirectory = false;
            pickingFolder = false;
            if (!closed && result is not null) await viewModel.AddDirectoryAsync(result.Path);
        }
        catch (Exception ex) { viewModel.ReportProblem($"无法添加文件夹：{ex.Message}"); }
        finally { pickingFolder = false; viewModel.ChoosingDirectory = false; }
    }

    private async void OnRefreshLibrary(object sender, RoutedEventArgs e) => await viewModel.RefreshAsync();

    private async void OnRemoveDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryDirectoryItem directory })
            await viewModel.RemoveDirectoryAsync(directory.Path);
    }

    private async void OnFavoriteSong(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibrarySongItem song })
            await viewModel.ToggleFavoriteAsync(song);
    }

    private async void OnQueueSong(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibrarySongItem song }) return;
        await QueueSongAsync(song, sender as FrameworkElement);
    }

    internal async Task QueueSongAsync(LibrarySongItem song, FrameworkElement? origin = null)
    {
        int position = viewModel.Queue.Pending.ToList().FindIndex(entry => entry.SongId == song.Song.Id);
        if (position >= 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "重复点歌",
                Content = $"这首歌已在待播列表第 {position + 1} 位，仍要再加入一次吗？",
                PrimaryButtonText = "再次加入", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        int previousCount = viewModel.Queue.Pending.Count;
        viewModel.AddToQueue(song);
        if (viewModel.Queue.Pending.Count > previousCount) ShowQueueAdded(song, origin);
    }

    private void OnSongRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement row) { PrepareLoadedSongRow(row); QueueVisibleRowMetadata(row); }
    }

    private void OnSongRowContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is LibrarySongItem) QueueVisibleRowMetadata(sender);
    }

    private Task LoadSongMetadataAsync(LibrarySongItem song, bool visibleOnly = false)
    {
        if (closed || song.MetadataLoaded || song.MetadataRetryAfter > DateTimeOffset.UtcNow) return Task.CompletedTask;
        if (!visibleOnly) explicitMetadataRequests.Add(song.Song.Id);
        if (metadataTasks.TryGetValue(song.Song.Id, out var pending) && !pending.IsCompleted) return pending;
        return metadataTasks[song.Song.Id] = ReadSongMetadataAsync(song);
    }

    private sealed record FileMetadataResult(string? Path, string Resolution, SongMetadata? Metadata);
    private async Task ReadSongMetadataAsync(LibrarySongItem song)
    {
        if (closed || song.MetadataLoaded || song.MetadataLoading || !song.IsAvailable) return;
        song.MetadataLoading = true;
        viewModel.BeginMetadataRead();
        bool entered = false;
        string? path = null;
        try
        {
            await metadataSlots.WaitAsync(metadataCancellation.Token);
            entered = true;
            if (!explicitMetadataRequests.Contains(song.Song.Id) && !IsSongVisible(song)) return;
            var snapshot = song.Song;
            // Windows property handlers, disk checks and parsing all begin on a worker thread.
            var result = await Task.Run(() => ReadFileMetadataAsync(snapshot, metadataCancellation.Token), metadataCancellation.Token);
            path = result.Path;
            if (closed || path is not null && !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            if (result.Resolution.Length > 0) song.Resolution = result.Resolution;
            if (path is not null && result.Metadata is { } metadata)
            {
                viewModel.ApplyLocalMetadata(song, path, metadata);
                song.MetadataLoaded = true;
            }
            else song.MetadataRetryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (Exception) { song.MetadataRetryAfter = DateTimeOffset.UtcNow.AddMinutes(5); }
        finally
        {
            if (entered) metadataSlots.Release();
            song.MetadataLoading = false;
            explicitMetadataRequests.Remove(song.Song.Id);
            viewModel.EndMetadataRead();
            if (!closed && path is not null && !song.MetadataLoaded && song.IsAvailable && !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                song.MetadataRetryAfter = default;
                DispatcherQueue.TryEnqueue(() => _ = LoadSongMetadataAsync(song));
            }
        }
    }
    private static async Task<FileMetadataResult> ReadFileMetadataAsync(Song snapshot, CancellationToken token)
    {
        string? path = snapshot.AvailablePath;
        if (path is null) return new(null, "", null);
        string resolution = MatroskaMetadata.TryReadDimensions(path)?.Label ?? "";
        try
        {
            var file = await ReadWindowsPropertyAsync(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
            if (resolution.Length == 0 && IsVideoFile(path))
            {
                try
                {
                    var video = await ReadWindowsPropertyAsync(file.Properties.GetVideoPropertiesAsync(), token).ConfigureAwait(false);
                    if (video.Width > 0 && video.Height > 0) resolution = $"{video.Height}p";
                }
                catch (Exception) when (!token.IsCancellationRequested) { /* Keep unknown resolution empty. */ }
            }
            var properties = await ReadWindowsPropertyAsync(file.Properties.RetrievePropertiesAsync(
                ["System.Title", "System.Music.Artist", "System.Music.AlbumTitle", "System.Media.Publisher",
                 "System.Media.Year", "System.Music.Lyrics", "System.Media.Duration"]), token).ConfigureAwait(false);
            string Text(string key) => (properties.TryGetValue(key, out var propertyValue) ? propertyValue : null) switch
            { string value => value.Trim(), string[] values => string.Join(" / ", values), uint year when year > 0 => year.ToString(), _ => "" };
            double duration = properties.TryGetValue("System.Media.Duration", out var durationValue) && durationValue is ulong ticks ? ticks / 10000000d : 0;
            return new(path, resolution, new(Text("System.Title"), Text("System.Music.Artist"), Text("System.Music.AlbumTitle"),
                Text("System.Media.Publisher"), Text("System.Media.Year"), Text("System.Music.Lyrics"), DurationSeconds: duration));
        }
        catch (Exception) when (!token.IsCancellationRequested) { return new(path, resolution, null); }
    }
    private static async Task<T> ReadWindowsPropertyAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await operation.AsTask(timeout.Token).WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
        finally { timeout.Cancel(); } // Cancel the WinRT request too, rather than only abandoning the await.
    }
    private static bool IsVideoFile(string path) => new[] { ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
