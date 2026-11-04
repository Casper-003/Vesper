using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using Vesper.Core;
using Windows.Foundation;
using Windows.System;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<QueueSongItem> queueRows = [];
    private QueueSongItem? currentQueueRow;
    private readonly Stack<QueueRemoval> queueUndo = [];
    private readonly DispatcherTimer queueUndoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer queueAdditionTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer queueAvailabilityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool queuePanelOpen, queueAvailabilityBusy, queueCloseApproved, queueClosing;
    private int queueAvailabilityRevision;
    private double queueScrollOffset;
    private readonly HashSet<TextBlock> queueScrollingTitles = [];

    private void InitializeQueuePresentation()
    {
        InitializeQueueRailPeek();
        QueueSongs.ItemsSource = queueRows;
        QueueSongs.ContainerContentChanging += OnQueueContainerChanging;
        viewModel.Queue.Pending.CollectionChanged += OnQueueCollectionChanged;
        viewModel.Queue.PropertyChanged += OnQueueStateChanged;
        viewModel.PropertyChanged += OnQueueLibraryChanged;
        queueUndoTimer.Tick += (_, _) => { queueUndoTimer.Stop(); queueUndo.Clear(); QueueUndoToast.Visibility = Visibility.Collapsed; };
        queueAdditionTimer.Tick += (_, _) => { queueAdditionTimer.Stop(); QueueAdditionToast.Visibility = Visibility.Collapsed; };
        queueAvailabilityTimer.Tick += async (_, _) => await CheckQueueAvailabilityAsync();
        InitializeQueueDrag();
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnQueueRootKeyDown), true);
        AppWindow.Closing += OnQueueWindowClosing;
        RefreshQueuePresentation();
    }

    private void OnQueueContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement row)
        {
            if (row.FindName("QueueTitleViewport") is Grid titleHost) ResetQueueTitle(titleHost);
            if (args.InRecycleQueue && row.DataContext is QueueSongItem recycled)
            {
                recycled.ShowActions(false); recycled.SetFocused(false);
                if (row is SongRowSurface surface) surface.SetHovered(false);
            }
            else
            {
                row.DataContext = args.Item;
                if (args.Item is QueueSongItem item) PrepareQueueRowSurface(row, item);
            }
        }
        else if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(1, OnQueueContainerChanging);
        if (args.ItemContainer is ListViewItem container) SyncQueueDragContainer(container);
    }

    private async void OnQueueWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        ResetQueueRailHint();
        if (queueCloseApproved) return;
        args.Cancel = true;
        if (queueClosing) return;
        queueClosing = true;
        Root.IsHitTestVisible = false;
        CloseLibraryEnrichment();
        await viewModel.FlushBackgroundMetadataAsync();
        bool saved = await viewModel.FlushQueueAsync();
        Root.IsHitTestVisible = true;
        queueClosing = false;
        if (saved) { queueCloseApproved = true; Close(); }
        else StartLibraryEnrichment();
    }

    private void CloseQueuePresentation()
    {
        CancelQueueDrag(false);
        StopQueueSidebarTransition();
        StopQueueLayoutTransition();
        queueChevronAnimation?.Stop();
        queueChevronAnimation = null;
        CloseQueueRailPeek();
        queueUndoTimer.Stop(); queueAdditionTimer.Stop(); queueAvailabilityTimer.Stop(); queueDragTimer.Stop();
        viewModel.Queue.Pending.CollectionChanged -= OnQueueCollectionChanged;
        viewModel.Queue.PropertyChanged -= OnQueueStateChanged;
        viewModel.PropertyChanged -= OnQueueLibraryChanged;
        AppWindow.Closing -= OnQueueWindowClosing;
        foreach (var row in queueRows) row.Dispose();
        currentQueueRow?.Dispose();
        queueScrollingTitles.Clear();
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { CancelQueueDrag(false); RefreshQueuePresentation(); }
    private void OnQueueStateChanged(object? sender, PropertyChangedEventArgs args)
    { CancelQueueDrag(false); RefreshQueuePresentation(); }
    private void OnQueueLibraryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.LibraryHero)) RefreshQueuePresentation();
        if (args.PropertyName == nameof(MainViewModel.Problem))
        {
            QueueSaveWarning.IsOpen = viewModel.Problem.StartsWith("待播队列保存失败", StringComparison.Ordinal);
            QueueSaveWarning.Message = viewModel.Problem;
        }
    }

    private void RefreshQueuePresentation()
    {
        if (closed) return;
        if (queueDragActive) CancelQueueDrag(false);
        queueAvailabilityRevision++;
        var pending = viewModel.Queue.Pending;
        var ids = pending.Select(entry => entry.Id).ToHashSet();
        for (int i = queueRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(queueRows[i].Entry.Id)) { queueRows[i].Dispose(); queueRows.RemoveAt(i); }
        for (int i = 0; i < pending.Count; i++)
        {
            var item = queueRows.FirstOrDefault(row => row.Entry.Id == pending[i].Id);
            if (item is null) { item = new(pending[i]); queueRows.Insert(i, item); }
            else if (queueRows.IndexOf(item) != i) queueRows.Move(queueRows.IndexOf(item), i);
            item.Update(pending[i], viewModel.FindSong(pending[i].SongId), i + 1);
        }
        if (viewModel.Queue.Current is { } current)
        {
            if (currentQueueRow?.Entry.Id != current.Id) { currentQueueRow?.Dispose(); currentQueueRow = new(current); QueueCurrentContent.Content = currentQueueRow; }
            currentQueueRow.Update(current, viewModel.FindSong(current.SongId), 0);
        }
        else { currentQueueRow?.Dispose(); currentQueueRow = null; QueueCurrentContent.Content = null; }
        QueueCurrentArea.Visibility = currentQueueRow is null ? Visibility.Collapsed : Visibility.Visible;
        QueueCurrentState.Text = viewModel.Queue.State switch { PlaybackState.Playing => "播放中", PlaybackState.Paused => "已暂停", PlaybackState.Loading => "正在准备", _ => "" };
        QueueHeadingCount.Text = pending.Count.ToString(); QueueBadge.Text = pending.Count.ToString();
        UpdateQueueToggle();
        QueueEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueEmptyTitle.Text = currentQueueRow is null ? "暂无待播" : "暂无后续歌曲";
        if (queuePanelOpen) _ = CheckQueueAvailabilityAsync();
        ScheduleQueueRowSurfaceRefresh();
    }

    private async Task CheckQueueAvailabilityAsync()
    {
        if (closed || queueAvailabilityBusy) return;
        queueAvailabilityBusy = true;
        int revision = queueAvailabilityRevision;
        var snapshot = queueRows.Concat(currentQueueRow is null ? [] : new[] { currentQueueRow })
            .Select(row => (Row: row, Sources: row.Song?.Song.Sources.ToArray() ?? [])).ToArray();
        try
        {
            bool[] available = await Task.Run(() => snapshot.Select(item => item.Sources.Any(File.Exists)).ToArray());
            if (closed || revision != queueAvailabilityRevision) return;
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Row.SetAvailable(available[i]);
        }
        finally { queueAvailabilityBusy = false; }
    }

    private void OnQueueToggle(object sender, RoutedEventArgs args) => SetQueueOpen(!queuePanelOpen);
    private void OnQueueRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        if (CancelQueueDrag()) { args.Handled = true; return; }
        if (queuePanelOpen) { SetQueueOpen(false); args.Handled = true; }
    }
    private double QueueCardGutter => QueueShell.Margin.Left + QueueShell.Margin.Right;
    private double QueueSidebarWidth => QueueRailHost.Width + QueueShellChrome.BorderThickness.Left + QueueShellChrome.BorderThickness.Right;
    private double QueueCardWidth => Math.Clamp(BrowserCards.ActualWidth - QueueSidebarWidth - QueueRailHost.Width - 320 - QueueCardGutter, 230, 400) + QueueRailHost.Width;

    private void UpdateQueueToggle()
    {
        AnimateQueueChevron();
        QueueBadge.Visibility = queuePanelOpen ? Visibility.Collapsed : Visibility.Visible;
        string action = queuePanelOpen ? "收起待播曲目" : "展开待播曲目";
        ToolTipService.SetToolTip(QueueOpenButton, $"{action} · {viewModel.Queue.Pending.Count} 首");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QueueOpenButton, action);
    }

    private async void OnQueueRowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } element) return;
        PrepareQueueRowSurface(element, row);
        if (row.Song is { } song) await LoadCoverAsync(song);
    }
    private void OnQueueRowEntered(object sender, PointerRoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) row.ShowActions(true); }
    private void OnQueueRowExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } element) return;
        var point = args.GetCurrentPoint(element).Position;
        if (point.X < 0 || point.Y < 0 || point.X > element.ActualWidth || point.Y > element.ActualHeight) row.ShowActions(false);
    }
    private void OnQueueRowFocused(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) row.SetFocused(true); }
    private void OnQueueRowUnfocused(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } element) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            bool inside = false;
            for (DependencyObject? focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; focus is not null; focus = VisualTreeHelper.GetParent(focus))
                if (ReferenceEquals(focus, element)) { inside = true; break; }
            row.SetFocused(inside);
        });
    }
    private void OnQueueOpenDetails(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) OpenQueuedDetails(row); }
    private void OnQueueTitleTapped(object sender, TappedRoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) OpenQueuedDetails(row); args.Handled = true; }
    private void OpenQueuedDetails(QueueSongItem row)
    {
        if (row.Song is null) return;
        SetQueueOpen(false);
        NavigateShell(new ShellNavigationRequest("歌曲详情:" + row.Song.Song.Id, this, row.Song.Song.Id));
    }
    private void OnQueueRemove(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) RemoveQueued(row); }
    private void RemoveQueued(QueueSongItem row)
    {
        if (viewModel.Queue.RemovePending(row.Entry.Id) is not { } removal) return;
        queueUndo.Push(removal); QueueUndoToast.Visibility = Visibility.Visible;
        queueUndoTimer.Stop(); queueUndoTimer.Start();
    }
    private void OnQueueUndo(object sender, RoutedEventArgs args)
    {
        if (queueUndo.TryPop(out var removal))
        {
            string songId = viewModel.FindSong(removal.Entry.SongId)?.Song.Id ?? removal.Entry.SongId;
            viewModel.Queue.RestorePending(removal with { Entry = removal.Entry with { SongId = songId } });
        }
        queueUndoTimer.Stop();
        if (queueUndo.Count == 0) QueueUndoToast.Visibility = Visibility.Collapsed; else queueUndoTimer.Start();
    }
    private void OnQueueContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem { IsPending: true } row } element) return;
        var menu = new MenuFlyout();
        var insert = new MenuFlyoutItem { Text = "插播", Icon = new FontIcon { Glyph = "\uE8EF" }, IsEnabled = row.IsAvailable && row.Position > 1 };
        insert.Click += (_, _) => viewModel.Queue.InsertNext(row.Entry.Id);
        var remove = new MenuFlyoutItem { Text = "移除", Icon = new FontIcon { Glyph = "\uE738" } };
        remove.Click += (_, _) => RemoveQueued(row); menu.Items.Add(insert); menu.Items.Add(remove);
        if (args.TryGetPosition(element, out var point)) menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
        args.Handled = true;
    }

    private void OnQueueTitleEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Grid host) StartQueueTitleMarquee(host);
    }
    private void StartQueueTitleMarquee(Grid host)
    {
        if (host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) return;
        var measure = new TextBlock { Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = measure.DesiredSize.Width - host.ActualWidth;
        if (overflow <= 1) return;
        host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, host.ActualWidth, host.ActualHeight) };
        title.Width = measure.DesiredSize.Width; title.TextTrimming = TextTrimming.None;
        ElementCompositionPreview.SetIsTranslationEnabled(title, true);
        var visual = ElementCompositionPreview.GetElementVisual(title);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0); animation.InsertKeyFrame(.15f, 0); animation.InsertKeyFrame(.85f, (float)-overflow); animation.InsertKeyFrame(1, (float)-overflow);
        animation.Duration = TimeSpan.FromSeconds(Math.Max(3, overflow / 30 + 1.5)); animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.X", animation);
        queueScrollingTitles.Add(title);
    }
    private void OnQueueTitleExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Grid host) ResetQueueTitle(host);
    }
    private void ResetQueueTitle(Grid host)
    {
        if (host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) return;
        // Translation is an opt-in property, not a native Visual property. A newly
        // realized row has no title animation to stop; stopping that path can fail fast.
        if (queueScrollingTitles.Remove(title) && !closed)
        {
            var visual = ElementCompositionPreview.GetElementVisual(title);
            visual.StopAnimation("Translation.X"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        title.ClearValue(FrameworkElement.WidthProperty); title.TextTrimming = TextTrimming.CharacterEllipsis; host.Clip = null;
    }

    private void OnQueueRowUnloaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement row && row.FindName("QueueTitleViewport") is Grid host) ResetQueueTitle(host); }

    private void ShowQueueAdded(LibrarySongItem song, FrameworkElement? origin)
    {
        QueueAdditionMessage.Text = $"已加入待播 · 第 {viewModel.Queue.Pending.Count} 位 · {song.Title}";
        QueueAdditionToast.Visibility = Visibility.Visible; queueAdditionTimer.Stop(); queueAdditionTimer.Start();
        if (!queueSidebarVisible || queueSidebarAnimating || Root.XamlRoot is null || QueueOpenButton.ActualWidth <= 0) return;
        Point start = (origin ?? PageArea).TransformToVisual(Root).TransformPoint(new Point((origin ?? PageArea).ActualWidth / 2, (origin ?? PageArea).ActualHeight / 2));
        Point end = QueueToggleContent.TransformToVisual(Root).TransformPoint(new Point(QueueToggleContent.ActualWidth / 2, QueueToggleContent.ActualHeight / 2));
        var note = new TextBlock { Text = "♪", FontSize = 24, Foreground = (Brush)Navigation.Resources["NavigationViewSelectionIndicatorForeground"] };
        Canvas.SetLeft(note, start.X); Canvas.SetTop(note, start.Y); QueueFlightLayer.Children.Add(note);
        ElementCompositionPreview.SetIsTranslationEnabled(note, true);
        var visual = ElementCompositionPreview.GetElementVisual(note); var compositor = visual.Compositor;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var flight = compositor.CreateVector3KeyFrameAnimation();
        flight.InsertKeyFrame(0, Vector3.Zero); flight.InsertKeyFrame(.5f, new((float)(end.X - start.X) / 2, (float)(end.Y - start.Y) / 2 - 32, 0)); flight.InsertKeyFrame(1, new((float)(end.X - start.X), (float)(end.Y - start.Y), 0)); flight.Duration = TimeSpan.FromMilliseconds(360);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation", flight);
        batch.Completed += (_, _) =>
        {
            QueueFlightLayer.Children.Remove(note);
            if (closed) return;
            var receive = compositor.CreateVector3KeyFrameAnimation(); receive.Target = "Scale";
            receive.InsertKeyFrame(0, Vector3.One); receive.InsertKeyFrame(.5f, new(1.15f, 1.15f, 1)); receive.InsertKeyFrame(1, Vector3.One); receive.Duration = TimeSpan.FromMilliseconds(180);
            QueueToggleContent.StartAnimation(receive);
        };
        batch.End();
    }
}
