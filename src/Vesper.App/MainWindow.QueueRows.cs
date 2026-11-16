using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly SongRowHoverGroup queueHoverGroup = new();
    private bool queueRowSurfaceRefreshQueued;

    private void PrepareQueueRowSurface(FrameworkElement element, QueueSongItem row)
    {
        if (element is not SongRowSurface surface) return;
        int index = Math.Max(0, row.Position - 1);
        bool prepare = surface.RowIndex != index || !ReferenceEquals(surface.HoverGroup, queueHoverGroup);
        surface.HoverGroup = queueHoverGroup;
        if (!prepare) return;
        var normal = (Brush)Root.Resources[index % 2 == 0 ? "OverviewEvenBrush" : "OverviewOddBrush"];
        var hover = (Brush)Root.Resources[index % 2 == 0 ? "OverviewEvenHoverBrush" : "OverviewOddHoverBrush"];
        surface.Prepare(index, normal, hover);
    }

    // Collection moves can keep the same realized container while changing its stripe index.
    // Coalesce repaints until the collection and its reused containers have settled.
    private void ScheduleQueueRowSurfaceRefresh()
    {
        if (closed || queueRowSurfaceRefreshQueued) return;
        queueRowSurfaceRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            queueRowSurfaceRefreshQueued = false;
            if (closed) return;
            foreach (var surface in RealizedQueueRowSurfaces())
                if (surface.DataContext is QueueSongItem row) PrepareQueueRowSurface(surface, row);
        });
    }

    private void RefreshQueueRowPalette()
    {
        foreach (var surface in RealizedQueueRowSurfaces()) surface.RefreshHoverPalette();
    }

    private IEnumerable<SongRowSurface> RealizedQueueRowSurfaces()
    {
        foreach (var list in new[] { QueueSongs, HomeSections.QueueList })
            if (list.ItemsPanelRoot is Panel panel)
                foreach (var item in panel.Children.OfType<ListViewItem>())
                    if (item.ContentTemplateRoot is SongRowSurface surface) yield return surface;
        if (FindChild<SongRowSurface>(QueueCurrentContent) is { } current) yield return current;
        if (FindChild<SongRowSurface>(HomeSections.CurrentQueueHost) is { } homeCurrent) yield return homeCurrent;
    }
}
