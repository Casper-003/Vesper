using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private ListView? overviewBackdropList;
    private InsetClip? overviewBackdropClip, overviewBackdropSourceClip;

    private void PrepareOverviewBackground(string key, string? songId)
    {
        overviewBackdropList = songId is not null ? null : key switch
        {
            "首页" => HomeOverview,
            "曲库" => LibrarySongs,
            "收藏" => FavoriteSongs,
            "创作" => CreationOverview,
            _ => null
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
            overviewBackdropSourceClip = null;
        }
    }

    private void SyncOverviewBackground(ListView list)
    {
        if (closed || !ReferenceEquals(list, overviewBackdropList)) return;
        var visual = ElementCompositionPreview.GetElementVisual(OverviewBackdrop);
        var clip = overviewBackdropClip ??= visual.Compositor.CreateInsetClip();
        if (!ReferenceEquals(visual.Clip, clip)) visual.Clip = clip;
        var source = ElementCompositionPreview.GetElementVisual(GetOverviewParts(list).Hero).Clip as InsetClip;
        if (source is not null && ReferenceEquals(source, overviewBackdropSourceClip)) return;
        clip.StopAnimation(nameof(InsetClip.BottomInset));
        overviewBackdropSourceClip = source;
        if (source is null) clip.BottomInset = 0;
        else
        {
            // Read the original animated inset on the compositor; never stop or
            // replace the original header's clip or any of its animations.
            var bottom = visual.Compositor.CreateExpressionAnimation("source.BottomInset");
            bottom.SetReferenceParameter("source", source);
            clip.StartAnimation(nameof(InsetClip.BottomInset), bottom);
        }
    }
}
