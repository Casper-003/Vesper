using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Vesper.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Uses generated disk images and existence-only media; the real preparation
    // gate runs, while the user's library and every online provider stay untouched.
    private async Task RunPagePreparationChecksAsync(string reportPath)
    {
        canSave = false;
        CancelPagePreparation();
        var timer = Stopwatch.StartNew();
        var checks = new List<object>();
        var failures = new List<string>();
        var diagnostics = new List<object>();
        string? storePath = SearchCheckStorePath();
        int libraryCommits = 0, albumCommits = 0;
        LibrarySongItem[] fixtureSongs = [];
        string? albumKey = null;
        void Check(bool condition, string description)
        {
            checks.Add(new { Description = description, Passed = condition });
            if (!condition) failures.Add(description);
        }
        string CurrentKey() => (ContentFrame.Content as ShellPage)?.PageKey ?? "";
        async Task WaitFor(Func<bool> condition, string description)
        {
            for (int turn = 0; turn < 300; turn++)
            {
                Root.UpdateLayout();
                if (condition()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException(description);
        }
        void OnPreparedNavigation(object sender, NavigationEventArgs args)
        {
            if (args.Parameter is not ShellNavigationRequest request) return;
            if (request.Key == "曲库")
            {
                libraryCommits++;
                Check(fixtureSongs.Length > 0 && fixtureSongs.All(song => song.MetadataLoaded && song.ListCover is BitmapImage),
                    "The library commits navigation only after its first-screen metadata and thumbnails are ready.");
            }
            if (request.Key == albumKey)
            {
                albumCommits++;
                var view = pageContent.GetValueOrDefault(request.Key) as EntityDetailsView;
                Check(view?.FindName("EntityCover") is Image { Source: BitmapImage, Visibility: Visibility.Visible },
                    "The album commits with its stable hero image already assigned, rather than an initial placeholder.");
            }
        }
        async Task WritePng(Uri url, byte red, byte green, byte blue)
        {
            string path = artworkCache.GetCachePath(url);
            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))! + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(reportDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Page checks require their own artwork directory beside the report.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            byte[] pixels = new byte[32 * 32 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            { pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 32, 32, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var input = stream.AsStreamForRead();
            await using var output = File.Create(path);
            await input.CopyToAsync(output);
        }

        ContentFrame.Navigated += OnPreparedNavigation;
        try
        {
            if (storePath is null || Environment.GetEnvironmentVariable("VESPER_PAGE_CHECK_PATH") is not { Length: > 0 })
                throw new InvalidOperationException("Page checks require an isolated store and their report environment.");
            var cover = new Uri("https://cdn-images.dzcdn.net/vesper-page-check-cover.png");
            var alternative = new Uri("https://cdn-images.dzcdn.net/vesper-page-check-alternative.png");
            await WritePng(cover, 40, 170, 85);
            await WritePng(alternative, 45, 80, 210);
            string mediaDirectory = Path.Combine(Path.GetDirectoryName(storePath)!, "media");
            Directory.CreateDirectory(mediaDirectory);
            var state = new LibraryState { Directories = [mediaDirectory] };
            AlbumTrackPosition[] positions = [new(1, 2), new(2, 1), new(2, null), new(null, null)];
            for (int index = 0; index < positions.Length; index++)
            {
                string path = Path.Combine(mediaDirectory, $"page-probe-{index:D3}.mp4");
                await File.WriteAllTextAsync(path, "Native page readiness probe " + new string('x', index));
                string id = $"page-ready-{index:D3}";
                var metadata = new SongMetadata($"Page track {index:D3}", "Prepared Artist", "Prepared Album",
                    Year: "2026", Lyrics: "Cached fixture lyrics")
                { AlbumArtist = "Prepared Artist", DiscNumber = positions[index].DiscNumber, TrackNumber = positions[index].TrackNumber };
                var details = new SongDetails(Local: metadata, Online: metadata with { CoverUrl = cover.AbsoluteUri });
                var input = new SongLookupInput(Path.GetFileName(path), details);
                state.Songs.Add(new(id, metadata.Title, [path]));
                state.Details[id] = details with
                { Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
            }
            await viewModel.Store.SaveAsync(state);
            await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
            fixtureSongs = viewModel.Results.ToArray();
            foreach (var song in fixtureSongs) song.MetadataLoaded = true;
            var catalog = viewModel.GetCatalogSnapshot();
            var album = catalog.Albums.Single(value => value.Name == "Prepared Album");
            var tracks = catalog.GetAlbumTracks(album.Id);
            albumKey = "专辑:" + album.Id;
            libraryPrepared.TrySetResult();
            Check(fixtureSongs.Length == 4 && fixtureSongs.All(song => song.ListCover is null)
                && album.CoverUrl == cover.AbsoluteUri,
                "The fixture starts with complete local metadata and one authoritative album cover, but no decoded first screen.");

            int held = 0;
            try
            {
                await coverSlots.WaitAsync(); held++;
                await coverSlots.WaitAsync(); held++;
                string previousPage = CurrentKey();
                var obsoleteSearch = searchRequests.BeginEdit();
                pendingSearch = obsoleteSearch;
                NavigateShell(new ShellNavigationRequest("曲库", this));
                Task preparing = pagePreparationTask;
                Check(CurrentKey() == previousPage && libraryCommits == 0 && !preparing.IsCompleted && !ContentFrame.IsHitTestVisible,
                    "A requested library remains uncommitted while first-screen image work is blocked.");
                Check(!searchRequests.CanPublish(obsoleteSearch),
                    "A non-search navigation intent retires an older search before the delayed page commits.");
                await WaitFor(() => PageLoading.Visibility == Visibility.Visible,
                    "Slow first-screen preparation never exposed its progress indicator.");
                Check(!PageLoadingBar.IsIndeterminate && PageLoadingBar.Maximum > 0
                    && PageLoadingBar.Value >= 0 && PageLoadingBar.Value < PageLoadingBar.Maximum
                    && PageLoadingCount.Text.Contains('/'),
                    "Slow preparation exposes explicit, bounded progress instead of displaying an unfinished page.");
                Task replacedPreparation = preparing;
                NavigateShell(new ShellNavigationRequest("曲库", this));
                preparing = pagePreparationTask;
                Check(!ReferenceEquals(preparing, replacedPreparation) && CurrentKey() == previousPage,
                    "A newer request can replace a preparation of the same destination without committing stale work.");
                coverSlots.Release(held); held = 0;
                await replacedPreparation.WaitAsync(TimeSpan.FromSeconds(10));
                await preparing.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { if (held > 0) coverSlots.Release(held); }
            await WaitFor(() => LibrarySongs.IsLoaded && CurrentKey() == "曲库", "The prepared library did not appear.");
            Check(libraryCommits == 1 && fixtureSongs.All(song => song.ListCover is BitmapImage)
                && PageLoading.Visibility == Visibility.Collapsed && ContentFrame.IsHitTestVisible,
                "The prepared library enters once and releases its loading overlay and input lock.");

            await viewModel.ToggleFavoriteAsync(fixtureSongs[0]);
            held = 0;
            try
            {
                await coverSlots.WaitAsync(); held++;
                await coverSlots.WaitAsync(); held++;
                // Both permits also drain the library hero's existing read before
                // removing its bitmap, so this fixture cannot be satisfied by that read.
                fixtureSongs[0].Thumbnail = null; fixtureSongs[0].Cover = null;
                Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "收藏");
                Task sidebarPreparation = pagePreparationTask;
                Check(CurrentKey() == "曲库" && !sidebarPreparation.IsCompleted,
                    "Selecting Favorites starts preparation without replacing the current library.");
                Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "曲库");
                coverSlots.Release(held); held = 0;
                await sidebarPreparation.WaitAsync(TimeSpan.FromSeconds(10));
                Check(CurrentKey() == "曲库" && PageLoading.Visibility == Visibility.Collapsed && ContentFrame.IsHitTestVisible,
                    "Re-selecting the still-current sidebar page cancels a pending destination instead of opening it later.");
            }
            finally { if (held > 0) coverSlots.Release(held); }

            int backBeforeCancellation = ContentFrame.BackStack.Count;
            held = 0;
            try
            {
                await coverSlots.WaitAsync(); held++;
                await coverSlots.WaitAsync(); held++;
                NavigateShell(new ShellNavigationRequest(albumKey, this, EntityId: album.Id, IsAlbum: true));
                Task abandoned = pagePreparationTask;
                Check(CurrentKey() == "曲库" && albumCommits == 0 && !abandoned.IsCompleted,
                    "An album does not enter while its stable hero image is still queued.");
                await WaitFor(() => PageLoading.Visibility == Visibility.Visible,
                    "Blocked album preparation never displayed progress.");
                NavigateShell(new ShellNavigationRequest("设置", this));
                Check(CurrentKey() == "设置" && PageLoading.Visibility == Visibility.Collapsed && ContentFrame.IsHitTestVisible,
                    "A newer immediate navigation cancels preparation and clears the previous request's loading state.");
                coverSlots.Release(held); held = 0;
                await abandoned.WaitAsync(TimeSpan.FromSeconds(10));
                await EnsureAlbumArtworkAsync(album, tracks).WaitAsync(TimeSpan.FromSeconds(10));
                Check(CurrentKey() == "设置" && albumCommits == 0 && PageLoading.Visibility == Visibility.Collapsed
                    && ContentFrame.IsHitTestVisible && ContentFrame.BackStack.Count == backBeforeCancellation + 1
                    && ContentFrame.BackStack.All(entry => (entry.Parameter as ShellNavigationRequest)?.Key != albumKey),
                    "A canceled album cannot steal the later route, reopen its mask, or insert a phantom history entry.");
            }
            finally { if (held > 0) coverSlots.Release(held); }

            NavigateShell(new ShellNavigationRequest(albumKey, this, EntityId: album.Id, IsAlbum: true));
            await pagePreparationTask.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitFor(() => pageContent.GetValueOrDefault(albumKey) is EntityDetailsView { IsLoaded: true },
                "The prepared album did not attach its native view.");
            var entity = (EntityDetailsView)pageContent[albumKey];
            var rows = entity.SongList.Items.OfType<EntitySongRow>().ToArray();
            Check(albumCommits == 1 && rows.Length == 4 && rows.Select(row => row.Number).SequenceEqual(["2", "1", "—", "—"]),
                "The album keeps its four real song rows and displays actual track positions rather than local row indices.");
            Check(rows.Select(row => row.DiscHeading).SequenceEqual(["Disc 1", "Disc 2", "", "未标注盘号"]),
                "Disc 1, Disc 2 and unknown-disc labels begin their corresponding song sections without fake playable header rows.");
            Check(entity.FindName("AlbumColumnHeader") is Grid { Visibility: Visibility.Visible } columnHeader
                && FindChild<TextBlock>(columnHeader)?.Text == "#"
                && entity.FindName("EntitySongHeading") is TextBlock { Visibility: Visibility.Collapsed },
                "The album's native column header begins with #, while the artist-style heading stays hidden.");
            await WaitFor(() => entity.HeroTitle.ActualHeight > 0 && entity.HeroSubtitle.ActualHeight > 0,
                "Album hero text did not complete native layout.");
            var subtitle = (TextBlock)entity.HeroSubtitle;
            var titleBounds = entity.HeroTitle.TransformToVisual(entity.HeroLayer)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, entity.HeroTitle.ActualWidth, entity.HeroTitle.ActualHeight));
            var subtitleBounds = subtitle.TransformToVisual(entity.HeroLayer)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, subtitle.ActualWidth, subtitle.ActualHeight));
            float expandedTitleY = 0, nativeTitleHeight = 0;
            bool metricsReady = overviewMotions.TryGetValue(entity.SongList, out var motion)
                && motion.Metrics.TryGetScalar("ExpandedTitleY", out expandedTitleY) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded
                && motion.Metrics.TryGetScalar("TitleHeight", out nativeTitleHeight) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded;
            double lineGap = subtitle.Margin.Top - expandedTitleY - nativeTitleHeight;
            Check(subtitle.TextAlignment == TextAlignment.Left && subtitle.HorizontalAlignment == HorizontalAlignment.Left
                && Math.Abs(titleBounds.Left - subtitleBounds.Left) <= 1 && metricsReady && Math.Abs(lineGap - 16) <= 1,
                "The album artist line is left-aligned with the title and sits 16 pixels below its composed line box.");
            diagnostics.Add(new { TitleBounds = titleBounds, SubtitleBounds = subtitleBounds,
                ExpandedTitleY = expandedTitleY, NativeTitleHeight = nativeTitleHeight, LineGap = lineGap,
                Rows = rows.Select(row => new { row.Number, row.DiscNumber, row.TrackNumber, row.DiscHeading }).ToArray() });

            var hero = (Image)entity.FindName("EntityCover");
            var stableImage = hero.Source;
            Check(entity.ArtworkColor == 0xFF28AA55 && ReferenceEquals(OverviewBackdrop.Background, entity.HeroBackground)
                && entity.HeroBackground.GradientStops[0].Color == HeroColor(HeroPalette.Create(overviewDark, 0xFF28AA55).Top),
                "The decoded album image supplies its stable tint before entry, and the backdrop shares that exact gradient.");
            var firstSong = rows[0].Song;
            using (var oldDetailOwner = new CancellationTokenSource())
            {
                held = 0;
                try
                {
                    await coverSlots.WaitAsync(); held++;
                    await coverSlots.WaitAsync(); held++;
                    firstSong.Cover = null;
                    var oldDetailRead = LoadCoverAsync(firstSong, oldDetailOwner.Token);
                    oldDetailOwner.Cancel();
                    var currentDetailRead = LoadCoverAsync(firstSong);
                    coverSlots.Release(held); held = 0;
                    try { await oldDetailRead.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
                    await currentDetailRead.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(firstSong.Cover is BitmapImage,
                        "A canceled detail-image owner releases its queued work and a newer owner still receives the full cover.");
                }
                finally { if (held > 0) coverSlots.Release(held); }
            }
            await using (var stream = File.OpenRead(artworkCache.GetCachePath(alternative)))
            {
                using var source = stream.AsRandomAccessStream();
                var unrelated = new BitmapImage { DecodePixelWidth = 128, DecodePixelHeight = 128 };
                await unrelated.SetSourceAsync(source);
                firstSong.Cover = unrelated;
            }
            RefreshCurrentEntityDetails();
            Check(stableImage is BitmapImage && ReferenceEquals(hero.Source, stableImage)
                && !ReferenceEquals(hero.Source, firstSong.Cover),
                "Loading or changing a song's own full-size cover does not replace an already-painted album hero.");
            Check(GoShellBack() && CurrentKey() == "设置", "Back leaves the prepared album along its existing native history edge.");
            Check(GoShellForward(), "Forward accepts the previously prepared album route.");
            await pagePreparationTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(CurrentKey() == albumKey && albumCommits == 2 && ReferenceEquals(hero.Source, stableImage)
                && PageLoading.Visibility == Visibility.Collapsed && ContentFrame.IsHitTestVisible,
                "Forward prepares the target before committing history and reuses the exact same album bitmap.");
            Check(automaticLookup is null && libraryEnrichment is null && videoLookups.Count == 0,
                "The isolated readiness and album checks start no metadata, lyrics or video provider.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            ContentFrame.Navigated -= OnPreparedNavigation;
            try
            {
                string path = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0, CheckCount = checks.Count, Checks = checks, Failures = failures,
                    LibraryCommits = libraryCommits, AlbumCommits = albumCommits, Diagnostics = diagnostics, StorePath = storePath,
                    ElapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1),
                    Limitations = "Isolated generated images and synthetic file-existence probes; checks native route commit timing, progress ownership, album bindings and text geometry. This does not measure real-provider coverage, physical input, cold-storage latency or animation acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { queueCloseApproved = true; Close(); }
        }
    }
}
