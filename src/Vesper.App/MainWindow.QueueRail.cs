using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Collapsed strips stay blank until hovered. Hover fades in the panel's own
    // leading content (Spotify-like) and a vertically centered "<". Expanded cards
    // have no rail; their top-left collapse button fades in while the pointer is
    // inside the card or the button has keyboard focus.
    private const double RailPreviewOpacity = .5;
    private static readonly TimeSpan RightPanelFade = TimeSpan.FromMilliseconds(160);
    private bool singingRailPointerOver, queueRailPointerOver, singingShellPointerInside, queueShellPointerInside;
    private bool singingRailPreview, queueRailPreview;
    private int singingRailPreviewRevision, queueRailPreviewRevision;

    internal bool SingingRailPreviewShown => singingRailPreview;
    internal bool QueueRailPreviewShown => queueRailPreview;

    private void InitializeRightPanelFades()
    {
        foreach (UIElement element in new UIElement[] { SingingCardHost, QueueCardHost, SingingToggleChevron, QueueToggleChevron, SingingCollapseButton, QueueCollapseButton })
            element.OpacityTransition = new ScalarTransition { Duration = RightPanelFade };
    }

    private void OnSingingRailEntered(object sender, PointerRoutedEventArgs args)
    {
        singingRailPointerOver = true;
        AnimateQueueRailHint(!singingPanelOpen && !queuePanelOpen);
        SetRailPreview(singing: true, show: true);
    }
    private void OnSingingRailExited(object sender, PointerRoutedEventArgs args)
    {
        singingRailPointerOver = false;
        AnimateQueueRailHint(false);
        SetRailPreview(singing: true, show: false);
    }
    private void OnSingingRailPressed(object sender, PointerRoutedEventArgs args) => AnimateSingingRailContent(.92f);
    private void OnSingingRailReleased(object sender, PointerRoutedEventArgs args) => AnimateSingingRailContent(1);

    private void OnQueueRailEntered(object sender, PointerRoutedEventArgs args)
    {
        queueRailPointerOver = true;
        AnimateQueueRailHint(!singingPanelOpen && !queuePanelOpen);
        SetRailPreview(singing: false, show: true);
    }
    private void OnQueueRailExited(object sender, PointerRoutedEventArgs args)
    {
        queueRailPointerOver = false;
        AnimateQueueRailHint(false);
        SetRailPreview(singing: false, show: false);
    }
    private void OnQueueRailPressed(object sender, PointerRoutedEventArgs args) => AnimateQueueRailContent(.92f);
    private void OnQueueRailReleased(object sender, PointerRoutedEventArgs args) => AnimateQueueRailContent(1);

    private void OnRightRailFocusChanged(object sender, RoutedEventArgs args) => RefreshRightRailChevrons();

    private void OnSingingShellEntered(object sender, PointerRoutedEventArgs args) { singingShellPointerInside = true; RefreshRightPanelCollapseButtons(); }
    private void OnSingingShellExited(object sender, PointerRoutedEventArgs args) { singingShellPointerInside = false; RefreshRightPanelCollapseButtons(); }
    private void OnQueueShellEntered(object sender, PointerRoutedEventArgs args) { queueShellPointerInside = true; RefreshRightPanelCollapseButtons(); }
    private void OnQueueShellExited(object sender, PointerRoutedEventArgs args) { queueShellPointerInside = false; RefreshRightPanelCollapseButtons(); }
    private void OnRightPanelCollapseFocusChanged(object sender, RoutedEventArgs args) => RefreshRightPanelCollapseButtons();

    private void RefreshRightPanelCollapseButtons()
    {
        SetCollapseButton(SingingCollapseButton, singingPanelOpen, singingShellPointerInside);
        SetCollapseButton(QueueCollapseButton, queuePanelOpen, queueShellPointerInside);

        static void SetCollapseButton(Button button, bool open, bool pointerInside)
        {
            button.IsHitTestVisible = open;
            button.IsTabStop = open;
            double target = open && pointerInside ? 1 : 0;
            if (button.Opacity != target) button.Opacity = target;
        }
    }

    private void RefreshRightRailChevrons()
    {
        SetChevron(SingingToggleChevron, !singingPanelOpen && singingRailPreview);
        SetChevron(QueueToggleChevron, !queuePanelOpen && queueRailPreview);

        static void SetChevron(UIElement chevron, bool shown)
        {
            double target = shown ? 1 : 0;
            if (chevron.Opacity != target) chevron.Opacity = target;
        }
    }

    private void SetRailPreview(bool singing, bool show)
    {
        Canvas host = singing ? SingingCardHost : QueueCardHost;
        bool open = singing ? singingPanelOpen : queuePanelOpen;
        show = show && !closed && queueSidebarVisible && !queueSidebarAnimating && !open && !queueLayoutAnimating;
        if (open || queueLayoutAnimating || queueSidebarAnimating)
        {
            // The card itself owns the host during real opening and closing.
            if (singing) singingRailPreview = false; else queueRailPreview = false;
            RefreshRightRailChevrons();
            return;
        }
        if (singing) singingRailPreview = show; else queueRailPreview = show;
        int revision = singing ? ++singingRailPreviewRevision : ++queueRailPreviewRevision;
        RefreshRightRailChevrons();
        if (show)
        {
            double cardWidth = QueueCardWidth;
            host.Width = QueueRailHost.Width;
            host.Visibility = Visibility.Visible;
            if (singing) { SingingCard.Width = cardWidth; PositionSingingCard(); UpdateSingingHostClip(); }
            else { QueueCard.Width = cardWidth; PositionQueueCard(); UpdateQueueHostClip(); }
            host.Opacity = RailPreviewOpacity;
        }
        else
        {
            host.Opacity = 0;
            _ = HideRailPreviewHostAsync(singing, revision);
        }
    }

    private async Task HideRailPreviewHostAsync(bool singing, int revision)
    {
        await Task.Delay(RightPanelFade + TimeSpan.FromMilliseconds(40));
        if (closed || revision != (singing ? singingRailPreviewRevision : queueRailPreviewRevision)) return;
        if ((singing ? singingPanelOpen : queuePanelOpen) || queueLayoutAnimating || queueSidebarAnimating) return;
        Canvas host = singing ? SingingCardHost : QueueCardHost;
        host.Visibility = Visibility.Collapsed;
        host.Width = 0;
    }

    private void ResetRailPreviews()
    {
        ++singingRailPreviewRevision; ++queueRailPreviewRevision;
        singingRailPreview = queueRailPreview = false;
        RefreshRightRailChevrons();
    }

    private void AnimateQueueRailHint(bool peek)
    {
        AnimateQueueRailPeek(peek);
        AnimateSingingRailContent(1);
        AnimateQueueRailContent(1);
    }

    private void ResetQueueRailHint()
    {
        ResetQueueRailPeek();
    }

    private void AnimateSingingRailContent(float target) => AnimateRailContent(SingingToggleContent, target);
    private void AnimateQueueRailContent(float target) => AnimateRailContent(QueueToggleContent, target);

    private void AnimateRailContent(UIElement content, float target)
    {
        if (closed || !queueSidebarVisible || queueSidebarAnimating) return;
        content.CenterPoint = new Vector3(17, 36, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Target = "Scale"; pulse.InsertExpressionKeyFrame(0, "this.StartingValue");
        pulse.InsertKeyFrame(1, new Vector3(target)); pulse.Duration = TimeSpan.FromMilliseconds(100);
        content.StartAnimation(pulse);
    }
}
