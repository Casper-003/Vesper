using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // 保持滚动属性集的强引用；滚动动画全部由合成线程求值。
    private sealed record OverviewMotion(ScrollViewer Viewer, CompositionPropertySet Scroll, CompositionPropertySet Metrics,
        double TitleWidthPerFontUnit, double TitleHeightPerFontUnit, double TitleLineHeightPerFontUnit)
    {
        internal float HeaderHeight = float.NaN;
        internal double NativeTitleFontSize = double.NaN;
    }
    private readonly Dictionary<ListView, OverviewMotion> overviewMotions = [];
    private bool stripeRefreshQueued;
    private bool overviewScrolling;
    private readonly DispatcherTimer overviewIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(175) };
    private readonly DispatcherTimer metadataRowsTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private long lastOverviewScroll;
    private readonly HashSet<FrameworkElement> pendingMetadataRows = [];
    private readonly SongRowHoverGroup songHoverGroup = new();
    private object GetOverviewDiagnostics()
    {
        bool favorite = (ContentFrame.Content as ShellPage)?.PageKey == "收藏";
        var list = favorite ? FavoriteSongs : LibrarySongs;
        var panel = list.ItemsPanelRoot as ItemsStackPanel;
        return new
        {
            Page = favorite ? "Favorites" : "Library", TotalRows = list.Items.Count,
            RealizedRows = panel?.Children.Count ?? 0, FirstVisibleIndex = panel?.FirstVisibleIndex, LastVisibleIndex = panel?.LastVisibleIndex,
            Scrolling = overviewScrolling, PendingMetadataRows = pendingMetadataRows.Count,
            ActiveMetadataReads = metadataTasks.Values.Count(task => !task.IsCompleted),
            viewModel.LastFilterMilliseconds, viewModel.LastCollectionMoves
            , ThemeSignals = themeSignals, ThemeRepaints = themeRepaints
        };
    }

    private void OnOverviewViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (closed) return;
        UpdateOverviewScrollbarView(sender, e);
        lastOverviewScroll = Stopwatch.GetTimestamp();
        if (overviewScrolling) return;
        overviewScrolling = true;
        metadataRowsTimer.Stop();
        viewModel.SetPresentationScrolling(true);
        overviewIdleTimer.Start();
    }
    private void OnOverviewIdle(object? sender, object e)
    {
        if (Stopwatch.GetElapsedTime(lastOverviewScroll).TotalMilliseconds < 350) return;
        overviewIdleTimer.Stop();
        overviewScrolling = false;
        if (closed) return;
        OnOverviewScrollbarIdle();
        viewModel.SetPresentationScrolling(false);
        if (pendingMetadataRows.Count > 0) metadataRowsTimer.Start();
    }
    private void QueueVisibleRowMetadata(FrameworkElement row)
    {
        if (Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is { Length: > 0 }) return;
        var song = row.DataContext as LibrarySongItem;
        if (song is null || song.MetadataLoaded || song.MetadataRetryAfter > DateTimeOffset.UtcNow) return;
        if (closed) return;
        pendingMetadataRows.Add(row);
        if (!overviewScrolling && !metadataRowsTimer.IsEnabled) metadataRowsTimer.Start();
    }
    private void OnMetadataRowsReady(object? sender, object e)
    {
        metadataRowsTimer.Stop();
        if (closed || overviewScrolling) return;
        var rows = pendingMetadataRows.ToArray();
        pendingMetadataRows.Clear();
        foreach (var row in rows)
            if (row.IsLoaded && row.DataContext is LibrarySongItem song && IsSongVisible(song)) _ = LoadSongMetadataAsync(song, visibleOnly: true);
    }
    private bool IsSongVisible(LibrarySongItem song)
    {
        var key = (ContentFrame.Content as ShellPage)?.PageKey;
        var list = key == "曲库" ? LibrarySongs : key == "收藏" ? FavoriteSongs : null;
        if (list?.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        for (int index = Math.Max(0, panel.FirstVisibleIndex); index <= panel.LastVisibleIndex; index++)
            if (list.ContainerFromIndex(index) is ListViewItem { Content: LibrarySongItem visible } && ReferenceEquals(visible, song)) return true;
        return false;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T target) return target;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private (Grid Page, Grid Hero, Canvas TitleMotion, TextBlock Title, Grid Spacer, Grid ScrollRail, ScrollBar ScrollBar)
        GetOverviewParts(ListView list)
    {
        if (ReferenceEquals(list, HomeOverview))
            return (HomePage, HomeHeroLayer, HomeTitleMotion, HomeTitleBold, HomeHeaderSpace, HomeScrollRail, HomeScrollBar);
        if (ReferenceEquals(list, CreationOverview))
            return (CreationPage, CreationHeroLayer, CreationTitleMotion, CreationTitleBold, CreationHeaderSpace, CreationScrollRail, CreationScrollBar);
        if (ReferenceEquals(list, FavoriteSongs))
            return (FavoritesPage, FavoriteHeroLayer, FavoriteTitleMotion, FavoriteTitleBold, FavoriteHeaderSpace, FavoriteScrollRail, FavoriteScrollBar);
        return (LibraryPage, LibraryHeroLayer, LibraryTitleMotion, LibraryTitleBold, LibraryHeaderSpace, LibraryScrollRail, LibraryScrollBar);
    }

    private async void OnOverviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView list) return;
        ConfigureOverview(list);
        var song = ReferenceEquals(list, FavoriteSongs) ? viewModel.LatestFavorite
            : ReferenceEquals(list, LibrarySongs) ? viewModel.LibraryHero : null;
        if (song is not null) await LoadDetailsAsync(song);
    }
    private void OnOverviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        ConfigureOverview(ReferenceEquals(sender, HomePage) ? HomeOverview
            : ReferenceEquals(sender, CreationPage) ? CreationOverview
            : ReferenceEquals(sender, FavoritesPage) ? FavoriteSongs : LibrarySongs);

    private void ConfigureOverview(ListView list)
    {
        var parts = GetOverviewParts(list);
        Grid page = parts.Page;
        overviewMotions.TryGetValue(list, out var existing);
        var scroll = existing?.Viewer ?? FindChild<ScrollViewer>(list);
        if (page.ActualHeight <= 0 || page.ActualWidth <= 0 || scroll is null) return;
        ConfigureOverviewScrollbar(list, scroll);
        Grid hero = parts.Hero;
        Canvas titleMotion = parts.TitleMotion;
        TextBlock title = parts.Title;
        Grid spacer = parts.Spacer;
        float height = (float)Math.Max(64, page.ActualHeight * .382);
        hero.Height = height;
        bool library = ReferenceEquals(list, LibrarySongs);
        spacer.Height = height - 48 + (library ? 52 : 16);
        if ((library || ReferenceEquals(list, FavoriteSongs)) && list.Footer is FrameworkElement footer)
            footer.Height = 0;
        if (existing is not null)
        {
            UpdateOverviewTitleLayout(existing, title, page.ActualWidth - titleMotion.Margin.Left - 32, height);
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(hero);
        var compositor = visual.Compositor;
        var properties = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroll);
        var metrics = compositor.CreatePropertySet();
        // Measure the typeface proportions once. Background changes set the native
        // text size; only scrolling scales that correctly sized text down.
        title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var motion = new OverviewMotion(scroll, properties, metrics,
            title.DesiredSize.Width / title.FontSize, title.DesiredSize.Height / title.FontSize,
            title.LineHeight / title.FontSize);
        overviewMotions[list] = motion;
        UpdateOverviewTitleLayout(motion, title, page.ActualWidth - titleMotion.Margin.Left - 32, height);
        scroll.ViewChanged += OnOverviewViewChanged;
        const string progress = "Clamp(-scroll.Translation.Y / (metrics.Height - 48), 0, 1)";
        ExpressionAnimation Expression(string formula)
        {
            var animation = compositor.CreateExpressionAnimation(formula);
            animation.SetReferenceParameter("scroll", properties);
            animation.SetReferenceParameter("metrics", metrics);
            return animation;
        }
        var clip = compositor.CreateInsetClip();
        visual.Clip = clip;
        clip.StartAnimation("BottomInset", Expression($"({progress}) * (metrics.Height - 48)"));
        // Animate the shared title host through XAML facade properties, avoiding layout-owned offsets/scales.
        var translation = Expression($"Vector3(0, (metrics.Height - metrics.TitleHeight - 16) * (1 - ({progress})) + 9 * ({progress}), 0)");
        translation.Target = "Translation";
        titleMotion.StartAnimation(translation);
        string visibleScale = $"metrics.ExpandedTitleScale * (1 - ({progress})) + metrics.CompactTitleScale * ({progress})";
        var scale = Expression($"Vector3({visibleScale}, {visibleScale}, 1)");
        scale.Target = "Scale";
        titleMotion.StartAnimation(scale);
        if (library)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(LibraryActions, true);
            ElementCompositionPreview.GetElementVisual(LibraryActions).StartAnimation("Translation.Y",
                Expression($"metrics.Height * (1 - ({progress}))"));
        }
    }

    private static void UpdateOverviewTitleLayout(OverviewMotion motion, TextBlock title, double availableWidth, float height)
    {
        // Render native glyphs at the fitted size instead of enlarging a small cached
        // glyph layout. An unchanged fit does not invalidate text layout again.
        double fitFontSize = Math.Max(1, Math.Min(height * .46 / Math.Max(.01, motion.TitleHeightPerFontUnit),
            Math.Max(1, availableWidth - 2) / Math.Max(.01, motion.TitleWidthPerFontUnit)));
        if (Math.Abs(title.FontSize - fitFontSize) > .01)
        {
            title.FontSize = fitFontSize;
            title.LineHeight = fitFontSize * motion.TitleLineHeightPerFontUnit;
        }
        if (motion.HeaderHeight != height)
        {
            motion.HeaderHeight = height;
            motion.Metrics.InsertScalar("Height", height);
        }
        if (motion.NativeTitleFontSize != title.FontSize)
        {
            motion.NativeTitleFontSize = title.FontSize;
            motion.Metrics.InsertScalar("TitleHeight", (float)(title.FontSize * motion.TitleHeightPerFontUnit));
            motion.Metrics.InsertScalar("ExpandedTitleScale", 1);
            motion.Metrics.InsertScalar("CompactTitleScale", (float)Math.Min(1, 24 / title.FontSize));
        }
    }

    private void OnOverviewContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is SongRowSurface surface)
        {
            if (args.InRecycleQueue) surface.SetHovered(false);
            else
            {
                // Compiled templates may reuse their binding root without updating inherited DataContext.
                // Keep command handlers and the cover Image's binding on the current song as well.
                if (!ReferenceEquals(surface.DataContext, args.Item)) surface.DataContext = args.Item;
                PaintSongRow(surface, args.ItemIndex);
            }
        }
        else if (!args.InRecycleQueue && args.Phase == 0)
            args.RegisterUpdateCallback(1, OnOverviewContainerChanging);
    }
    private void SetRowBrush(ListViewItem item, int index)
    {
        if (item.ContentTemplateRoot is SongRowSurface surface) PaintSongRow(surface, index);
    }
    private void PaintSongRow(SongRowSurface surface, int index)
    {
        if (index < 0) return;
        var normal = (Brush)Root.Resources[index % 2 == 0 ? "OverviewEvenBrush" : "OverviewOddBrush"];
        var hover = (Brush)Root.Resources[index % 2 == 0 ? "OverviewEvenHoverBrush" : "OverviewOddHoverBrush"];
        surface.HoverGroup = songHoverGroup;
        surface.Prepare(index, normal, hover);
    }
    private void PrepareLoadedSongRow(FrameworkElement row)
    {
        if (row is not SongRowSurface surface) return;
        for (DependencyObject? parent = row; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ListViewItem container)
            {
                int index = LibrarySongs.IndexFromContainer(container);
                if (index < 0) index = FavoriteSongs.IndexFromContainer(container);
                PaintSongRow(surface, index);
                return;
            }
    }

    private void OnOverviewRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (stripeRefreshQueued || closed) return;
        stripeRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            stripeRefreshQueued = false;
            if (closed) return;
            foreach (var list in new[] { LibrarySongs, FavoriteSongs, HomeOverview, CreationOverview })
            {
                if (list.ItemsPanelRoot is Panel panel)
                    foreach (var container in panel.Children.OfType<ListViewItem>()) SetRowBrush(container, list.IndexFromContainer(container));
                ConfigureOverview(list);
            }
        });
    }
    private void ApplyOverviewTheme(bool dark)
    {
        ((SolidColorBrush)Root.Resources["OverviewEvenBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 43, 43, 43) : Windows.UI.Color.FromArgb(255, 250, 250, 250);
        ((SolidColorBrush)Root.Resources["OverviewOddBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 34, 34, 34) : Windows.UI.Color.FromArgb(255, 239, 239, 239);
        ((SolidColorBrush)Root.Resources["OverviewEvenHoverBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 56, 56, 56) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        ((SolidColorBrush)Root.Resources["OverviewOddHoverBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 22, 22, 22) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        foreach (var list in new[] { LibrarySongs, FavoriteSongs })
            if (list.ItemsPanelRoot is Panel panel)
                foreach (var item in panel.Children.OfType<ListViewItem>())
                    if (item.ContentTemplateRoot is SongRowSurface surface) surface.RefreshHoverPalette();
        RefreshQueueRowPalette();
    }
}
