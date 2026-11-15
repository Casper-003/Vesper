using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer queueDragTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<Guid, QueueDragMotion> queueDragMotions = [];
    private readonly Dictionary<ListViewItem, Guid> queueDragContainers = [];
    private Guid? queueDragId;
    private Pointer? queueDragPointer;
    private Point queueDragPressPoint, queueDragLastPoint, queueDragGrabOffset;
    private ContentControl? queueDragFloating;
    private ScrollViewer? queueDragViewer;
    private bool queueDragActive;
    private double queueDragScroll;
    private long queueDragScrollTime;
    private const double QueueDragMotionMilliseconds = 140;
    private readonly record struct QueueDragMotion(double From, long Started);

    private void InitializeQueueDrag() => queueDragTimer.Tick += OnQueueDragScrollTick;

    private void OnQueueHandlePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (queueDragPointer is not null || queueDragActive
            || sender is not ContentControl { DataContext: QueueSongItem { IsPending: true } row } handle) return;
        var point = args.GetCurrentPoint(QueueSongs);
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed
            || !(point.Properties.IsLeftButtonPressed || point.IsInContact)) return;
        handle.Focus(FocusState.Pointer);
        args.Handled = true;
        if (!QueueSongs.CapturePointer(args.Pointer)) return;
        queueDragPointer = args.Pointer;
        queueDragId = row.Entry.Id;
        queueDragPressPoint = queueDragLastPoint = point.Position;
    }

    private void OnQueueDragPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (queueDragPointer?.PointerId != args.Pointer.PointerId) return;
        Point point = args.GetCurrentPoint(QueueSongs).Position;
        queueDragLastPoint = point;
        args.Handled = true;
        if (!queueDragActive)
        {
            double dx = queueDragLastPoint.X - queueDragPressPoint.X;
            double dy = queueDragLastPoint.Y - queueDragPressPoint.Y;
            if (dx * dx + dy * dy < 16) return;
            var row = queueRows.FirstOrDefault(item => item.Entry.Id == queueDragId);
            Point origin = QueueSongs.TransformToVisual(Root).TransformPoint(queueDragPressPoint);
            if (row is null || !StartQueueDragPreview(row, origin)) { CancelQueueDrag(); return; }
        }
        UpdateQueueDragPointer(point);
    }

    private void OnQueueDragPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (queueDragPointer?.PointerId != args.Pointer.PointerId) return;
        Point point = args.GetCurrentPoint(QueueSongs).Position;
        args.Handled = true;
        if (queueDragActive) UpdateQueueDragPointer(point);
        FinishQueueDrag(IsInsideQueueDragViewport(point));
    }

    private void OnQueueDragPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (queueDragPointer?.PointerId != args.Pointer.PointerId) return;
        args.Handled = true;
        CancelQueueDrag();
    }

    private void OnQueueDragPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (queueDragPointer?.PointerId == args.Pointer.PointerId) CancelQueueDrag();
    }

    // The list projection is the preview. Core.Pending is changed only once on release.
    // rootPoint is in Root coordinates so the same path can be checked without synthetic pointer input.
    private bool StartQueueDragPreview(QueueSongItem row, Point rootPoint)
    {
        if (closed || queueDragActive || !row.IsPending || !queueRows.Contains(row)
            || QueueSongs.ContainerFromItem(row) is not ListViewItem container) return false;
        queueDragId = row.Entry.Id;
        queueDragActive = true;
        queueDragViewer = FindChild<ScrollViewer>(QueueSongs);
        if (container.ContentTemplateRoot is FrameworkElement source && source.FindName("QueueTitleViewport") is Grid titleHost)
            ResetQueueTitle(titleHost);
        Point topLeft = container.TransformToVisual(Root).TransformPoint(default);
        queueDragGrabOffset = new(rootPoint.X - topLeft.X, rootPoint.Y - topLeft.Y);
        queueDragLastPoint = Root.TransformToVisual(QueueSongs).TransformPoint(rootPoint);
        queueDragFloating = new ContentControl
        {
            Content = row, DataContext = row, ContentTemplate = (DataTemplate)Root.Resources["QueueRowTemplate"],
            Width = container.ActualWidth, Height = container.ActualHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false, IsTabStop = false,
            Background = QueueShellChrome.Background, BorderBrush = QueueShellChrome.BorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Opacity = .96
        };
        queueDragFloating.Loaded += OnQueueDragFloatingLoaded;
        QueueFlightLayer.Children.Add(queueDragFloating);
        PositionQueueDragFloating(queueDragLastPoint);
        SyncQueueDragContainer(container);
        return true;
    }

    private void OnQueueDragFloatingLoaded(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        if (sender is not ContentControl { ContentTemplateRoot: FrameworkElement surface, Content: QueueSongItem row }) return;
        PrepareQueueRowSurface(surface, row);
        if (surface.FindName("QueueTitleViewport") is Grid titleHost) ResetQueueTitle(titleHost);
    }

    private void UpdateQueueDragPointer(Point point)
    {
        queueDragLastPoint = point;
        PositionQueueDragFloating(point);
        bool inside = IsInsideQueueDragViewport(point);
        if (inside) MoveQueueDragPreview(QueueDragTargetIndex(point.Y));
        double edge = Math.Min(40, QueueSongs.ActualHeight / 3);
        queueDragScroll = !inside || edge <= 0 ? 0
            : point.Y < edge ? -450 * (edge - point.Y) / edge
            : point.Y > QueueSongs.ActualHeight - edge ? 450 * (point.Y - QueueSongs.ActualHeight + edge) / edge : 0;
        if (queueDragScroll == 0) queueDragTimer.Stop();
        else if (!queueDragTimer.IsEnabled) { queueDragScrollTime = Stopwatch.GetTimestamp(); queueDragTimer.Start(); }
    }

    private bool IsInsideQueueDragViewport(Point point) => point.X >= 0 && point.Y >= 0
        && point.X <= QueueSongs.ActualWidth && point.Y <= QueueSongs.ActualHeight;

    private void PositionQueueDragFloating(Point point)
    {
        if (queueDragFloating is null) return;
        Point position = QueueSongs.TransformToVisual(QueueFlightLayer).TransformPoint(point);
        Canvas.SetLeft(queueDragFloating, position.X - queueDragGrabOffset.X);
        Canvas.SetTop(queueDragFloating, position.Y - queueDragGrabOffset.Y);
    }

    private int QueueDragTargetIndex(double pointerY)
    {
        int from = queueRows.ToList().FindIndex(item => item.Entry.Id == queueDragId);
        if (from < 0) return -1;
        var realized = RealizedQueueDragContainers().Select(container =>
            (Container: container, Index: queueRows.IndexOf((QueueSongItem)container.Content))).Where(item => item.Index >= 0).OrderBy(item => item.Index).ToArray();
        if (realized.Length == 0) return from;
        double centerY = pointerY - queueDragGrabOffset.Y + (queueDragFloating?.Height ?? 72) / 2;
        int boundary = realized[^1].Index + 1;
        foreach (var item in realized)
        {
            if (item.Index == from) continue;
            double top = QueueDragLayoutTop(item.Container);
            if (centerY < top + item.Container.ActualHeight / 2) { boundary = item.Index; break; }
        }
        return Math.Clamp(boundary - (from < boundary ? 1 : 0), 0, queueRows.Count - 1);
    }

    private bool MoveQueueDragPreview(int targetIndex)
    {
        if (!queueDragActive || targetIndex < 0 || targetIndex >= queueRows.Count) return false;
        int from = queueRows.ToList().FindIndex(item => item.Entry.Id == queueDragId);
        if (from < 0 || from == targetIndex) return false;
        long now = Stopwatch.GetTimestamp();
        var positions = new Dictionary<Guid, double>();
        foreach (var container in RealizedQueueDragContainers())
        {
            var row = (QueueSongItem)container.Content;
            double top = QueueDragLayoutTop(container);
            positions[row.Entry.Id] = top + QueueDragMotionOffset(row.Entry.Id, now);
        }
        double? scrollOffset = queueDragViewer?.VerticalOffset;
        queueRows.Move(from, targetIndex);
        for (int i = Math.Min(from, targetIndex); i <= Math.Max(from, targetIndex); i++) queueRows[i].SetPosition(i + 1);
        ScheduleQueueRowSurfaceRefresh();
        QueueSongs.UpdateLayout();
        if (scrollOffset is { } targetScroll && queueDragViewer is not null && Math.Abs(queueDragViewer.VerticalOffset - targetScroll) > .5)
        {
            queueDragViewer.ChangeView(null, targetScroll, null, true);
            QueueSongs.UpdateLayout();
        }
        foreach (var container in RealizedQueueDragContainers())
        {
            SyncQueueDragContainer(container);
            var row = (QueueSongItem)container.Content;
            if (row.Entry.Id == queueDragId || !positions.TryGetValue(row.Entry.Id, out double previous)) continue;
            double top = QueueDragLayoutTop(container);
            AnimateQueueDragContainer(container, row.Entry.Id, previous - top, now);
        }
        return true;
    }

    private IEnumerable<ListViewItem> RealizedQueueDragContainers() => QueueSongs.ItemsPanelRoot is Panel panel
        ? panel.Children.OfType<ListViewItem>().Where(container => container.Content is QueueSongItem) : [];

    private double QueueDragLayoutTop(ListViewItem container)
    {
        // Read the panel's layout slot, not the container's animated composition transform.
        double y = LayoutInformation.GetLayoutSlot(container).Y + container.Margin.Top;
        return QueueSongs.ItemsPanelRoot.TransformToVisual(QueueSongs).TransformPoint(new Point(0, y)).Y;
    }

    private double QueueDragMotionOffset(Guid id, long now)
    {
        if (!queueDragMotions.TryGetValue(id, out var motion)) return 0;
        double t = Math.Clamp(Stopwatch.GetElapsedTime(motion.Started, now).TotalMilliseconds / QueueDragMotionMilliseconds, 0, 1);
        return motion.From * Math.Pow(1 - t, 3);
    }

    private void AnimateQueueDragContainer(ListViewItem container, Guid id, double offset, long now)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StopAnimation("Translation.Y");
        if (Math.Abs(offset) < .1) { queueDragMotions.Remove(id); return; }
        queueDragMotions[id] = new(offset, now);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)offset);
        animation.InsertKeyFrame(1, 0, visual.Compositor.CreateCubicBezierEasingFunction(new(1f / 3, 1), new(2f / 3, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(QueueDragMotionMilliseconds);
        visual.StartAnimation("Translation.Y", animation);
    }

    private void SyncQueueDragContainer(ListViewItem container)
    {
        if (closed) return;
        Guid id = container.Content is QueueSongItem row ? row.Entry.Id : Guid.Empty;
        if (queueDragContainers.TryGetValue(container, out Guid previous) && previous != id)
            ResetQueueDragContainerTranslation(container);
        queueDragContainers[container] = id;
        container.Opacity = queueDragActive && id == queueDragId ? 0 : 1;
    }

    private static void ResetQueueDragContainerTranslation(ListViewItem container)
    {
        // Enable the opt-in Translation property before stopping its animation path.
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StopAnimation("Translation.Y");
    }

    private void OnQueueDragScrollTick(object? sender, object args)
    {
        if (!queueDragActive || queueDragViewer is not { } viewer || queueDragScroll == 0)
        { queueDragTimer.Stop(); return; }
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp(Stopwatch.GetElapsedTime(queueDragScrollTime, now).TotalSeconds, 0, .05);
        queueDragScrollTime = now;
        double offset = Math.Clamp(viewer.VerticalOffset + queueDragScroll * dt, 0, viewer.ScrollableHeight);
        if (Math.Abs(offset - viewer.VerticalOffset) < .1) { queueDragTimer.Stop(); return; }
        viewer.ChangeView(null, offset, null, true);
        QueueSongs.UpdateLayout();
        MoveQueueDragPreview(QueueDragTargetIndex(queueDragLastPoint.Y));
    }

    private bool FinishQueueDrag(bool commit, bool refresh = true)
    {
        bool existed = queueDragPointer is not null || queueDragId is not null || queueDragActive;
        Guid? id = queueDragId;
        bool wasActive = queueDragActive;
        int target = id is null ? -1 : queueRows.ToList().FindIndex(row => row.Entry.Id == id);
        var pointer = queueDragPointer;
        // Clear capture state before ReleasePointerCapture raises PointerCaptureLost.
        queueDragPointer = null; queueDragId = null; queueDragActive = false;
        queueDragTimer.Stop(); queueDragScroll = 0; queueDragViewer = null;
        if (!closed && queueDragFloating is { } floating) QueueFlightLayer.Children.Remove(floating);
        queueDragFloating = null;
        if (!closed)
            foreach (var container in queueDragContainers.Keys)
            { container.Opacity = 1; ResetQueueDragContainerTranslation(container); }
        queueDragContainers.Clear(); queueDragMotions.Clear();
        if (!closed && pointer is not null) QueueSongs.ReleasePointerCapture(pointer);
        bool moved = wasActive && commit && id is { } entryId && target >= 0
            && viewModel.Queue.MovePending(entryId, target);
        if (wasActive && !moved && refresh && !closed) RefreshQueuePresentation();
        return existed;
    }

    private bool CancelQueueDrag(bool refresh = true) => FinishQueueDrag(false, refresh);
    private void ClearQueueDrag() => CancelQueueDrag();

    private void OnQueueHandleKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } || args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        CancelQueueDrag();
        viewModel.Queue.MovePending(row.Entry.Id, row.Position - 1 + (args.Key == VirtualKey.Up ? -1 : 1));
        args.Handled = true;
    }
}
