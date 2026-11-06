using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;

namespace Vesper.App;

// Native horizontal layout checks with synthetic cards; no personal library,
// network requests, shell preference writes or desktop automation.
internal sealed class HomeCheckWindow : Window
{
    private readonly HomeContent home = new();
    private readonly ListView outer = new() { SelectionMode = ListViewSelectionMode.None, Margin = new Thickness(24),
        Header = new Grid { Height = 160 } };
    private bool started;
    internal HomeCheckWindow(string reportPath)
    {
        Title = "Vesper · 首页隔离检查"; outer.Footer = home; Content = outer;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900));
        outer.Loaded += async (_, _) => { if (!started) { started = true; await RunAsync(reportPath); } };
    }
    private static T? Child<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T result) return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Child<T>(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    private async Task RunAsync(string reportPath)
    {
        var checks = new List<object>(); string? error = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        async Task Until(Func<bool> condition)
        {
            long start = Environment.TickCount64;
            while (!condition()) { if (Environment.TickCount64 - start > 10000) throw new TimeoutException(); await Task.Delay(30); }
        }
        try
        {
            var artists = Enumerable.Range(0, 1000).Select(i =>
            {
                var card = new HomeCard($"artist-{i}", true); card.Update(new CatalogArtist(card.Id, $"艺人 {i:0000}")); return card;
            }).ToArray();
            var songs = Enumerable.Range(0, 40).Select(i =>
            {
                var card = new HomeCard($"song-{i}", false); card.Update(new LibrarySongItem(new(card.Id, $"歌曲 {i:00}", []))); return card;
            }).ToArray();
            home.Bind(artists, songs.Take(12).ToArray(), songs);
            await Until(() => home.ArtistViewport is { ScrollableWidth: > 1000, ViewportWidth: > 300 }
                && home.Lists[0].ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: not null });
            Check(home.ArtistCards.Count == 1000 && home.RecommendedCards.Count == 12 && home.FavoriteCards.Count == 40,
                "艺人、推荐、收藏各自绑定完整一行内容");
            var list = home.Lists[0];
            Check(list.ItemsPanelRoot is ItemsStackPanel { Orientation: Orientation.Horizontal } panel && panel.Children.Count < 60,
                "千个艺人卡使用横向虚拟化，不一次创建整库卡片");
            var first = (ListViewItem)list.ContainerFromIndex(0);
            Check(Math.Abs(((FrameworkElement)first.ContentTemplateRoot).ActualWidth - 224) < 1,
                "艺人封面维持大卡尺寸，不按窗口均分拉伸");
            ((IInvokeProvider)new ButtonAutomationPeer(home.ArtistNext).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => home.ArtistViewport!.HorizontalOffset > 100);
            double offset = home.ArtistViewport!.HorizontalOffset;
            Check(offset > 100, "右箭头实际推进横向内容，禁用手势不阻断按钮浏览");
            home.Bind(artists, songs.Take(12).ToArray(), songs);
            await Task.Delay(100);
            Check(Math.Abs(home.ArtistViewport.HorizontalOffset - offset) < 1, "相同数据刷新保留横向位置，不重建卡片行");
            outer.Footer = null; await Task.Delay(50); outer.Footer = home;
            await Until(() => home.IsLoaded && home.ArtistViewport.ViewportWidth > 300);
            await Task.Delay(100);
            Check(Math.Abs(home.ArtistViewport.HorizontalOffset - offset) < 1, "离页卸载再挂回首页仍保留卡片横向位置");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 600));
            await Until(() => home.ActualWidth < 1000 && home.ArtistViewport.ViewportWidth < 1000);
            await Until(() => list.ItemsPanelRoot is ItemsStackPanel p && list.ContainerFromIndex(Math.Max(0, p.FirstVisibleIndex)) is ListViewItem { ContentTemplateRoot: not null });
            var realized = (ListViewItem)list.ContainerFromIndex(((ItemsStackPanel)list.ItemsPanelRoot).FirstVisibleIndex);
            Check(Math.Abs(((FrameworkElement)realized.ContentTemplateRoot).ActualWidth - 224) < 1
                && ((ItemsStackPanel)list.ItemsPanelRoot).Orientation == Orientation.Horizontal,
                "窄窗口只减少可见卡片数量，封面不缩小也不换行");
            var viewer = Child<ScrollViewer>(outer)!;
            double beforeHeight = viewer.ExtentHeight;
            home.Bind(artists, songs.Take(12).ToArray(), []);
            await Until(() => viewer.ExtentHeight < beforeHeight - 100);
            Check(!home.FavoriteSectionVisible && viewer.ExtentHeight < beforeHeight - 100, "收藏为空时标题和整行空间一起隐藏");
            home.Bind([], [], []);
            Check(home.Visibility == Visibility.Collapsed, "三个区域均为空时不留空标题或内容块");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Checks = checks, Error = error,
                Limits = "合成卡片验证原生排布、虚拟化和按钮滚动；真实首页图片、物理滚轮、页面往返与队列开合观感须人工验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
