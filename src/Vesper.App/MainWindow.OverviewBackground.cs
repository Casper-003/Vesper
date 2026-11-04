using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private ListView? overviewBackdropList;
    private InsetClip? overviewBackdropClip;
    private OverviewMotion? overviewBackdropMotion;

    private void PrepareOverviewBackground(string key, string? songId)
    {
        overviewBackdropList = songId is not null ? null : key switch
        {
            "首页" => HomeOverview,
            "曲库" => LibrarySongs,
            "收藏" => FavoriteSongs,
            "创作" => CreationOverview,
            _ => pageContent.GetValueOrDefault(key) is EntityDetailsView entity ? entity.SongList : null
        };
        OverviewBackdrop.Visibility = overviewBackdropList is null ? Visibility.Collapsed : Visibility.Visible;
        if (overviewBackdropList is { } list)
        {
            // The existing page still moves in Frame. This separate color layer
            // only fills the gap it uncovers, using the same height and scroll clip.
            OverviewBackdrop.SetBinding(FrameworkElement.HeightProperty, new Binding
            {
                Source = GetOverviewParts(list).Hero,
                Path = new PropertyPath(nameof(FrameworkElement.Height)),
                Mode = BindingMode.OneWay
            });
            SyncOverviewBackground(list);
        }
        else
        {
            OverviewBackdrop.ClearValue(FrameworkElement.HeightProperty);
            overviewBackdropClip?.StopAnimation(nameof(InsetClip.BottomInset));
            overviewBackdropMotion = null;
        }
    }

    private void SyncOverviewBackground(ListView list)
    {
        if (closed || !ReferenceEquals(list, overviewBackdropList)) return;
        var visual = ElementCompositionPreview.GetElementVisual(OverviewBackdrop);
        var clip = overviewBackdropClip ??= visual.Compositor.CreateInsetClip();
        bool reattached = !ReferenceEquals(visual.Clip, clip);
        if (reattached) visual.Clip = clip;
        if (!overviewMotions.TryGetValue(list, out var motion))
        {
            clip.StopAnimation(nameof(InsetClip.BottomInset));
            clip.BottomInset = 0;
            overviewBackdropMotion = null;
            return;
        }
        if (!reattached && ReferenceEquals(motion, overviewBackdropMotion)) return;
        clip.StopAnimation(nameof(InsetClip.BottomInset));
        overviewBackdropMotion = motion;
        clip.BottomInset = (float)Math.Clamp(motion.Viewer.VerticalOffset, 0,
            Math.Max(0, GetOverviewParts(list).Hero.Height - 48));
        // Both layers read the current scroll source directly. Chaining an
        // animated Visual.Clip through another expression can retain a stale
        // clip value when a cached page leaves and re-enters the Frame.
        var bottom = visual.Compositor.CreateExpressionAnimation("Clamp(-scroll.Translation.Y, 0, Max(0, metrics.Height - 48))");
        bottom.SetReferenceParameter("scroll", motion.Scroll);
        bottom.SetReferenceParameter("metrics", motion.Metrics);
        clip.StartAnimation(nameof(InsetClip.BottomInset), bottom);
    }
}
