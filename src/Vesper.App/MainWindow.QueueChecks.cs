using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Opt-in regression for a native fail-fast. It never initializes or writes the user's library.
    private async Task RunQueueRuntimeChecksAsync(string outputPath)
    {
        var failures = new List<string>();
        int transitions = 0, idleTitleResets = 0, marqueeResets = 0, dragPreviewChecks = 0;
        int browserLayouts = 0, maxLayoutsPerTransition = 0;
        var contentChecks = new List<object>();
        BrowserCard.SizeChanged += (_, _) => browserLayouts++;
        string probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $"queue-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            await File.WriteAllTextAsync(probe, "isolated queue source");
            for (int i = 0; i < 9; i++)
                viewModel.Queue.Add(new($"queue-check-{i}", i % 2 == 0 ? "普通标题" : string.Concat(Enumerable.Repeat("需要滚动展示的超长队列标题 ", 12)), [probe]));
            await Task.Delay(150);
            Grid? publicContent = null;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(BrowserCards); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Grid { Name: "ContentGrid" } grid) { publicContent = grid; break; }
            if (publicContent is null || !publicContent.BorderThickness.Equals(new Thickness(0))
                || publicContent.BorderBrush is SolidColorBrush { Color.A: > 0 } || publicContent.Background is SolidColorBrush { Color.A: > 0 })
                failures.Add("The navigation content parent must not draw a shared outline across both cards.");
            for (int cycle = 0; cycle < 8; cycle++)
            {
                if (cycle == 0)
                {
                    if (SingingToggleChevron.Opacity != 0 || QueueToggleChevron.Opacity != 0
                        || SingingCardHost.Visibility != Visibility.Collapsed || QueueCardHost.Visibility != Visibility.Collapsed)
                        failures.Add("Collapsed strips must stay blank when the pointer is not hovering over them.");
                    OnQueueRailEntered(QueueOpenButton, null!);
                    AnimateQueueRailContent(.92f);
                    await Task.Delay(160);
                    Root.UpdateLayout();
                    Point collapsedArrow = QueueToggleChevron.TransformToVisual(QueueShell).TransformPoint(new Point(QueueToggleChevron.ActualWidth / 2, QueueToggleChevron.ActualHeight / 2));
                    if (!QueueRailPreviewShown || QueueToggleChevron.Opacity != 1
                        || QueueCardHost.Visibility != Visibility.Visible || Math.Abs(QueueCardHost.ActualWidth - QueueRailHost.Width) > .5
                        || Math.Abs(QueueCardHost.Opacity - .5) > .05
                        || Math.Abs(collapsedArrow.Y - QueueShell.ActualHeight / 2) > .5)
                        failures.Add("Hovering a collapsed strip must fade in the behind content and vertically centered '<' chevron.");
                    OnQueueRailExited(QueueOpenButton, null!);
                }
                browserLayouts = 0;
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                if (cycle == 0)
                {
                    await Task.Delay(80);
                    if (QueueCardHost.ActualWidth <= QueueRailHost.Width || QueueCardHost.ActualWidth >= QueueCard.Width)
                        failures.Add("Queue reveal must pass through animated intermediate widths.");
                    await Task.Delay(220);
                }
                else await Task.Delay(300);
                Root.UpdateLayout();
                maxLayoutsPerTransition = Math.Max(maxLayoutsPerTransition, browserLayouts);
                if (!singingPanelOpen || !queuePanelOpen || SingingCardHost.ActualWidth < 100 || QueueCardHost.ActualWidth < 100)
                    failures.Add($"cycle {cycle}: right panels did not open together in linked mode.");
                contentChecks.Add(await CheckQueueVisibleContentAsync(failures, cycle, cycle is 0 or 4,
                    cycle == 0 ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "queue-visible-content.png") : null));
                if (SingingShell.Margin.Left != 4 || QueueShell.Margin.Left != 4 || QueueShell.Margin.Right != 0 || QueueShell.Margin.Top != 0 || QueueShell.Margin.Bottom != 0)
                    failures.Add("Right sidebar card spacing differs from the shared four-unit edge.");
                Point singingOrigin = SingingShell.TransformToVisual(BrowserCards).TransformPoint(default);
                Point queueOrigin = QueueShell.TransformToVisual(BrowserCards).TransformPoint(default);
                Point browserOrigin = BrowserCard.TransformToVisual(BrowserCards).TransformPoint(default);
                double verticalGap = queueOrigin.Y - (singingOrigin.Y + SingingShell.ActualHeight);
                if (Math.Abs(singingOrigin.Y - browserOrigin.Y) > .5
                    || Math.Abs(verticalGap - 4) > .6
                    || Math.Abs(SingingShell.ActualHeight + 4 + QueueShell.ActualHeight - BrowserCard.ActualHeight) > .6
                    || Math.Abs(singingOrigin.X - browserOrigin.X - BrowserCard.ActualWidth - 4) > .5
                    || Math.Abs(queueOrigin.X - browserOrigin.X - BrowserCard.ActualWidth - 4) > .5)
                    failures.Add("The visible singing, queue and browser card edges are not aligned at a four-unit gap.");
                if (!ReferenceEquals(BrowserCardChrome.Style, SingingShellChrome.Style) || !ReferenceEquals(BrowserCardChrome.Style, QueueShellChrome.Style)
                    || SingingShellChrome.Shadow is not null || QueueShellChrome.Shadow is not null || QueueCard.Shadow is not null
                    || !BrowserCardChrome.BorderThickness.Equals(new Thickness(1)) || !BrowserCardChrome.BorderThickness.Equals(QueueShellChrome.BorderThickness))
                    failures.Add("Each card must independently draw the same one-unit light outline.");
                if (SingingRailHost.Visibility != Visibility.Collapsed || QueueRailHost.Visibility != Visibility.Collapsed
                    || SingingCollapseButton.Opacity != 0 || QueueCollapseButton.Opacity != 0)
                    failures.Add("Expanded panels must remove the left rail reserve and hide top-left collapse buttons when not hovered.");
                OnSingingShellEntered(SingingShell, null!);
                OnQueueShellEntered(QueueShell, null!);
                if (SingingCollapseButton.Opacity != 1 || QueueCollapseButton.Opacity != 1)
                    failures.Add("Hovering an expanded card must fade in its top-left collapse button.");
                OnSingingShellExited(SingingShell, null!);
                OnQueueShellExited(QueueShell, null!);
                var headingTitle = QueueHeading.Children.OfType<TextBlock>().First(tb => tb.Text == "待播曲目");
                Point titleCenter = headingTitle.TransformToVisual(QueueHeading).TransformPoint(new Point(headingTitle.ActualWidth / 2, headingTitle.ActualHeight / 2));
                if (Math.Abs(titleCenter.X - QueueHeading.ActualWidth / 2) > 1
                    || SingingPreviewStage.ActualWidth <= 100 || SingingStagePlayButton.ActualWidth != 56
                    || QueueSongs.Padding.Right != 12 || QueueHeading.Margin.Right != 12
                    || LibraryScrollRail.Width != 16 || FavoriteScrollRail.Width != 16)
                    failures.Add("Expanded cards must have centered queue heading, normal 12-unit side padding, singing preview stage and separate scrollbar space.");
                if (QueueSongs.ItemsPanelRoot is not ItemsStackPanel panel) throw new InvalidOperationException("Queue list did not realize its virtualizing panel.");
                foreach (var container in panel.Children.OfType<ListViewItem>())
                {
                    if (container.ContentTemplateRoot is not FrameworkElement row || row.FindName("QueueTitleViewport") is not Grid host
                        || host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
                    if (row.FindName("QueueDragHandle") is not ContentControl { IsTabStop: true }) failures.Add("Drag handle must remain focusable.");
                    if (row is not SongRowSurface surface || row.DataContext is not QueueSongItem model
                        || surface.RowIndex != model.Position - 1 || !ReferenceEquals(surface.HoverGroup, queueHoverGroup))
                        failures.Add("A reused queue row lost its stripe index or separate hover group.");
                    ResetQueueTitle(host); ResetQueueTitle(host); idleTitleResets += 2;
                    title.Text = string.Concat(Enumerable.Repeat("超长队列标题复现 ", 14));
                    title.UpdateLayout();
                    StartQueueTitleMarquee(host);
                    if (!queueScrollingTitles.Contains(title)) failures.Add($"cycle {cycle}: long title animation was not started.");
                    ResetQueueTitle(host); ResetQueueTitle(host); marqueeResets++;
                }
                if (cycle < 3) dragPreviewChecks += await CheckQueueDragPreviewAsync(failures, cycle);
                browserLayouts = 0;
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                await Task.Delay(280);
                OnSingingRailExited(SingingOpenButton, null!);
                OnQueueRailExited(QueueOpenButton, null!);
                await Task.Delay(220);
                Root.UpdateLayout();
                maxLayoutsPerTransition = Math.Max(maxLayoutsPerTransition, browserLayouts);
                if (singingPanelOpen || queuePanelOpen || SingingCardHost.Visibility != Visibility.Collapsed || QueueCardHost.Visibility != Visibility.Collapsed
                    || SingingRailHost.Visibility != Visibility.Visible || QueueRailHost.Visibility != Visibility.Visible
                    || SingingToggleChevron.Opacity != 0 || QueueToggleChevron.Opacity != 0
                    || Math.Abs(SingingShell.ActualWidth - QueueSidebarWidth) > .5 || Math.Abs(QueueShell.ActualWidth - QueueSidebarWidth) > .5)
                    failures.Add($"cycle {cycle}: queue close state open={queuePanelOpen}, host={QueueCardHost.Visibility}/{QueueCardHost.ActualWidth:F2}, chevronOpacity={QueueToggleChevron.Opacity:F2}, shell={QueueShell.ActualWidth:F2}/{QueueSidebarWidth:F2}, animating={queueLayoutAnimating}.");
                if (cycle == 3)
                {
                    viewModel.Queue.StartNext([new("queue-check-0", "当前歌曲", [probe])], out _);
                    viewModel.Queue.MarkPaused();
                }
            }
            // Interrupt both animation directions, then settle on a fully closed card.
            SetQueueOpen(true); transitions++; await Task.Delay(35);
            SetQueueOpen(false); transitions++; await Task.Delay(35);
            SetQueueOpen(true); transitions++; await Task.Delay(280);
            SetQueueOpen(false); transitions++; await Task.Delay(280);
            if (queuePanelOpen || QueueCardHost.Visibility != Visibility.Collapsed) failures.Add("Interrupted transitions did not settle closed.");
            SetQueueOpen(true); transitions++; await Task.Delay(280);
            int pendingBeforeAdd = viewModel.Queue.Pending.Count;
            viewModel.Queue.Add(new("added-while-open", "展开队列时继续加入", [probe]));
            if (!queuePanelOpen || viewModel.Queue.Pending.Count != pendingBeforeAdd + 1
                || QueueHeadingCount.Text != viewModel.Queue.Pending.Count.ToString() || !BrowserCard.IsHitTestVisible || BrowserCards.Children.Count != 2)
                failures.Add("Adding a song must keep the queue open and the browser accessible.");
            await CheckQueueRemoveClickAsync(failures);
            SetQueueOpen(false); transitions++; await Task.Delay(280);
            double browserClosedWidth = BrowserCard.ActualWidth;
            double singingBeforeSplit = SingingShell.ActualHeight;
            SetRightSplitRatio(0.52, save: false); Root.UpdateLayout();
            if (Math.Abs(SingingShell.ActualHeight / Math.Max(1, QueueSidebarSlot.ActualHeight - 4) - 0.52) > .03
                || SingingShell.ActualHeight <= singingBeforeSplit)
                failures.Add("Dragging the vertical split ratio must resize the upper and lower right panels.");
            SetRightSplitRatio(0.382, save: false); Root.UpdateLayout();
            LinkRightPanelsSwitch.IsOn = false;
            OnSingingRailToggle(SingingOpenButton, new RoutedEventArgs()); await Task.Delay(280); Root.UpdateLayout();
            if (!singingPanelOpen || queuePanelOpen || Math.Abs(BrowserCard.ActualWidth - browserClosedWidth) > .5
                || SingingCollapseButton.CornerRadius.TopLeft > 6)
                failures.Add("In unlinked mode, opening only the singing panel must overlay without squeezing the browser card and use a square collapse button.");
            OnSingingRailToggle(SingingCollapseButton, new RoutedEventArgs()); await Task.Delay(80); Root.UpdateLayout();
            if (SingingShellChrome.Background is not AcrylicBrush || Math.Abs(SingingCardHost.Opacity - 1) > .01)
                failures.Add("Collapsing a single overlay panel must retain its frosted material and full content opacity during the close transition.");
            await Task.Delay(220); Root.UpdateLayout();
            OnSingingRailToggle(SingingOpenButton, new RoutedEventArgs()); await Task.Delay(280); Root.UpdateLayout();
            OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); await Task.Delay(280); Root.UpdateLayout();
            if (!singingPanelOpen || !queuePanelOpen || BrowserCard.ActualWidth >= browserClosedWidth - 100)
                failures.Add("In unlinked mode, opening both panels must squeeze the browser card.");
            CloseRightPanels(); await Task.Delay(280); Root.UpdateLayout();
            LinkRightPanelsSwitch.IsOn = true;
            if (idleTitleResets == 0 || marqueeResets == 0) failures.Add("No realized titles exercised the crash path.");
            if (viewModel.Queue.State != PlaybackState.Paused || viewModel.Queue.Current is null) failures.Add("Queue toggling changed the current song or paused state.");
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0, Transitions = transitions, IdleTitleResets = idleTitleResets,
                    MarqueeResets = marqueeResets, DragPreviewChecks = dragPreviewChecks, MaxBrowserLayoutsPerTransition = maxLayoutsPerTransition,
                    ContentChecks = contentChecks, Failures = failures,
                    Limitations = "Isolated synthetic controls, actual layout/hit testing and rendered heading/row pixel differences; no user library, physical pointer gesture, measured FPS or visual acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (File.Exists(probe)) File.Delete(probe); Close(); }
        }
    }

    private async Task CheckQueueRemoveClickAsync(List<string> failures)
    {
        QueueSongs.UpdateLayout();
        if (QueueSongs.ItemsPanelRoot is not ItemsStackPanel panel) return;
        var container = panel.Children.OfType<ListViewItem>().FirstOrDefault(item => item.Content is QueueSongItem);
        if (container?.ContentTemplateRoot is not FrameworkElement { DataContext: QueueSongItem row } element
            || element.FindName("QueueDragHandle") is not FrameworkElement handle
            || VisualTreeHelper.GetParent(handle) is not StackPanel actions
            || actions.Children.OfType<Button>().FirstOrDefault() is not { } remove)
        { failures.Add("No real remove button found for click regression."); return; }
        row.ShowActions(true); Root.UpdateLayout();
        int before = viewModel.Queue.Pending.Count;
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(remove);
        var provider = peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
            as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider;
        if (provider is null) { failures.Add("Remove button has no native Invoke pattern."); return; }
        provider.Invoke(); await Task.Delay(40);
        if (viewModel.Queue.Pending.Count != before - 1 || viewModel.Queue.Pending.Any(entry => entry.Id == row.Entry.Id))
            failures.Add("The real remove button Click must change the pending queue.");
        else OnQueueUndo(QueueUndoToast, new RoutedEventArgs());
    }

    private async Task<object> CheckQueueVisibleContentAsync(List<string> failures, int cycle, bool capturePixels, string? pngPath)
    {
        double hostHeight = QueueCardHost.ActualHeight, cardHeight = QueueCard.ActualHeight;
        double headingHeight = QueueHeading.ActualHeight, listHeight = QueueSongs.ActualHeight;
        if (hostHeight < 100 || Math.Abs(QueueCard.Height - hostHeight) > .5 || Math.Abs(cardHeight - hostHeight) > .5 || headingHeight <= 0 || listHeight <= 72)
            failures.Add($"cycle {cycle}: visible card geometry host={hostHeight:F2}, card={cardHeight:F2}, heading={headingHeight:F2}, list={listHeight:F2}.");

        var panel = QueueSongs.ItemsPanelRoot as ItemsStackPanel;
        ListViewItem? first = null;
        if (panel is not null)
        {
            for (int index = Math.Max(0, panel.FirstVisibleIndex); index <= panel.LastVisibleIndex; index++)
                if (QueueSongs.ContainerFromIndex(index) is ListViewItem candidate
                    && candidate.ContentTemplateRoot is FrameworkElement candidateRow
                    && candidateRow.FindName("QueueDragHandle") is FrameworkElement candidateHandle
                    && candidateHandle.ActualHeight > 0)
                {
                    Point inList = candidateHandle.TransformToVisual(QueueSongs).TransformPoint(new Point(candidateHandle.ActualWidth / 2, candidateHandle.ActualHeight / 2));
                    if (inList.Y >= 4 && inList.Y <= QueueSongs.ActualHeight - 4) { first = candidate; break; }
                }
            first ??= QueueSongs.ContainerFromIndex(Math.Max(0, panel.FirstVisibleIndex)) as ListViewItem;
        }
        var row = first?.ContentTemplateRoot as FrameworkElement;
        var handle = row?.FindName("QueueDragHandle") as FrameworkElement;
        bool inClip = false, hit = false;
        string? topHitName = null;
        if (handle is not null && handle.ActualWidth > 0 && handle.ActualHeight > 0)
        {
            var center = new Point(handle.ActualWidth / 2, handle.ActualHeight / 2);
            Point hostPoint = handle.TransformToVisual(QueueCardHost).TransformPoint(center);
            Point windowPoint = handle.TransformToVisual(null).TransformPoint(center);
            inClip = QueueCardHost.Clip?.Rect.Contains(hostPoint) == true;
            var topHit = VisualTreeHelper.FindElementsInHostCoordinates(windowPoint, Root).FirstOrDefault();
            topHitName = (topHit as FrameworkElement)?.Name ?? topHit?.GetType().Name;
            for (DependencyObject? element = topHit; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, handle)) { hit = true; break; }
        }
        if (!inClip || !hit) failures.Add($"cycle {cycle}: visible row handle must be inside the host clip and topmost hit-testable (clip={inClip}, hit={hit}, top={topHitName}).");

        int headingPixels = -1, rowPixels = -1;
        if (capturePixels && row is not null)
        {
            var visible = await CaptureQueueRootAsync();
            if (pngPath is not null) await WriteQueueCheckPngAsync(pngPath, visible.Width, visible.Height, visible.Pixels);
            // A transparent card reveals the same chrome underneath. Comparing captures
            // verifies real painted heading and row content after all ancestor clips.
            QueueCard.Opacity = 0;
            (int Width, int Height, byte[] Pixels) hidden;
            try { hidden = await CaptureQueueRootAsync(); }
            finally { QueueCard.Opacity = 1; }
            if (visible.Width == hidden.Width && visible.Height == hidden.Height)
            {
                headingPixels = CountQueueChangedPixels(visible, hidden.Pixels, QueueHeading);
                rowPixels = CountQueueChangedPixels(visible, hidden.Pixels, row);
            }
            if (headingPixels < 8 || rowPixels < 8)
                failures.Add($"cycle {cycle}: heading/row produced no visible content through the host clip (pixels={headingPixels}/{rowPixels}).");
        }
        return new { Cycle = cycle, HostHeight = hostHeight, CardHeight = cardHeight, HeadingHeight = headingHeight,
            ListHeight = listHeight, HandleInsideClip = inClip, HandleHit = hit, TopHit = topHitName, HeadingChangedPixels = headingPixels, RowChangedPixels = rowPixels };
    }

    private async Task<(int Width, int Height, byte[] Pixels)> CaptureQueueRootAsync()
    {
        var bitmap = new RenderTargetBitmap();
        // RenderTargetBitmap omits the native Mica backdrop. Supply an opaque
        // test-only base so translucent cards and white text remain readable in PNGs.
        var previousBackground = Root.Background;
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        try { await bitmap.RenderAsync(Root); }
        finally { Root.Background = previousBackground; }
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[checked((int)buffer.Length)];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || pixels.Length == 0)
            throw new InvalidOperationException("Queue content capture returned no pixels.");
        return (bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private int CountQueueChangedPixels((int Width, int Height, byte[] Pixels) visible, byte[] hidden, FrameworkElement region)
    {
        Rect bounds = region.TransformToVisual(Root).TransformBounds(new Rect(0, 0, region.ActualWidth, region.ActualHeight));
        double sx = visible.Width / Root.ActualWidth, sy = visible.Height / Root.ActualHeight;
        int left = Math.Clamp((int)Math.Floor(bounds.Left * sx), 0, visible.Width);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right * sx), 0, visible.Width);
        int top = Math.Clamp((int)Math.Floor(bounds.Top * sy), 0, visible.Height);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * sy), 0, visible.Height);
        int changed = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int offset = (y * visible.Width + x) * 4;
                if (Math.Abs(visible.Pixels[offset] - hidden[offset]) > 8
                    || Math.Abs(visible.Pixels[offset + 1] - hidden[offset + 1]) > 8
                    || Math.Abs(visible.Pixels[offset + 2] - hidden[offset + 2]) > 8) changed++;
            }
        return changed;
    }

    private static async Task WriteQueueCheckPngAsync(string path, int width, int height, byte[] pixels)
    {
        await File.WriteAllBytesAsync(path, []);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private async Task<int> CheckQueueDragPreviewAsync(List<string> failures, int cycle)
    {
        var original = viewModel.Queue.Pending.Select(entry => entry.Id).ToArray();
        QueueSongItem item = queueRows[0];
        if (QueueSongs.ContainerFromItem(item) is not ListViewItem container) throw new InvalidOperationException("No realized source row.");
        Point point = container.TransformToVisual(Root).TransformPoint(new Point(container.ActualWidth - 12, container.ActualHeight / 2));
        if (!StartQueueDragPreview(item, point)) throw new InvalidOperationException("Whole-row preview could not start.");
        Root.UpdateLayout();
        if (queueDragFloating is null || Math.Abs(queueDragFloating.ActualWidth - container.ActualWidth) > 1 || container.Opacity != 0)
            failures.Add("Drag must float the whole row and leave a hidden source placeholder.");
        int lastRealized = Math.Max(2, (QueueSongs.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex ?? (queueRows.Count - 1));
        MoveQueueDragPreview(Math.Min(queueRows.Count - 1, lastRealized)); await Task.Delay(30);
        MoveQueueDragPreview(1); Root.UpdateLayout();
        if (!viewModel.Queue.Pending.Select(entry => entry.Id).SequenceEqual(original) || queueRows[1].Entry.Id != item.Entry.Id
            || queueRows.Where((row, index) => row.Position != index + 1).Any())
            failures.Add("Preview must move projected rows and numbers without changing the saved queue.");
        if (cycle == 0)
        {
            CancelQueueDrag();
            if (!queueRows.Select(row => row.Entry.Id).SequenceEqual(original) || queueDragFloating is not null)
                failures.Add("Cancel must restore the original order and remove the floating row.");
        }
        else if (cycle == 1)
        {
            int changes = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => changes++;
            viewModel.Queue.Pending.CollectionChanged += changed;
            try { FinishQueueDrag(true); }
            finally { viewModel.Queue.Pending.CollectionChanged -= changed; }
            if (changes != 1 || viewModel.Queue.Pending[1].Id != item.Entry.Id || queueDragFloating is not null)
                failures.Add("Release must commit one final move for the selected entry.");
        }
        else
        {
            // A concurrent queue mutation must cancel preview without losing the new entry.
            viewModel.Queue.Add(new("queue-concurrent", "拖动时新增", []));
            if (queueDragActive || queueDragFloating is not null || viewModel.Queue.Pending[^1].SongId != "queue-concurrent"
                || !queueRows.Select(row => row.Entry.Id).SequenceEqual(viewModel.Queue.Pending.Select(entry => entry.Id)))
                failures.Add("Concurrent changes must cancel safely and retain the actual queue.");
        }
        await Task.Delay(30); Root.UpdateLayout();
        if (RealizedQueueDragContainers().Any(row => row.Opacity != 1)) failures.Add("Recycled rows retained a hidden drag placeholder.");
        return 1;
    }
}
