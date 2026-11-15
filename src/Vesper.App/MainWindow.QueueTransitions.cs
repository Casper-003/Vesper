using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Vesper.App;

public sealed class RightSidebarSplitterGrip : Grid
{
    public RightSidebarSplitterGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}

public sealed partial class MainWindow
{
    private Storyboard? queueLayoutAnimation;
    private bool queueLayoutAnimating, splittingRightSidebar;
    private double rightSplitRatio = 0.382;
    private readonly RectangleGeometry singingRevealClip = new();
    private readonly RectangleGeometry queueRevealClip = new();

    internal double RightSplitRatio => rightSplitRatio;

    internal void SetRightSplitRatio(double ratio, bool save = true)
    {
        double clamped = Math.Clamp(double.IsFinite(ratio) ? ratio : 0.382, 0.18, 0.82);
        rightSplitRatio = clamped;
        SingingShellRow.Height = new GridLength(clamped, GridUnitType.Star);
        QueueShellRow.Height = new GridLength(1 - clamped, GridUnitType.Star);
        if (save && ready) SavePreferences();
    }

    private void OnRightSplitterPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!queueSidebarVisible || queueSidebarAnimating) return;
        splittingRightSidebar = RightSidebarSplitter.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnRightSplitterMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!splittingRightSidebar) return;
        double available = Math.Max(1, QueueSidebarSlot.ActualHeight - 4);
        double minHeight = Math.Min(110, available * 0.25);
        double y = args.GetCurrentPoint(QueueSidebarSlot).Position.Y;
        double top = Math.Clamp(y - 2, minHeight, Math.Max(minHeight, available - minHeight));
        SetRightSplitRatio(top / available, save: false);
        args.Handled = true;
    }

    private void OnRightSplitterReleased(object sender, PointerRoutedEventArgs args) => FinishRightSplitterDrag(args.Pointer);
    private void OnRightSplitterCanceled(object sender, PointerRoutedEventArgs args) => FinishRightSplitterDrag(args.Pointer);
    private void OnRightSplitterCaptureLost(object sender, PointerRoutedEventArgs args) => FinishRightSplitterDrag(null);

    private void FinishRightSplitterDrag(Pointer? pointer)
    {
        if (!splittingRightSidebar) return;
        splittingRightSidebar = false;
        if (pointer is not null) RightSidebarSplitter.ReleasePointerCapture(pointer);
        if (ready) SavePreferences();
    }

    private void OnRightPanelModeChanged(object sender, RoutedEventArgs args)
    {
        if (!ready) return;
        if (LinkRightPanelsSwitch.IsOn && singingPanelOpen != queuePanelOpen)
            SetRightPanelsOpen(true, true);
        else RefreshRightPanelChrome();
        SavePreferences();
    }

    private void CloseRightPanels() => SetRightPanelsOpen(false, false);

    private void SetQueueOpen(bool open) =>
        SetRightPanelsOpen(LinkRightPanelsSwitch.IsOn ? open : singingPanelOpen, open);

    private void SetRightPanelsOpen(bool openSinging, bool openQueue)
    {
        if (!queueSidebarVisible || queueSidebarAnimating) return;
        if (singingPanelOpen == openSinging && queuePanelOpen == openQueue) return;
        ResetQueueRailHint();
        double rail = QueueRailHost.Width;
        // A closed card grows out of (and shrinks back into) its collapsed strip,
        // including the hover preview that already shows the card's leading edge.
        double fromSinging = SingingCardHost.Visibility == Visibility.Visible ? Math.Max(rail, SingingCardHost.ActualWidth) : rail;
        double fromQueue = QueueCardHost.Visibility == Visibility.Visible ? Math.Max(rail, QueueCardHost.ActualWidth) : rail;
        if (!openQueue && FindChild<ScrollViewer>(QueueSongs) is { } viewer) queueScrollOffset = viewer.VerticalOffset;
        StopQueueLayoutTransition();
        ResetRailPreviews();
        singingPanelOpen = openSinging;
        queuePanelOpen = openQueue;
        UpdateQueueToggle();
        double cardWidth = QueueCardWidth;
        SingingCard.Width = cardWidth;
        QueueCard.Width = cardWidth;
        queueLayoutAnimating = true;
        SingingCardHost.Width = fromSinging;
        QueueCardHost.Width = fromQueue;
        double targetSinging = openSinging ? cardWidth : rail;
        double targetQueue = openQueue ? cardWidth : rail;
        bool singingActive = fromSinging > rail + .5 || openSinging;
        bool queueActive = fromQueue > rail + .5 || openQueue;
        if (singingActive) { SingingCardHost.Visibility = Visibility.Visible; SingingCardHost.Opacity = 1; }
        if (queueActive) { QueueCardHost.Visibility = Visibility.Visible; QueueCardHost.Opacity = 1; }

        bool linkedMotion = openSinging == openQueue && Math.Abs(fromSinging - fromQueue) < .5;
        if (linkedMotion)
        {
            Grid.SetColumnSpan(BrowserCard, 1);
            BrowserCard.Margin = new Thickness(0);
        }
        else
        {
            Grid.SetColumnSpan(BrowserCard, 2);
            SyncBrowserOverlayMargin(fromSinging, fromQueue);
        }
        RefreshRightPanelChrome(singingActive, queueActive);
        PositionSingingCard();
        UpdateSingingHostClip();
        PositionQueueCard();
        UpdateQueueHostClip();

        var storyboard = new Storyboard();
        if (Math.Abs(fromSinging - targetSinging) > .1)
        {
            var singingWidth = CreateQueueWidthAnimation(fromSinging, targetSinging);
            Storyboard.SetTarget(singingWidth, SingingCardHost);
            Storyboard.SetTargetProperty(singingWidth, nameof(FrameworkElement.Width));
            storyboard.Children.Add(singingWidth);
        }
        if (Math.Abs(fromQueue - targetQueue) > .1)
        {
            var queueWidth = CreateQueueWidthAnimation(fromQueue, targetQueue);
            // The real card boundary drives layout. Native text trimming, action positions,
            // one-unit outlines and corner radii follow it without scaled or clipped stand-ins.
            Storyboard.SetTarget(queueWidth, QueueCardHost);
            Storyboard.SetTargetProperty(queueWidth, nameof(FrameworkElement.Width));
            storyboard.Children.Add(queueWidth);
        }
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueLayoutAnimation, storyboard)) return;
            // Store the final base value before stopping so stopping cannot expose the
            // starting width and enqueue another intermediate layout.
            SingingCardHost.Width = singingPanelOpen ? QueueCardWidth : 0;
            QueueCardHost.Width = queuePanelOpen ? QueueCardWidth : 0;
            StopQueueLayoutTransition();
            if (!closed) SettleQueueLayout(restoreScroll: true);
        };
        queueLayoutAnimation = storyboard;
        storyboard.Begin();
        if (openQueue) { queueAvailabilityTimer.Start(); _ = CheckQueueAvailabilityAsync(); }
        else { queueAvailabilityTimer.Stop(); ClearQueueDrag(); }
    }

    private static DoubleAnimation CreateQueueWidthAnimation(double from, double target) => new()
    {
        From = from, To = target, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
    };

    private void StopQueueLayoutTransition()
    {
        queueLayoutAnimation?.Stop();
        queueLayoutAnimation = null;
        queueLayoutAnimating = false;
    }

    private void SyncBrowserOverlayMargin(double? singingWidth = null, double? queueWidth = null)
    {
        if (Grid.GetColumnSpan(BrowserCard) != 2) return;
        double rail = QueueRailHost.Width;
        double sw = singingWidth ?? (SingingCardHost.Visibility == Visibility.Visible ? SingingCardHost.ActualWidth : 0);
        double qw = queueWidth ?? (QueueCardHost.Visibility == Visibility.Visible ? QueueCardHost.ActualWidth : 0);
        double squeeze = Math.Min(Math.Max(0, sw - rail), Math.Max(0, qw - rail));
        BrowserCard.Margin = new Thickness(0, 0, QueueSidebarWidth + QueueCardGutter + squeeze, 0);
    }

    private void RefreshRightPanelChrome(bool singingActive = false, bool queueActive = false)
    {
        bool overlayMode = Grid.GetColumnSpan(BrowserCard) == 2;
        if (overlayMode && (singingPanelOpen || singingActive))
            SingingShellChrome.Background = PreviewTransport.FrostedPanel(Root.ActualTheme);
        else SingingShellChrome.ClearValue(Border.BackgroundProperty);
        if (overlayMode && (queuePanelOpen || queueActive))
            QueueShellChrome.Background = PreviewTransport.FrostedPanel(Root.ActualTheme);
        else QueueShellChrome.ClearValue(Border.BackgroundProperty);
    }

    private void SettleQueueLayout(bool restoreScroll = false, bool focusToggle = true)
    {
        double cardWidth = QueueCardWidth;
        SingingCard.Width = cardWidth;
        QueueCard.Width = cardWidth;
        SingingCardHost.Width = singingPanelOpen ? cardWidth : 0;
        QueueCardHost.Width = queuePanelOpen ? cardWidth : 0;
        SingingCardHost.Opacity = singingPanelOpen ? 1 : 0;
        QueueCardHost.Opacity = queuePanelOpen ? 1 : 0;
        if (singingPanelOpen == queuePanelOpen)
        {
            Grid.SetColumnSpan(BrowserCard, 1);
            BrowserCard.Margin = new Thickness(0);
        }
        else
        {
            Grid.SetColumnSpan(BrowserCard, 2);
            BrowserCard.Margin = new Thickness(0, 0, QueueSidebarWidth + QueueCardGutter, 0);
        }
        UpdateQueueToggle();
        RefreshRightPanelChrome();
        PositionSingingCard();
        UpdateSingingHostClip();
        PositionQueueCard();
        UpdateQueueHostClip();
        if (!singingPanelOpen)
        {
            if (singingRailPointerOver) SetRailPreview(singing: true, show: true);
            else SingingCardHost.Visibility = Visibility.Collapsed;
        }
        if (!queuePanelOpen)
        {
            if (queueRailPointerOver) SetRailPreview(singing: false, show: true);
            else QueueCardHost.Visibility = Visibility.Collapsed;
            if (focusToggle && queueSidebarVisible && !singingPanelOpen) QueueOpenButton.Focus(FocusState.Programmatic);
        }
        else if (restoreScroll && FindChild<ScrollViewer>(QueueSongs) is { } scroll) scroll.ChangeView(null, queueScrollOffset, null, true);
    }

    private void OnQueueLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        // Only the user's outer window resize changes this grid's width. The animated
        // Auto column changes its sibling's width, rather than this parent grid.
        if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) < .5) return;
        if (queueSidebarAnimating)
        {
            StopQueueSidebarTransition();
            SettleQueueSidebar();
            return;
        }
        if (queueLayoutAnimating) StopQueueLayoutTransition();
        if (singingPanelOpen || queuePanelOpen || SingingCardHost.Visibility == Visibility.Visible || QueueCardHost.Visibility == Visibility.Visible)
            SettleQueueLayout();
    }

    private void OnSingingHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        PositionSingingCard(args.NewSize.Height);
        UpdateSingingHostClip();
        if (queueLayoutAnimating) SyncBrowserOverlayMargin();
    }

    private void PositionSingingCard(double? hostHeight = null)
    {
        SetSingingCardHeight(hostHeight ?? SingingCardHost.ActualHeight);
        Canvas.SetLeft(SingingCard, 0);
    }

    private void SetSingingCardHeight(double hostHeight)
    {
        double height = Math.Max(0, hostHeight);
        if (double.IsNaN(SingingCard.Height) || Math.Abs(SingingCard.Height - height) > .01) SingingCard.Height = height;
    }

    private void UpdateSingingHostClip()
    {
        var bounds = new Rect(0, 0, Math.Max(0, SingingCardHost.ActualWidth), Math.Max(0, SingingCardHost.ActualHeight));
        if (!singingRevealClip.Rect.Equals(bounds)) singingRevealClip.Rect = bounds;
        if (!ReferenceEquals(SingingCardHost.Clip, singingRevealClip)) SingingCardHost.Clip = singingRevealClip;
    }

    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // ActualHeight does not notify ElementName bindings. A Canvas also does not
        // stretch its children, so the card must receive the arranged height explicitly.
        PositionQueueCard(args.NewSize.Height);
        UpdateQueueHostClip();
        if (queueLayoutAnimating) SyncBrowserOverlayMargin();
    }

    private void PositionQueueCard(double? hostHeight = null)
    {
        SetQueueCardHeight(hostHeight ?? QueueCardHost.ActualHeight);
        Canvas.SetLeft(QueueCard, 0);
    }

    private void SetQueueCardHeight(double hostHeight)
    {
        double height = Math.Max(0, hostHeight);
        if (double.IsNaN(QueueCard.Height) || Math.Abs(QueueCard.Height - height) > .01) QueueCard.Height = height;
    }

    private void UpdateQueueHostClip()
    {
        var bounds = new Rect(0, 0, Math.Max(0, QueueCardHost.ActualWidth), Math.Max(0, QueueCardHost.ActualHeight));
        if (!queueRevealClip.Rect.Equals(bounds)) queueRevealClip.Rect = bounds;
        if (!ReferenceEquals(QueueCardHost.Clip, queueRevealClip)) QueueCardHost.Clip = queueRevealClip;
    }
}
