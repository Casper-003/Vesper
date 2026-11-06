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
        internal string TitleText = "";
        internal InsetClip? HeroClip;
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
        QueueCurrentVisibleArtwork();
        QueueHomeArtwork();
        if (pendingMetadataRows.Count > 0) metadataRowsTimer.Start();
    }
    private void QueueVisibleRowMetadata(FrameworkElement row)
    {
        if (Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is { Length: > 0 }) return;
        var song = row.DataContext as LibrarySongItem;
        if (song is null || song.MetadataLoaded && song.ListCover is not null) return;
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
            if (row.IsLoaded && row.DataContext is LibrarySongItem song && IsSongVisible(song)) _ = LoadVisibleRowPresentationAsync(song);
    }
    private bool IsSongVisible(LibrarySongItem song)
    {
        var key = (ContentFrame.Content as ShellPage)?.PageKey;
        var list = key == "曲库" ? LibrarySongs : key == "收藏" ? FavoriteSongs
            : key is not null && pageContent.GetValueOrDefault(key) is EntityDetailsView entity ? entity.SongList : null;
        if (list?.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        for (int index = Math.Max(0, panel.FirstVisibleIndex); index <= panel.LastVisibleIndex; index++)
            if (list.ContainerFromIndex(index) is ListViewItem container
                && ReferenceEquals(container.Content is EntitySongRow row ? row.Song : container.Content, song)) return true;
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
        if (entityOverviews.TryGetValue(list, out var entity))
            return (entity.PageGrid, entity.HeroLayer, entity.TitleMotionHost, entity.HeroTitle,
                entity.HeaderSpace, entity.ScrollRail, entity.ScrollBarPart);
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
        ConfigureOverview(list, rebind: true);
        var song = ReferenceEquals(list, FavoriteSongs) ? viewModel.LatestFavorite
            : ReferenceEquals(list, LibrarySongs) ? viewModel.LibraryHero : null;
        if (song is not null) await LoadDetailsAsync(song);
    }
    private void OnOverviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        ConfigureOverview(ReferenceEquals(sender, HomePage) ? HomeOverview
            : ReferenceEquals(sender, CreationPage) ? CreationOverview
            : ReferenceEquals(sender, FavoritesPage) ? FavoriteSongs : LibrarySongs);

    private void ConfigureOverview(ListView list, bool rebind = false)
    {
        var parts = GetOverviewParts(list);
        Grid page = parts.Page;
        overviewMotions.TryGetValue(list, out var existing);
        var scroll = FindChild<ScrollViewer>(list);
        if (page.ActualHeight <= 0 || page.ActualWidth <= 0 || scroll is null) return;
        ConfigureOverviewScrollbar(list, scroll);
        Grid hero = parts.Hero;
        Canvas titleMotion = parts.TitleMotion;
        TextBlock title = parts.Title;
        Grid spacer = parts.Spacer;
        float height = (float)Math.Max(64, page.ActualHeight * .382);
        hero.Height = height;
        entityOverviews.TryGetValue(list, out var entity);
        entity?.RefreshHeaderMetrics(height);
        bool library = ReferenceEquals(list, LibrarySongs);
        spacer.Height = height - 48 + (library ? 52 : 16);
        // Vertically center left-aligned empty states below the hero, leaving the library toolbar clear.
        ScrollViewer? emptyState = library ? LibraryEmptyState
            : ReferenceEquals(list, FavoriteSongs) ? FavoriteEmptyState
            : ReferenceEquals(list, CreationOverview) ? CreationEmptyState : null;
        if (emptyState is not null)
            emptyState.Margin = new Thickness(24, height + (library ? 52 : 0) + 24, 24, 24);
        if ((library || ReferenceEquals(list, FavoriteSongs)) && list.Footer is FrameworkElement footer)
            footer.Height = 0;
        bool refreshBindings = rebind || existing is not null && !ReferenceEquals(existing.Viewer, scroll);
        if (existing is not null)
        {
            existing.Metrics.InsertScalar("TitleLeftShift", (float)(entity is null ? 0 : Math.Max(0, titleMotion.Margin.Left - 32)));
            if (existing.TitleText != title.Text)
            {
                if (entity is null)
                {
                    title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    existing = existing with
                    {
                        TitleWidthPerFontUnit = title.DesiredSize.Width / title.FontSize,
                        TitleHeightPerFontUnit = title.DesiredSize.Height / title.FontSize,
                        TitleLineHeightPerFontUnit = title.LineHeight / title.FontSize
                    };
                }
                existing.TitleText = title.Text;
                overviewMotions[list] = existing;
            }
            if (!refreshBindings)
            {
                UpdateOverviewTitleLayout(existing, title, page.ActualWidth - titleMotion.Margin.Left - 32, height, entity);
                var currentVisual = ElementCompositionPreview.GetElementVisual(hero);
                if (existing.HeroClip is not null && !ReferenceEquals(currentVisual.Clip, existing.HeroClip))
                    currentVisual.Clip = existing.HeroClip;
                SyncOverviewBackground(list);
                return;
            }
            existing.Viewer.ViewChanged -= OnOverviewViewChanged;
        }
        var visual = ElementCompositionPreview.GetElementVisual(hero);
        var compositor = visual.Compositor;
        var properties = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroll);
        var metrics = existing?.Metrics ?? compositor.CreatePropertySet();
        metrics.InsertScalar("TitleLeftShift", (float)(entity is null ? 0 : Math.Max(0, titleMotion.Margin.Left - 32)));
        // Measure the typeface proportions once. Background changes set the native
        // text size; only scrolling scales that correctly sized text down.
        OverviewMotion motion;
        if (existing is null)
        {
            title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            motion = new(scroll, properties, metrics,
                title.DesiredSize.Width / title.FontSize, title.DesiredSize.Height / title.FontSize,
                title.LineHeight / title.FontSize);
        }
        else motion = existing with { Viewer = scroll, Scroll = properties };
        motion.TitleText = title.Text;
        overviewMotions[list] = motion;
        UpdateOverviewTitleLayout(motion, title, page.ActualWidth - titleMotion.Margin.Left - 32, height, entity);
        scroll.ViewChanged += OnOverviewViewChanged;
        const string progress = "Clamp(-scroll.Translation.Y / (metrics.Height - 48), 0, 1)";
        ExpressionAnimation Expression(string formula)
        {
            var animation = compositor.CreateExpressionAnimation(formula);
            animation.SetReferenceParameter("scroll", properties);
            animation.SetReferenceParameter("metrics", metrics);
            return animation;
        }
        var clip = motion.HeroClip ??= compositor.CreateInsetClip();
        clip.StopAnimation("BottomInset");
        clip.BottomInset = (float)Math.Clamp(scroll.VerticalOffset, 0, Math.Max(0, height - 48));
        visual.Clip = clip;
        clip.StartAnimation("BottomInset", Expression($"({progress}) * (metrics.Height - 48)"));
        // Animate the shared title host through XAML facade properties, avoiding layout-owned offsets/scales.
        var translation = Expression($"Vector3(-metrics.TitleLeftShift * ({progress}), metrics.ExpandedTitleY * (1 - ({progress})) + 9 * ({progress}), 0)");
        translation.Target = "Translation";
        titleMotion.StartAnimation(translation);
        string visibleScale = $"metrics.ExpandedTitleScale * (1 - ({progress})) + metrics.CompactTitleScale * ({progress})";
        var scale = Expression($"Vector3({visibleScale}, {visibleScale}, 1)");
        scale.Target = "Scale";
        titleMotion.StartAnimation(scale);
        if (entity is not null)
        {
            // This is the same title throughout the transition. Crop only its
            // extra lines as it collapses; never crossfade two layers of text.
            var titleVisual = ElementCompositionPreview.GetElementVisual(title);
            var titleClip = compositor.CreateInsetClip();
            titleVisual.Clip = titleClip;
            titleClip.StartAnimation("BottomInset", Expression($"Max(0, metrics.TitleHeight - metrics.TitleLineHeight) * ({progress})"));
            void Animate(UIElement target, string property, string formula)
            {
                var animation = Expression(formula);
                animation.Target = property;
                target.StartAnimation(animation);
            }
            // Shrink around the artwork's center and keep it inside the same
            // shrinking header as the title, rather than clipping off a full-size image.
            Animate(entity.HeroArtwork, "Scale", $"Vector3(1 - ({progress}), 1 - ({progress}), 1)");
            Animate(entity.HeroArtwork, "Translation", $"Vector3(metrics.ArtworkShiftX * ({progress}), metrics.ArtworkShiftY * ({progress}), 0)");
            Animate(entity.HeroArtwork, "Opacity", $"1 - Clamp((({progress}) - 0.35) / 0.65, 0, 1)");
            Animate(entity.HeroSubtitle, "Translation", $"Vector3(-metrics.TitleLeftShift * ({progress}), metrics.SubtitleShiftY * ({progress}), 0)");
            Animate(entity.HeroSubtitle, "Opacity", $"1 - ({progress})");
        }
        if (library)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(LibraryActions, true);
            ElementCompositionPreview.GetElementVisual(LibraryActions).StartAnimation("Translation.Y",
                Expression($"metrics.Height * (1 - ({progress}))"));
        }
        SyncOverviewBackground(list);
    }

    private static void UpdateOverviewTitleLayout(OverviewMotion motion, TextBlock title, double availableWidth, float height,
        EntityDetailsView? entity = null)
    {
        // Render native glyphs at the fitted size instead of enlarging a small cached
        // glyph layout. An unchanged fit does not invalidate text layout again.
        double fitFontSize = Math.Max(1, Math.Min(height * .46 / Math.Max(.01, motion.TitleHeightPerFontUnit),
            Math.Max(1, availableWidth - 2) / Math.Max(.01, motion.TitleWidthPerFontUnit)));
        if (entity is not null)
        {
            // Width constrains layout, not the font size: long names wrap into
            // the text column instead of becoming an ever-smaller single line.
            fitFontSize = Math.Clamp(height * .14, 26, 72);
            title.Width = Math.Max(1, availableWidth);
            title.MaxWidth = Math.Max(1, availableWidth);
            ToolTipService.SetToolTip(title, title.Text);
        }
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
        double titleHeight = title.FontSize * motion.TitleHeightPerFontUnit;
        double lineHeight = Math.Max(title.LineHeight, titleHeight);
        if (entity is not null)
        {
            double heightBudget = Math.Max(lineHeight, height - (entity.HasSubtitle ? 52 : 24));
            title.MaxLines = Math.Clamp((int)Math.Floor(heightBudget / lineHeight), 1, 3);
            title.Measure(new Windows.Foundation.Size(title.Width, double.PositiveInfinity));
            titleHeight = Math.Max(lineHeight, title.DesiredSize.Height);
            motion.Metrics.InsertScalar("TitleHeight", (float)titleHeight);
            motion.Metrics.InsertScalar("TitleLineHeight", (float)lineHeight);
        }
        double expandedY = entity?.RefreshTextMetrics(height, titleHeight) ?? height - titleHeight - 16;
        motion.Metrics.InsertScalar("ExpandedTitleY", (float)expandedY);
        if (entity is not null)
        {
            var artwork = entity.HeroArtwork;
            motion.Metrics.InsertScalar("ArtworkShiftX", (float)(32 - artwork.Margin.Left - artwork.Width / 2));
            motion.Metrics.InsertScalar("ArtworkShiftY", (float)(24 - artwork.Margin.Top - artwork.Height / 2));
            double compactTitleHeight = lineHeight * Math.Min(1, 24 / title.FontSize);
            motion.Metrics.InsertScalar("SubtitleShiftY", (float)(9 + compactTitleHeight - entity.HeroSubtitle.Margin.Top));
        }
    }

    private void OnOverviewContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var surface = args.ItemContainer.ContentTemplateRoot as SongRowSurface
            ?? (args.ItemContainer.ContentTemplateRoot is { } root ? FindChild<SongRowSurface>(root) : null);
        if (surface is not null)
        {
            if (args.InRecycleQueue) { surface.SetHovered(false); ForgetSongRowArtwork(surface); }
            else
            {
                // Compiled templates may reuse their binding root without updating inherited DataContext.
                // Keep command handlers and the cover Image's binding on the current song as well.
                object item = args.Item is EntitySongRow row ? row.Song : args.Item;
                if (!ReferenceEquals(surface.DataContext, item)) surface.DataContext = item;
                ObserveSongRowArtwork(surface);
                QueueVisibleRowMetadata(surface);
                PaintSongRow(surface, args.ItemIndex);
            }
        }
        else if (!args.InRecycleQueue && args.Phase == 0)
            args.RegisterUpdateCallback(1, OnOverviewContainerChanging);
    }
    private void SetRowBrush(ListViewItem item, int index)
    {
        var surface = item.ContentTemplateRoot as SongRowSurface
            ?? (item.ContentTemplateRoot is { } root ? FindChild<SongRowSurface>(root) : null);
        if (surface is not null) PaintSongRow(surface, index);
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
                if (index < 0)
                    foreach (var list in entityOverviews.Keys)
                    { index = list.IndexFromContainer(container); if (index >= 0) break; }
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
            foreach (var list in new[] { LibrarySongs, FavoriteSongs, HomeOverview, CreationOverview }.Concat(entityOverviews.Keys))
            {
                if (list.ItemsPanelRoot is Panel panel)
                    foreach (var container in panel.Children.OfType<ListViewItem>()) SetRowBrush(container, list.IndexFromContainer(container));
                ConfigureOverview(list);
            }
        });
    }
    private void ApplyOverviewTheme(bool dark)
    {
        ApplyHeroColors(dark);
        ((SolidColorBrush)Root.Resources["OverviewEvenBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 43, 43, 43) : Windows.UI.Color.FromArgb(255, 250, 250, 250);
        ((SolidColorBrush)Root.Resources["OverviewOddBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 34, 34, 34) : Windows.UI.Color.FromArgb(255, 239, 239, 239);
        ((SolidColorBrush)Root.Resources["OverviewEvenHoverBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 56, 56, 56) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        ((SolidColorBrush)Root.Resources["OverviewOddHoverBrush"]).Color = dark ? Windows.UI.Color.FromArgb(255, 22, 22, 22) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        foreach (var list in new[] { LibrarySongs, FavoriteSongs }.Concat(entityOverviews.Keys))
            if (list.ItemsPanelRoot is Panel panel)
                foreach (var item in panel.Children.OfType<ListViewItem>())
                {
                    var surface = item.ContentTemplateRoot as SongRowSurface
                        ?? (item.ContentTemplateRoot is { } root ? FindChild<SongRowSurface>(root) : null);
                    surface?.RefreshHoverPalette();
                }
        RefreshQueueRowPalette();
    }
}
