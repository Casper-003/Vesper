using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vesper.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // This deliberately navigates cached native pages. It does not initialize a
    // library, open song details, invoke providers, or inspect animation base values.
    private async Task<object> CheckOverviewNavigationAsync(List<string> failures)
    {
        if (Environment.GetEnvironmentVariable("VESPER_NAVIGATION_CHECK_PATH") is not { Length: > 0 }
            && Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is not { Length: > 0 })
            throw new InvalidOperationException("Overview navigation checks require isolated runtime mode.");
        if (viewModel.Library.Songs.Count != 0 || viewModel.Library.Directories.Count != 0 || searchText.Length != 0)
            throw new InvalidOperationException("Overview navigation checks require a fresh, uninitialized library and search session.");

        var originalRows = viewModel.Results.ToArray();
        var originalFavorites = viewModel.FavoriteResults.ToArray();
        var originalSize = AppWindow.Size;
        string originalPage = (ContentFrame.Content as ShellPage)?.PageKey ?? "首页";
        bool previousCanSave = canSave;
        canSave = false;
        int checkCount = 0, captureCount = 0, navigations = 0, entityCheckCount = 0, coreMatrixCount = 0;
        var entityFixtures = new Dictionary<string, EntityDetailsView>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        var diagnostics = new List<object>();
        var limitations = new List<string>
        {
            "Synthetic native controls and programmatic Frame/ChangeView navigation; no real library, providers, media or physical pointer input.",
            "RenderTargetBitmap uses the existing test-only opaque Mica substitute. Pixel assertions cover painted header/backdrop clips and title bounds, not desktop visual acceptance.",
            "Home and Create are navigation destinations; their unimplemented content is not populated merely to create artificial scrollability."
        };

        void Check(bool passed, string message)
        { checkCount++; if (!passed) failures.Add("Overview navigation: " + message); }

        async Task Settle(int turns = 24)
        {
            for (int turn = 0; turn < turns; turn++)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(180))
                    throw new TimeoutException("Overview navigation regression exceeded its bounded settling budget.");
                await Task.Delay(16);
                Root.UpdateLayout();
            }
        }

        void Navigate(string key)
        {
            string? from = (ContentFrame.Content as ShellPage)?.PageKey;
            NavigationTransitionInfo transition = key == "创作" || from == "创作"
                ? new SuppressNavigationTransitionInfo() : ShellSlideTransition();
            bool accepted = ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest(key, this), transition);
            navigations++;
            Check(accepted, "Frame rejected navigation to " + key + ".");
            if (entityFixtures.TryGetValue(key, out var fixture))
            {
                // The fixture intentionally has no business EntityId: production
                // catalog refresh must not replace its isolated synthetic rows.
                // Supply only the normal overview shell geometry for this route.
                PageHeading.Visibility = Visibility.Collapsed;
                PageArea.Padding = new Thickness(0);
                Root.UpdateLayout();
                ConfigureOverview(fixture.SongList);
            }
        }

        ListView? ListFor(string key) => key switch
        {
            "曲库" => LibrarySongs, "收藏" => FavoriteSongs,
            "首页" => HomeOverview, "创作" => CreationOverview,
            _ => entityFixtures.GetValueOrDefault(key)?.SongList
        };

        async Task<(int Width, int Height, byte[] Pixels)> Capture()
        {
            await Settle(2);
            captureCount++;
            return await CaptureQueueRootAsync();
        }

        int[] Rgb((int Width, int Height, byte[] Pixels) capture, FrameworkElement page, Point local)
        {
            Point point = page.TransformToVisual(Root).TransformPoint(local);
            int x = (int)Math.Round(point.X * capture.Width / Root.ActualWidth);
            int y = (int)Math.Round(point.Y * capture.Height / Root.ActualHeight);
            if (x < 1 || y < 1 || x + 1 >= capture.Width || y + 1 >= capture.Height)
                throw new InvalidOperationException("Overview navigation pixel sample is outside Root.");
            int[] sum = new int[3];
            for (int py = y - 1; py <= y + 1; py++)
                for (int px = x - 1; px <= x + 1; px++)
                {
                    int offset = (py * capture.Width + px) * 4;
                    sum[0] += capture.Pixels[offset + 2];
                    sum[1] += capture.Pixels[offset + 1];
                    sum[2] += capture.Pixels[offset];
                }
            return sum.Select(value => (int)Math.Round(value / 9d)).ToArray();
        }

        static int Difference(int[] left, int[] right) => Enumerable.Range(0, 3).Max(channel => Math.Abs(left[channel] - right[channel]));

        OverviewNavigationTitlePixels TitlePixels((int Width, int Height, byte[] Pixels) visible,
            (int Width, int Height, byte[] Pixels) withoutHero, FrameworkElement page, double headerHeight, double leftInset = 24)
        {
            if (visible.Width != withoutHero.Width || visible.Height != withoutHero.Height)
                throw new InvalidOperationException("Window changed during paired overview title captures.");
            Point origin = page.TransformToVisual(Root).TransformPoint(default);
            double sx = visible.Width / Root.ActualWidth, sy = visible.Height / Root.ActualHeight;
            int left = Math.Clamp((int)Math.Ceiling((origin.X + leftInset) * sx), 0, visible.Width);
            int right = Math.Clamp((int)Math.Floor((origin.X + page.ActualWidth - 24) * sx), 0, visible.Width);
            int top = Math.Clamp((int)Math.Ceiling(origin.Y * sy), 0, visible.Height);
            int bottom = Math.Clamp((int)Math.Floor((origin.Y + headerHeight) * sy), 0, visible.Height);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int offset = (y * visible.Width + x) * 4;
                    // Header text is white; the accent's retained black mask keeps
                    // the background below this threshold. This excludes a broad
                    // background discrepancy from masquerading as title glyphs.
                    if (visible.Pixels[offset] < 220 || visible.Pixels[offset + 1] < 220 || visible.Pixels[offset + 2] < 220) continue;
                    int delta = Math.Max(Math.Abs(visible.Pixels[offset] - withoutHero.Pixels[offset]),
                        Math.Max(Math.Abs(visible.Pixels[offset + 1] - withoutHero.Pixels[offset + 1]),
                            Math.Abs(visible.Pixels[offset + 2] - withoutHero.Pixels[offset + 2])));
                    if (delta <= 16) continue;
                    count++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            return count == 0 ? new(0, 0, 0, 0, 0) : new(count,
                minX / sx - origin.X, minY / sy - origin.Y, (maxX - minX + 1) / sx, (maxY - minY + 1) / sy);
        }

        async Task<OverviewNavigationTitlePixels> Snapshot(string source, string label, double expectedOffset,
            OverviewNavigationTitlePixels? expectedTitle = null, bool strictOffset = true)
        {
            var list = ListFor(source) ?? throw new InvalidOperationException("The source has no overview list.");
            var parts = GetOverviewParts(list);
            var viewer = FindChild<ScrollViewer>(list) ?? throw new InvalidOperationException("The overview ScrollViewer was not realized.");
            double offset = viewer.VerticalOffset;
            double visibleHeight = Math.Clamp(parts.Hero.Height - offset, 48, parts.Hero.Height);
            bool compact = offset >= parts.Hero.Height - 48 - .5;
            double belowY = compact ? 64 : Math.Min(parts.Page.ActualHeight - 8, visibleHeight + 16);
            var topPoint = new Point(8, 20);
            var belowPoint = new Point(8, belowY);
            Check((ContentFrame.Content as ShellPage)?.PageKey == source, label + ": wrong current Frame page.");
            Check(viewer.ViewportHeight > 0 && viewer.ScrollableHeight > parts.Hero.Height,
                label + ": fixture lacks a finite, meaningfully scrollable viewport.");
            Check(offset > 1 && (!strictOffset || Math.Abs(offset - expectedOffset) <= 1),
                $"{label}: source scroll position changed ({offset:F2} vs {expectedOffset:F2}).");
            Check(OverviewBackdrop.Visibility == Visibility.Visible && ReferenceEquals(overviewBackdropList, list)
                && Math.Abs(OverviewBackdrop.ActualHeight - parts.Hero.ActualHeight) <= 1,
                label + ": shared backdrop is not bound to the returned source header.");

            double heroOpacity = parts.Hero.Opacity;
            var backdropVisibility = OverviewBackdrop.Visibility;
            (int Width, int Height, byte[] Pixels) normal, withoutBackdrop, backdropOnly, neither;
            try
            {
                normal = await Capture();
                OverviewBackdrop.Visibility = Visibility.Collapsed;
                withoutBackdrop = await Capture();
                OverviewBackdrop.Visibility = Visibility.Visible;
                parts.Hero.Opacity = 0;
                backdropOnly = await Capture();
                OverviewBackdrop.Visibility = Visibility.Collapsed;
                neither = await Capture();
            }
            finally
            {
                parts.Hero.Opacity = heroOpacity;
                OverviewBackdrop.Visibility = backdropVisibility;
            }

            int[] normalTop = Rgb(normal, parts.Page, topPoint), sourceTop = Rgb(withoutBackdrop, parts.Page, topPoint);
            int[] patchTop = Rgb(backdropOnly, parts.Page, topPoint), plainTop = Rgb(neither, parts.Page, topPoint);
            int[] normalBelow = Rgb(normal, parts.Page, belowPoint), sourceBelow = Rgb(withoutBackdrop, parts.Page, belowPoint);
            int[] patchBelow = Rgb(backdropOnly, parts.Page, belowPoint), plainBelow = Rgb(neither, parts.Page, belowPoint);
            Check(Difference(normalBelow, sourceBelow) <= 3,
                $"{label}: backdrop leaked accent into the list margin at (8,{belowY:F1}).");
            Check(Difference(sourceBelow, plainBelow) <= 3 && Difference(patchBelow, plainBelow) <= 3,
                label + ": source hero and backdrop do not end at the same scrolled header boundary.");
            Check(Difference(normalTop, sourceTop) <= 3 && Difference(normalTop, patchTop) <= 5,
                label + ": the upper accent patch no longer matches the painted source hero.");
            Check(Difference(patchTop, plainTop) >= 8,
                label + ": the retained upper accent patch produced no distinguishable painted pixels.");
            double titleLeftInset = 24;
            if (entityFixtures.TryGetValue(source, out var fixture))
            {
                double progress = Math.Clamp(offset / Math.Max(1, parts.Hero.Height - 48), 0, 1);
                // Do not count an artist/album placeholder as white title text.
                titleLeftInset = Math.Max(24, 32 + (fixture.TitleMotionHost.Margin.Left - 32) * (1 - progress) - 2);
            }
            var title = TitlePixels(normal, backdropOnly, parts.Page, visibleHeight, titleLeftInset);
            Check(title.Count > 8 && title.Top >= 0 && title.Top + title.Height <= visibleHeight + 1,
                label + ": the actual painted title is missing or outside the scrolled header.");
            if (expectedTitle is { } before)
                Check(Math.Abs(title.Left - before.Left) <= 2 && Math.Abs(title.Top - before.Top) <= 2
                    && Math.Abs(title.Width - before.Width) <= 2 && Math.Abs(title.Height - before.Height) <= 2,
                    label + ": title returned to a different painted position/size while the scroll offset was retained.");
            if (source == "曲库" && compact)
            {
                Point toolbar = LibraryActions.TransformToVisual(parts.Page).TransformPoint(default);
                Check(LibraryActions.Visibility == Visibility.Visible && toolbar.Y >= 0 && toolbar.Y < 48,
                    label + ": compact library toolbar did not return with its header.");
            }
            diagnostics.Add(new { Label = label, Source = source, Offset = offset, ExpectedOffset = expectedOffset,
                HeaderHeight = parts.Hero.Height, ExpectedVisibleHeight = visibleHeight, Compact = compact,
                MarginSample = new { X = 8, Y = belowY }, NormalBelow = normalBelow, WithoutBackdropBelow = sourceBelow,
                BackdropOnlyBelow = patchBelow, NeitherBelow = plainBelow, NormalTop = normalTop, SourceTop = sourceTop,
                BackdropOnlyTop = patchTop, NeitherTop = plainTop, PaintedTitle = title,
                WindowWidth = AppWindow.Size.Width, WindowHeight = AppWindow.Size.Height,
                AtMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1) });
            return title;
        }

        try
        {
            viewModel.Results.Clear(); viewModel.FavoriteResults.Clear();
            for (int index = 0; index < 96; index++)
            {
                var song = new LibrarySongItem(new Song($"navigation-fixture-{index:D3}", $"Navigation song {index:D3}", []))
                    { MetadataLoaded = true };
                viewModel.Results.Add(song); viewModel.FavoriteResults.Add(song);
            }
            foreach (string source in new[] { "曲库", "收藏" })
                foreach (bool compact in new[] { true, false })
                {
                    Navigate(source); await Settle();
                    var list = ListFor(source)!;
                    ConfigureOverview(list); await Settle(2);
                    var parts = GetOverviewParts(list);
                    var viewer = FindChild<ScrollViewer>(list)!;
                    double target = compact ? parts.Hero.Height - 48 + 96 : (parts.Hero.Height - 48) * .45;
                    viewer.ChangeView(null, Math.Min(viewer.ScrollableHeight, target), null, true);
                    await Settle(4);
                    double expectedOffset = viewer.VerticalOffset;
                    string mode = compact ? "compact" : "partial";
                    var title = await Snapshot(source, source + "/" + mode + "/before", expectedOffset);
                    foreach (string destination in new[] { "曲库", "收藏", "首页", "创作", "设置", "搜索" }.Where(key => key != source))
                    {
                        Navigate(destination); await Settle(4);
                        var destinationList = ListFor(destination);
                        Check(destinationList is null ? OverviewBackdrop.Visibility == Visibility.Collapsed
                            : OverviewBackdrop.Visibility == Visibility.Visible && ReferenceEquals(overviewBackdropList, destinationList),
                            source + " → " + destination + ": shared backdrop kept the departed source.");
                        Navigate(source); await Settle();
                        await Snapshot(source, source + "/" + mode + "/via-" + destination, expectedOffset, title);
                    }
                }

            Navigate("曲库"); await Settle();
            var libraryViewer = FindChild<ScrollViewer>(LibrarySongs)!;
            libraryViewer.ChangeView(null, Math.Min(libraryViewer.ScrollableHeight, LibraryHeroLayer.Height + 96), null, true);
            await Settle(4);
            double historyOffset = libraryViewer.VerticalOffset;
            var historyTitle = await Snapshot("曲库", "history/before", historyOffset);
            Navigate("首页"); await Settle(4);
            Check(ContentFrame.CanGoBack, "history: library navigation was not added to Frame history.");
            GoShellBack();
            navigations++; await Settle();
            await Snapshot("曲库", "history/back", historyOffset, historyTitle);
            Check(ContentFrame.CanGoForward, "history: forward destination is missing.");
            GoShellForward(); navigations++; await Settle(4);
            GoShellBack(); navigations++; await Settle();
            await Snapshot("曲库", "history/forward-back", historyOffset, historyTitle);

            foreach (string destination in new[] { "设置", "首页", "创作", "搜索", "曲库" })
            { Navigate(destination); await Settle(1); }
            await Settle();
            await Snapshot("曲库", "rapid/return", historyOffset, historyTitle);

            AppWindow.Resize(new SizeInt32(originalSize.Width + 120, originalSize.Height + 80));
            await Settle();
            if (AppWindow.Size.Width == originalSize.Width && AppWindow.Size.Height == originalSize.Height)
                limitations.Add("The window manager did not accept the bounded resize; unchanged-size restoration was still checked.");
            await Snapshot("曲库", "resize/grown", historyOffset, strictOffset: false);
            AppWindow.Resize(originalSize); await Settle();
            await Snapshot("曲库", "resize/restored", historyOffset, strictOffset: false);

            int beforeEntityChecks = checkCount;
            coreMatrixCount = checkCount;
            foreach (bool isAlbum in new[] { false, true })
            {
                string key = isAlbum ? "__navigation-fixture-album" : "__navigation-fixture-artist";
                var entity = new EntityDetailsView(this, isAlbum);
                entityFixtures.Add(key, entity);
                pageContent.Add(key, entity);
                entityOverviews.Add(entity.SongList, entity);
                var entityRows = Enumerable.Range(0, 96).Select(index => new EntitySongRow(
                    new LibrarySongItem(new Song($"{key}-{index:D3}", $"Entity navigation song {index:D3}", []))
                        { MetadataLoaded = true }, isAlbum ? (index + 1).ToString("00") : "")).ToArray();
                entity.Apply(isAlbum ? "隔离专辑长标题\n第二行导航恢复\n第三行裁切检查" : "隔离艺人长标题\n第二行导航恢复\n第三行裁切检查",
                    isAlbum ? "测试艺人 · 2026" : "", null, entityRows, []);
                foreach (bool compact in new[] { true, false })
                {
                    Navigate(key); await Settle();
                    var parts = GetOverviewParts(entity.SongList);
                    var viewer = FindChild<ScrollViewer>(entity.SongList)
                        ?? throw new InvalidOperationException("The isolated entity fixture has no native ScrollViewer.");
                    Check(entity.SongList.Items.Count == 96 && entity.HeroTitle.Text.Count(character => character == '\n') == 2,
                        key + ": synthetic rows or the three-line title were replaced by a catalog refresh.");
                    double target = compact ? parts.Hero.Height - 48 + 96 : (parts.Hero.Height - 48) * .45;
                    viewer.ChangeView(null, Math.Min(viewer.ScrollableHeight, target), null, true);
                    await Settle(4);
                    double expectedOffset = viewer.VerticalOffset;
                    string mode = compact ? "compact" : "partial";
                    var title = await Snapshot(key, key + "/" + mode + "/before", expectedOffset);
                    foreach (string destination in new[] { "设置", "首页" })
                    {
                        Navigate(destination); await Settle(4);
                        Navigate(key); await Settle();
                        Check(entity.SongList.Items.Count == 96, key + ": returning to the cached native entity control cleared its fixture rows.");
                        await Snapshot(key, key + "/" + mode + "/via-" + destination, expectedOffset, title);
                    }
                }
            }
            entityCheckCount = checkCount - beforeEntityChecks;
            limitations.Add("Artist/album fixtures reuse cached EntityDetailsView controls and the shared native overview machinery, with synthetic private route keys and no EntityId. They do not exercise business catalog IDs, artist/album matching, metadata lookup or song-detail navigation.");
        }
        finally
        {
            if (!closed && entityFixtures.Count > 0)
            {
                int beforeFixtureCleanup = checkCount;
                Navigate("设置");
                entityCheckCount += checkCount - beforeFixtureCleanup;
                foreach (var (key, fixture) in entityFixtures)
                {
                    if (pageOwners.Remove(key, out var owner)) owner.Content = null;
                    pageContent.Remove(key);
                    entityOverviews.Remove(fixture.SongList);
                    if (overviewMotions.Remove(fixture.SongList, out var motion)) motion.Viewer.ViewChanged -= OnOverviewViewChanged;
                    if (overviewScrollbars.Remove(fixture.SongList, out var scrollbar)) DetachOverviewScrollbar(scrollbar);
                }
                foreach (var history in new[] { ContentFrame.BackStack, ContentFrame.ForwardStack })
                    for (int index = history.Count - 1; index >= 0; index--)
                        if (history[index].Parameter is ShellNavigationRequest request && entityFixtures.ContainsKey(request.Key))
                            history.RemoveAt(index);
            }
            AppWindow.Resize(originalSize);
            viewModel.Results.Clear(); foreach (var row in originalRows) viewModel.Results.Add(row);
            viewModel.FavoriteResults.Clear(); foreach (var row in originalFavorites) viewModel.FavoriteResults.Add(row);
            canSave = false;
            if (!closed && originalPage is "首页" or "曲库" or "收藏" or "创作" or "设置" or "搜索")
            {
                int beforeOriginalCleanup = checkCount;
                Navigate(originalPage);
                // Retain the original matrix's final restoration assertion in
                // its own count, allowing direct comparison with the baseline.
                coreMatrixCount += checkCount - beforeOriginalCleanup;
            }
            canSave = previousCanSave;
        }
        return new { CheckCount = checkCount, CoreMatrixCount = coreMatrixCount, EntityCheckCount = entityCheckCount,
            Navigations = navigations, RootCaptures = captureCount,
            FixtureSongs = 96, ElapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1), Diagnostics = diagnostics,
            Limitations = limitations };
    }

    private sealed record OverviewNavigationTitlePixels(int Count, double Left, double Top, double Width, double Height);
}
