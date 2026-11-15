using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private bool queueSidebarVisible = true, queueSidebarAnimating;
    private Storyboard? queueSidebarAnimation;
    private readonly RectangleGeometry queueSidebarClip = new();

    private void SetQueueSidebarVisible(bool visible)
    {
        if (queueSidebarVisible == visible) return;
        ResetQueueRailHint();
        double from = QueueSidebarSlot.Visibility == Visibility.Visible ? QueueSidebarSlot.ActualWidth : 0;
        if (queuePanelOpen && FindChild<ScrollViewer>(QueueSongs) is { } viewer) queueScrollOffset = viewer.VerticalOffset;
        QueueSidebarSlot.Width = from;
        StopQueueSidebarTransition();
        StopQueueLayoutTransition();
        ResetRailPreviews();
        queueSidebarVisible = visible;
        singingPanelOpen = false;
        queuePanelOpen = false;
        queueAvailabilityTimer.Stop();
        ClearQueueDrag();
        UpdateQueueToggle();
        Grid.SetColumnSpan(BrowserCard, 1);
        BrowserCard.Margin = new Thickness(0);
        SingingShell.IsHitTestVisible = false;
        RightSidebarSplitter.IsHitTestVisible = false;
        QueueShell.IsHitTestVisible = false;
        QueueSidebarSlot.Visibility = Visibility.Visible;
        queueSidebarAnimating = true;
        ApplyQueueSidebarWidth(from, QueueSidebarSlot.ActualHeight);

        double target = visible ? QueueSidebarWidth + QueueCardGutter : 0;
        var storyboard = new Storyboard();
        var width = CreateQueueWidthAnimation(from, target);
        Storyboard.SetTarget(width, QueueSidebarSlot);
        Storyboard.SetTargetProperty(width, nameof(FrameworkElement.Width));
        storyboard.Children.Add(width);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueSidebarAnimation, storyboard)) return;
            QueueSidebarSlot.Width = target;
            StopQueueSidebarTransition();
            if (!closed) SettleQueueSidebar();
        };
        queueSidebarAnimation = storyboard;
        storyboard.Begin();
    }

    private void StopQueueSidebarTransition()
    {
        var animation = queueSidebarAnimation;
        queueSidebarAnimation = null;
        queueSidebarAnimating = false;
        animation?.Stop();
    }

    private void OnQueueSidebarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (queueSidebarAnimating) ApplyQueueSidebarWidth(args.NewSize.Width, args.NewSize.Height);
    }

    private void ApplyQueueSidebarWidth(double width, double height)
    {
        // Use the same right-anchored reveal as ordinary queue closing. Once the
        // content is gone, the unscaled rail and its gutter also slide out of view.
        double shellWidth = Math.Max(QueueSidebarWidth, width - QueueCardGutter);
        if (SingingShell.Width != shellWidth) SingingShell.Width = shellWidth;
        if (QueueShell.Width != shellWidth) QueueShell.Width = shellWidth;
        double contentWidth = shellWidth > QueueSidebarWidth + .5 ? Math.Max(0, shellWidth - 2) : 0;
        if (SingingCardHost.Width != contentWidth) SingingCardHost.Width = contentWidth;
        if (QueueCardHost.Width != contentWidth) QueueCardHost.Width = contentWidth;
        SingingCardHost.Opacity = contentWidth > 0 ? 1 : 0;
        QueueCardHost.Opacity = contentWidth > 0 ? 1 : 0;
        SingingCardHost.Visibility = contentWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueCardHost.Visibility = contentWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionSingingCard();
        UpdateSingingHostClip();
        PositionQueueCard();
        UpdateQueueHostClip();
        queueSidebarClip.Rect = new Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
        QueueSidebarSlot.Clip = queueSidebarClip;
    }

    private void SettleQueueSidebar()
    {
        SingingShell.Width = double.NaN;
        QueueShell.Width = double.NaN;
        QueueSidebarSlot.Width = queueSidebarVisible ? double.NaN : 0;
        QueueSidebarSlot.Visibility = queueSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        QueueSidebarSlot.Clip = null;
        SingingShell.IsHitTestVisible = queueSidebarVisible;
        RightSidebarSplitter.IsHitTestVisible = queueSidebarVisible;
        QueueShell.IsHitTestVisible = queueSidebarVisible;
        SettleQueueLayout(focusToggle: false);
    }
}
