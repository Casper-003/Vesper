using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Vesper.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Opt-in native regression. Sources are existence probes; all image bytes are
    // generated into the isolated cache and no details page or provider is opened.
    private async Task RunArtworkChecksAsync(string reportPath)
    {
        canSave = false;
        var timer = Stopwatch.StartNew();
        var failures = new List<string>();
        var checks = new List<object>();
        var diagnostics = new List<object>();
        int renderedImageChecks = 0, reusedSurfaces = 0;
        string? storePath = SearchCheckStorePath();
        void Check(bool passed, string description)
        {
            checks.Add(new { Description = description, Passed = passed });
            if (!passed) failures.Add(description);
        }
        async Task WaitFor(Func<bool> ready, string description)
        {
            for (int i = 0; i < 250; i++)
            {
                Root.UpdateLayout();
                if (ready()) return;
                // Bounded native layout/dispatcher settling, not an engine race test.
                await Task.Delay(20);
            }
            throw new TimeoutException(description);
        }
        SongRowSurface? Surface(LibrarySongItem song)
        {
            if (LibrarySongs.ContainerFromItem(song) is not ListViewItem container) return null;
            return container.ContentTemplateRoot as SongRowSurface
                ?? (container.ContentTemplateRoot is { } root ? FindChild<SongRowSurface>(root) : null);
        }
        Image? RowImage(LibrarySongItem song) => Surface(song) is { } surface ? FindChild<Image>(surface) : null;
        bool Bound(LibrarySongItem song) => song.ListCover is BitmapImage bitmap && bitmap.PixelWidth > 0
            && RowImage(song) is { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 } image
            && ReferenceEquals(image.Source, song.ListCover) && ReferenceEquals(Surface(song)?.DataContext, song);
        (Rect ImageBounds, Rect Viewport, Point Center, bool Inside) SampleGeometry(LibrarySongItem song)
        {
            if (RowImage(song) is not { } image || FindChild<ScrollViewer>(LibrarySongs) is not { } viewer)
                return (default, default, default, false);
            static Rect Intersection(Rect left, Rect right)
            {
                double x = Math.Max(left.Left, right.Left), y = Math.Max(left.Top, right.Top);
                return new Rect(x, y, Math.Max(0, Math.Min(left.Right, right.Right) - x),
                    Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y));
            }
            Rect Bounds(FrameworkElement element) => element.TransformToVisual(Root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Rect viewport = Intersection(Intersection(Bounds(viewer), Bounds(LibrarySongs)),
                Intersection(Bounds(ContentFrame), new Rect(0, 0, Root.ActualWidth, Root.ActualHeight)));
            Rect imageBounds = Bounds(image);
            Point center = image.TransformToVisual(Root).TransformPoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2));
            bool inside = imageBounds.Width > 0 && imageBounds.Height > 0
                && imageBounds.Left >= viewport.Left + 1 && imageBounds.Right <= viewport.Right - 1
                && imageBounds.Top >= viewport.Top + 1 && imageBounds.Bottom <= viewport.Bottom - 1;
            return (imageBounds, viewport, center, inside);
        }
        async Task PublishCover(LibrarySongItem song, Uri url)
        {
            var metadata = viewModel.GetSongMetadata(song.Song.Id);
            if (!await viewModel.SaveOnlineMetadataAsync(song.Song.Id, metadata with { CoverUrl = url.AbsoluteUri }))
                throw new InvalidOperationException("Synthetic cover update did not publish.");
            // Seal the fixture again: this test never asks any online provider.
            var input = viewModel.GetLookupInput(song.Song.Id)!;
            viewModel.Library.Details[song.Song.Id] = input.Details with
            { Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
        }
        async Task WritePng(Uri url, byte red, byte green, byte blue)
        {
            string path = artworkCache.GetCachePath(url);
            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))! + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(reportDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Artwork checks require an isolated cache beside the report.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            byte[] pixels = new byte[32 * 32 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            { pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 32, 32, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var source = stream.AsStreamForRead();
            await using var output = File.Create(path);
            await source.CopyToAsync(output);
        }
        async Task CheckPixel(LibrarySongItem song, byte red, byte green, byte blue, string label)
        {
            await WaitFor(() => Bound(song) && SampleGeometry(song).Inside,
                label + " did not bind a fully visible image inside the list viewport.");
            // Image decoding and composition can finish after the property update.
            bool matched = false;
            int[] observed = [];
            var geometry = SampleGeometry(song);
            for (int attempt = 0; attempt < 12 && !matched; attempt++)
            {
                await Task.Delay(30);
                var capture = await CaptureQueueRootAsync();
                geometry = SampleGeometry(song);
                if (!geometry.Inside) continue;
                Point center = geometry.Center;
                int x = (int)Math.Round(center.X * capture.Width / Root.ActualWidth);
                int y = (int)Math.Round(center.Y * capture.Height / Root.ActualHeight);
                if (x < 0 || y < 0 || x >= capture.Width || y >= capture.Height) continue;
                int offset = (y * capture.Width + x) * 4;
                observed = [capture.Pixels[offset], capture.Pixels[offset + 1], capture.Pixels[offset + 2], capture.Pixels[offset + 3]];
                matched = Math.Abs(observed[0] - blue) <= 5 && Math.Abs(observed[1] - green) <= 5
                    && Math.Abs(observed[2] - red) <= 5 && observed[3] >= 250;
            }
            renderedImageChecks++;
            diagnostics.Add(new { Label = label, SongId = song.Song.Id, Bgra = observed, Matched = matched,
                ImageBounds = new { geometry.ImageBounds.X, geometry.ImageBounds.Y, geometry.ImageBounds.Width, geometry.ImageBounds.Height },
                Viewport = new { geometry.Viewport.X, geometry.Viewport.Y, geometry.Viewport.Width, geometry.Viewport.Height },
                Center = new { geometry.Center.X, geometry.Center.Y }, geometry.Inside });
            Check(geometry.Inside, label + ": the sampled image lies fully inside the actual list viewport.");
            Check(matched, label + ": the visible row paints the expected cached image pixels.");
        }

        try
        {
            if (storePath is null || Environment.GetEnvironmentVariable("VESPER_ARTWORK_CHECK_PATH") is not { Length: > 0 })
                throw new InvalidOperationException("Artwork checks require their isolated store and report environment.");
            var green = new Uri("https://cdn-images.dzcdn.net/vesper-artwork-check-green.png");
            var blue = new Uri("https://cdn-images.dzcdn.net/vesper-artwork-check-blue.png");
            var orange = new Uri("https://cdn-images.dzcdn.net/vesper-artwork-check-orange.png");
            await WritePng(green, 34, 180, 80);
            await WritePng(blue, 40, 90, 210);
            await WritePng(orange, 220, 120, 35);

            string mediaDirectory = Path.Combine(Path.GetDirectoryName(storePath)!, "media");
            Directory.CreateDirectory(mediaDirectory);
            var state = new LibraryState { Directories = [mediaDirectory] };
            for (int i = 0; i < 128; i++)
            {
                string path = Path.Combine(mediaDirectory, $"cover-probe-{i:D3}.mp4");
                await File.WriteAllTextAsync(path, "Synthetic cover existence probe " + new string('x', i));
                string id = $"artwork-runtime-{i:D3}";
                var metadata = new SongMetadata($"Cover track {i:D3}", "Cached Artist", "Cached Album", Lyrics: "Cached lyrics");
                // The library hero is index zero. Keep it unillustrated so its
                // separate full-cover path cannot satisfy the list-only assertion.
                var details = new SongDetails(Local: metadata,
                    Online: i is 0 or 2 ? null : metadata with { CoverUrl = green.AbsoluteUri });
                var input = new SongLookupInput(Path.GetFileName(path), details);
                state.Songs.Add(new(id, metadata.Title, [path]));
                state.Details[id] = details with
                { Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
            }
            await viewModel.Store.SaveAsync(state);
            await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var song in viewModel.Results) song.MetadataLoaded = true;
            Check(!viewModel.ReadOnly && viewModel.Results.Count == 128 && viewModel.Results.All(song => song.ListCover is null),
                "The isolated library begins with complete metadata and disk images, but no decoded covers.");
            var cached = viewModel.FindSong("artwork-runtime-001")!;
            var late = viewModel.FindSong("artwork-runtime-002")!;
            var unvisited = viewModel.FindSong("artwork-runtime-064")!;

            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("曲库", this), new SuppressNavigationTransitionInfo());
            await WaitFor(() => LibrarySongs.IsLoaded && IsSongVisible(cached) && Bound(cached),
                "A metadata-complete visible row never hydrated its cached cover.");
            Check(cached.MetadataLoaded && cached.Cover is null && cached.Thumbnail is BitmapImage { DecodePixelWidth: > 0 and <= 256 }
                && Bound(cached),
                "A metadata-complete visible row binds a small cached thumbnail without opening details or creating a full cover.");
            await CheckPixel(cached, 34, 180, 80, "Initial cached cover");
            Check(IsSongVisible(late) && late.ListCover is null && unvisited.ListCover is null,
                "A missing-URL row remains a placeholder and unvisited rows do not decode their cached images.");
            var lateSurface = Surface(late);
            await PublishCover(late, blue);
            await WaitFor(() => Bound(late), "A cover URL arriving on an already-realized row was not loaded.");
            Check(ReferenceEquals(lateSurface, Surface(late)) && late.Cover is null && Bound(late),
                "A late cover URL hydrates the existing native row, without revisiting or opening details.");
            await CheckPixel(late, 40, 90, 210, "Late cover update");

            // Hold the two real decode/download permits, queue one URL, then
            // change its identity before it is allowed to start reading bytes.
            int held = 0;
            try
            {
                await coverSlots.WaitAsync(); held++;
                await coverSlots.WaitAsync(); held++;
                await PublishCover(late, orange);
                var stale = LoadListCoverAsync(late);
                await PublishCover(late, green);
                Check(late.ListCover is null, "Changing cover identity clears the previously displayed thumbnail immediately.");
                coverSlots.Release(held); held = 0;
                await stale.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (held > 0) coverSlots.Release(held); }
            await WaitFor(() => Bound(late), "The current cover did not replace a stale waiting URL.");
            await CheckPixel(late, 34, 180, 80, "Replacement cover after stale queued read");

            var initialSurfaces = artworkRows.Keys.OfType<SongRowSurface>().ToHashSet();
            var scroll = FindChild<ScrollViewer>(LibrarySongs)
                ?? throw new InvalidOperationException("The library did not create a ScrollViewer.");
            var offscreen = cached;
            held = 0;
            try
            {
                await coverSlots.WaitAsync(); held++;
                await coverSlots.WaitAsync(); held++;
                offscreen.Thumbnail = null;
                var pending = LoadListCoverAsync(offscreen);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await WaitFor(() => !IsSongVisible(offscreen) && IsSongVisible(viewModel.Results[^1]),
                    "Scrolling did not move the queued artwork row outside the visible range.");
                coverSlots.Release(held); held = 0;
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(offscreen.ListCover is null, "A row that scrolls away while waiting does not decode or publish an offscreen bitmap.");
            }
            finally { if (held > 0) coverSlots.Release(held); }
            var last = viewModel.Results[^1];
            await WaitFor(() => Bound(last), "The newly visible end of the library did not load its covers.");
            var visible = viewModel.Results.Where(IsSongVisible).ToArray();
            await WaitFor(() => visible.All(Bound), "A recycled visible row retained an empty or mismatched image binding.");
            reusedSurfaces = visible.Select(Surface).OfType<SongRowSurface>().Count(initialSurfaces.Contains);
            Check(visible.All(song => Bound(song) && ReferenceEquals(RowImage(song)?.Source, song.ListCover)),
                "After scrolling, every visible native row binds its current song's cover rather than a recycled song's image.");
            var pixelCandidates = visible.Where(song => SampleGeometry(song).Inside).ToArray();
            var pixelTarget = pixelCandidates.OrderByDescending(song => Surface(song) is { } surface && initialSurfaces.Contains(surface))
                .ThenBy(song => Math.Abs(SampleGeometry(song).Center.Y
                    - (SampleGeometry(song).Viewport.Top + SampleGeometry(song).Viewport.Height / 2)))
                .FirstOrDefault() ?? throw new InvalidOperationException("No fully visible recycled cover exists for the rendered pixel assertion.");
            await CheckPixel(pixelTarget, 34, 180, 80, "Cover on newly visible recycled row");
            Check(unvisited.ListCover is null && viewModel.Results.Count(song => song.ListCover is not null) < state.Songs.Count / 2,
                "Scrolling and cached artwork arrival hydrate visible rows rather than allocating bitmaps for the whole library.");
            Check(viewModel.Results.All(song => song.Cover is null) && automaticLookup is null && libraryEnrichment is null
                && videoLookups.Count == 0 && pageContent.Values.All(page => page is not SongDetailsView),
                "The cover checks opened no song-details page, online lookup, background enrichment or full-size cover path.");
            diagnostics.Add(new { RealizedArtworkRows = artworkRows.Count, DecodedListCovers = viewModel.Results.Count(song => song.ListCover is not null),
                CachedThumbnails = listCoverCache.Count, ReusedSurfaces = reusedSurfaces });
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            try
            {
                string path = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0, CheckCount = checks.Count, Checks = checks,
                    RenderedImageChecks = renderedImageChecks, ReusedSurfaces = reusedSurfaces,
                    Diagnostics = diagnostics, Failures = failures, StorePath = storePath,
                    ElapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1),
                    Limitations = "Isolated generated PNGs and synthetic existence-only media; native row binding and rendered pixels are checked without desktop automation, real providers, user-library coverage, physical scrolling or animation/performance acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { queueCloseApproved = true; Close(); }
        }
    }
}
