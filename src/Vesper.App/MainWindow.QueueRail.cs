using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private const double RailPreviewOpacity = .5;
    private static readonly TimeSpan RightPanelFade = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan RailPreviewFadeIn = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RailPreviewFadeOut = TimeSpan.FromMilliseconds(180);
    private bool singingRailPointerOver, queueRailPointerOver, singingShellPointerInside, queueShellPointerInside;
    private bool singingRailPreview, queueRailPreview;
    private int singingRailPreviewRevision, queueRailPreviewRevision;
    private Storyboard? singingPreviewStoryboard, queuePreviewStoryboard;

    internal bool SingingRailPreviewShown => singingRailPreview;
    internal bool QueueRailPreviewShown => queueRailPreview;

    private void InitializeRightPanelFades()
    {
        foreach (UIElement element in new UIElement[] { SingingCollapseButton, QueueCollapseButton })
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
        if (singingPreviewStoryboard is null && !singingRailPreview)
        {
            if (SingingToggleChevron.Opacity != 0) SingingToggleChevron.Opacity = 0;
        }
        if (queuePreviewStoryboard is null && !queueRailPreview)
        {
            if (QueueToggleChevron.Opacity != 0) QueueToggleChevron.Opacity = 0;
        }
    }

    private void SetRailPreview(bool singing, bool show)
    {
        Canvas host = singing ? SingingCardHost : QueueCardHost;
        UIElement chevron = singing ? SingingToggleChevron : QueueToggleChevron;
        bool open = singing ? singingPanelOpen : queuePanelOpen;
        show = show && !closed && queueSidebarVisible && !queueSidebarAnimating && !open && !queueLayoutAnimating;

        double currentHostOpacity = host.Visibility == Visibility.Visible ? host.Opacity : 0;
        double currentChevronOpacity = chevron.Opacity;
        StopRailPreviewStoryboard(singing);
        host.Opacity = currentHostOpacity;
        chevron.Opacity = currentChevronOpacity;

        if (open || queueLayoutAnimating || queueSidebarAnimating)
        {
            if (singing) singingRailPreview = false; else queueRailPreview = false;
            return;
        }

        if (singing) singingRailPreview = show; else queueRailPreview = show;
        int revision = singing ? ++singingRailPreviewRevision : ++queueRailPreviewRevision;

        if (show)
        {
            double cardWidth = QueueCardWidth;
            host.Width = QueueRailHost.Width;
            if (host.Visibility != Visibility.Visible)
            {
                currentHostOpacity = 0;
                currentChevronOpacity = 0;
                host.Opacity = 0;
                chevron.Opacity = 0;
                host.Visibility = Visibility.Visible;
            }
            if (singing) { SingingCard.Width = cardWidth; PositionSingingCard(); UpdateSingingHostClip(); }
            else { QueueCard.Width = cardWidth; PositionQueueCard(); UpdateQueueHostClip(); }

            if (!systemUi.AnimationsEnabled)
            {
                host.Opacity = RailPreviewOpacity;
                chevron.Opacity = 1;
                return;
            }

            var storyboard = new Storyboard();
            AddOpacityFade(storyboard, host, currentHostOpacity, RailPreviewOpacity, RailPreviewFadeIn);
            AddOpacityFade(storyboard, chevron, currentChevronOpacity, 1, RailPreviewFadeIn);
            if (singing) singingPreviewStoryboard = storyboard; else queuePreviewStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (closed || revision != (singing ? singingRailPreviewRevision : queueRailPreviewRevision)) return;
                if (singing) singingPreviewStoryboard = null; else queuePreviewStoryboard = null;
                host.Opacity = RailPreviewOpacity;
                chevron.Opacity = 1;
            };
            storyboard.Begin();
        }
        else
        {
            if (host.Visibility != Visibility.Visible)
            {
                host.Opacity = 0;
                chevron.Opacity = 0;
                host.Width = 0;
                return;
            }

            if (!systemUi.AnimationsEnabled)
            {
                host.Opacity = 0;
                chevron.Opacity = 0;
                host.Visibility = Visibility.Collapsed;
                host.Width = 0;
                return;
            }

            var storyboard = new Storyboard();
            AddOpacityFade(storyboard, host, currentHostOpacity, 0, RailPreviewFadeOut);
            AddOpacityFade(storyboard, chevron, currentChevronOpacity, 0, RailPreviewFadeOut);
            if (singing) singingPreviewStoryboard = storyboard; else queuePreviewStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (closed || revision != (singing ? singingRailPreviewRevision : queueRailPreviewRevision)) return;
                if (singing) singingPreviewStoryboard = null; else queuePreviewStoryboard = null;
                if ((singing ? singingPanelOpen : queuePanelOpen) || queueLayoutAnimating || queueSidebarAnimating) return;
                host.Opacity = 0;
                chevron.Opacity = 0;
                host.Visibility = Visibility.Collapsed;
                host.Width = 0;
            };
            storyboard.Begin();
        }
    }

    private void StopRailPreviewStoryboard(bool singing)
    {
        if (singing)
        {
            singingPreviewStoryboard?.Stop();
            singingPreviewStoryboard = null;
        }
        else
        {
            queuePreviewStoryboard?.Stop();
            queuePreviewStoryboard = null;
        }
    }

    private static void AddOpacityFade(Storyboard storyboard, UIElement target, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        storyboard.Children.Add(animation);
    }

    private void ResetRailPreviews()
    {
        ++singingRailPreviewRevision;
        ++queueRailPreviewRevision;
        StopRailPreviewStoryboard(true);
        StopRailPreviewStoryboard(false);
        singingRailPreview = queueRailPreview = false;
        SingingToggleChevron.Opacity = 0;
        QueueToggleChevron.Opacity = 0;
    }

    private void AnimateQueueRailHint(bool peek)
    {
        AnimateQueueRailPeek(peek);
        AnimateSingingRailContent(1);
        AnimateQueueRailContent(1);
    }

    private void ResetQueueRailHint() => ResetQueueRailPeek();

    private void AnimateSingingRailContent(float target) => AnimateRailContent(SingingToggleContent, target);
    private void AnimateQueueRailContent(float target) => AnimateRailContent(QueueToggleContent, target);

    private void AnimateRailContent(UIElement content, float target)
    {
        if (closed || !queueSidebarVisible || queueSidebarAnimating) return;
        content.CenterPoint = new Vector3(17, 36, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Target = "Scale";
        pulse.InsertExpressionKeyFrame(0, "this.StartingValue");
        pulse.InsertKeyFrame(1, new Vector3(target));
        pulse.Duration = TimeSpan.FromMilliseconds(100);
        content.StartAnimation(pulse);
    }
}
