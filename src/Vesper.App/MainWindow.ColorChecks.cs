using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Isolated brush/layout/pixel regression. The fixture has no library, image
    // readers or providers; artwork colors are controlled inputs to the real views.
    private async Task RunColorChecksAsync(string reportPath)
    {
        canSave = false;
        PreferenceNotice.IsOpen = false;
        var timer = Stopwatch.StartNew();
        var checks = new List<object>();
        var failures = new List<string>();
        var diagnostics = new List<object>();
        var previews = new List<string>();
        int captures = 0;
        void Check(bool condition, string description)
        {
            checks.Add(new { Description = description, Passed = condition });
            if (!condition) failures.Add(description);
        }
        static uint Argb(Windows.UI.Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
        static uint[] Stops(Brush brush) => brush is LinearGradientBrush gradient
            ? gradient.GradientStops.Select(stop => Argb(stop.Color)).ToArray() : [];
        static uint Foreground(TextBlock text) => text.Foreground is SolidColorBrush brush ? Argb(brush.Color) : 0;
        static bool Matches(Brush brush, HeroPaletteColors colors) => brush is LinearGradientBrush gradient
            && gradient.GradientStops.Count == 3
            && Stops(brush).SequenceEqual([colors.Top, colors.Middle, colors.Bottom])
            && gradient.GradientStops[0].Offset == 0 && gradient.GradientStops[^1].Offset == 1;
        async Task Settle(int turns = 16)
        {
            for (int turn = 0; turn < turns; turn++) { await Task.Delay(16); Root.UpdateLayout(); }
        }
        async Task Navigate(string key, EntityDetailsView? fixture = null)
        {
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest(key, this), new SuppressNavigationTransitionInfo());
            if (fixture is not null)
            {
                PageHeading.Visibility = Visibility.Collapsed;
                PageArea.Padding = new Thickness(0);
            }
            await Settle();
            if (fixture is not null) ConfigureOverview(fixture.SongList);
        }
        async Task<(int Width, int Height, byte[] Pixels)> Capture()
        { await Settle(2); captures++; return await CaptureQueueRootAsync(); }
        int[] Pixel((int Width, int Height, byte[] Pixels) capture, FrameworkElement page, Point local)
        {
            var point = page.TransformToVisual(Root).TransformPoint(local);
            int x = (int)Math.Round(point.X * capture.Width / Root.ActualWidth);
            int y = (int)Math.Round(point.Y * capture.Height / Root.ActualHeight);
            if (x < 1 || y < 1 || x + 1 >= capture.Width || y + 1 >= capture.Height)
                throw new InvalidOperationException("The color sample is outside the native root.");
            int[] sum = [0, 0, 0];
            for (int py = y - 1; py <= y + 1; py++)
                for (int px = x - 1; px <= x + 1; px++)
                {
                    int offset = (py * capture.Width + px) * 4;
                    sum[0] += capture.Pixels[offset + 2]; sum[1] += capture.Pixels[offset + 1]; sum[2] += capture.Pixels[offset];
                }
            return sum.Select(value => (int)Math.Round(value / 9d)).ToArray();
        }
        static int Difference(int[] left, int[] right) => Enumerable.Range(0, 3).Max(index => Math.Abs(left[index] - right[index]));
        static int[] Rgb(uint color) => [(byte)(color >> 16), (byte)(color >> 8), (byte)color];
        async Task SavePreview(string name, (int Width, int Height, byte[] Pixels) capture)
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, name + ".png");
            await WriteQueueCheckPngAsync(path, capture.Width, capture.Height, capture.Pixels);
            previews.Add(path);
        }

        try
        {
            if (Environment.GetEnvironmentVariable("VESPER_COLOR_CHECK_PATH") is not { Length: > 0 }
                || viewModel.Library.Songs.Count != 0 || viewModel.Library.Directories.Count != 0)
                throw new InvalidOperationException("Color checks require a fresh isolated mode.");
            var fixtureSongs = Enumerable.Range(0, 72).Select(index =>
                new LibrarySongItem(new Song("color-" + index, "Color fixture track " + index.ToString("D2"), []))
                { MetadataLoaded = true }).ToArray();
            foreach (var song in fixtureSongs) { viewModel.Results.Add(song); viewModel.FavoriteResults.Add(song); }
            const string albumKey = "__color-album", artistKey = "__color-artist";
            var album = CreateEntityDetails(albumKey, "__color-album-id", true);
            var artist = CreateEntityDetails(artistKey, "__color-artist-id", false);
            album.Apply("Color Study", "Fixture artist · 2026", null,
                fixtureSongs.Select((song, index) => new EntitySongRow(song, "", 1, index + 1)).ToArray(), []);
            artist.Apply("Fixture Artist", "", null, fixtureSongs.Select(song => new EntitySongRow(song, "")).ToArray(), []);
            pageContent[albumKey] = album; pageContent[artistKey] = artist;
            uint cool = 0xFF2367CD, warm = 0xFFDD6742;
            foreach (bool dark in new[] { true, false })
            {
                string theme = dark ? "Dark" : "Light";
                Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
                ApplyOverviewTheme(dark);
                var neutral = HeroPalette.Create(dark);
                await Navigate("曲库");
                var neutralBrush = GetOverviewHeroBrush(LibrarySongs);
                Check(Matches(neutralBrush, neutral)
                    && new[] { HomeOverview, FavoriteSongs, CreationOverview }.All(list => ReferenceEquals(GetOverviewHeroBrush(list), neutralBrush)),
                    theme + ": home, library, favorites and Create use one neutral gradient rather than the system accent.");
                Check(ReferenceEquals(OverviewBackdrop.Background, neutralBrush)
                    && PageArea.Background is SolidColorBrush body && Argb(body.Color) == neutral.Bottom,
                    theme + ": the page body and header gradient meet at the same opaque neutral surface.");
                Check(Foreground(LibraryTitleBold) == neutral.Foreground && Foreground(HomeTitleBold) == neutral.Foreground
                    && Foreground(FavoriteTitleBold) == neutral.Foreground && Foreground(CreationTitleBold) == neutral.Foreground,
                    theme + ": every root-page title follows the light/dark readable foreground.");
                var neutralCapture = await Capture();
                int[] neutralTop = Pixel(neutralCapture, LibraryPage, new Point(8, 20));
                Check(Difference(neutralTop, Rgb(neutral.Top)) <= 5,
                    theme + ": the actual library header paints the neutral palette without a retained black/accent overlay.");
                diagnostics.Add(new { Theme = theme, Label = "NeutralHeader", Rgb = neutralTop });
                if (dark) await SavePreview("colors-dark-library", neutralCapture);

                var accent = appliedAccent;
                var accentOption = SystemAccent.IsChecked;
                album.ApplyHeroPalette(dark, cool);
                artist.ApplyHeroPalette(dark, warm);
                await Navigate(albumKey, album);
                overviewMotions[album.SongList].Viewer.ChangeView(null, 0, null, true);
                await Settle(12);
                var coolPalette = HeroPalette.Create(dark, cool);
                Check(Matches(album.HeroBackground, coolPalette) && album.ArtworkColor == cool
                    && Foreground(album.HeroTitle) == coolPalette.Foreground
                    && Foreground((TextBlock)album.HeroSubtitle) == coolPalette.SecondaryForeground,
                    theme + ": an album's controlled artwork hue tints its gradient and uses readable title/subtitle colors.");
                Check(ReferenceEquals(OverviewBackdrop.Background, album.HeroBackground)
                    && ReferenceEquals(GetOverviewHeroBrush(album.SongList), album.HeroBackground),
                    theme + ": the album and the navigation backdrop share the exact gradient instance.");
                Check(appliedAccent == accent && SystemAccent.IsChecked == accentOption,
                    theme + ": artwork tinting preserves the user's control-accent choice.");
                var coloredCapture = await Capture();
                int[] coloredTop = Pixel(coloredCapture, album.PageGrid, new Point(8, 20));
                Check(Difference(coloredTop, Rgb(coolPalette.Top)) <= 5 && Difference(coloredTop, neutralTop) >= 3,
                    theme + ": rendered album pixels contain the subtle artwork tint while remaining near the neutral surface.");
                diagnostics.Add(new { Theme = theme, Label = "AlbumHeader", Rgb = coloredTop });
                await SavePreview(dark ? "colors-dark-album" : "colors-light-album", coloredCapture);

                var brushBeforeScroll = album.HeroBackground;
                var stopsBeforeScroll = Stops(brushBeforeScroll);
                var motion = overviewMotions[album.SongList];
                var originalClip = ElementCompositionPreview.GetElementVisual(album.HeroLayer).Clip;
                motion.Viewer.ChangeView(null, Math.Min(motion.Viewer.ScrollableHeight, album.HeroLayer.Height + 60), null, true);
                await Settle(25);
                Check(motion.Viewer.VerticalOffset > album.HeroLayer.Height - 48
                    && ReferenceEquals(album.HeroBackground, brushBeforeScroll) && Stops(album.HeroBackground).SequenceEqual(stopsBeforeScroll),
                    theme + ": scrolling collapses the header without recreating the artwork palette or gradient.");
                var normal = await Capture();
                album.HeroLayer.Opacity = 0;
                var backdropOnly = await Capture();
                OverviewBackdrop.Visibility = Visibility.Collapsed;
                var bodyOnly = await Capture();
                OverviewBackdrop.Visibility = Visibility.Visible; album.HeroLayer.Opacity = 1;
                int[] top = Pixel(normal, album.PageGrid, new Point(8, 20));
                int[] patch = Pixel(backdropOnly, album.PageGrid, new Point(8, 20));
                Check(Difference(top, patch) <= 4,
                    theme + ": the collapsed header's actual top pixels match its navigation backdrop.");
                Check(Difference(Pixel(normal, album.PageGrid, new Point(8, 72)), Pixel(bodyOnly, album.PageGrid, new Point(8, 72))) <= 3,
                    theme + ": the clipped gradient does not leak below the compact header into list margins.");
                double offset = motion.Viewer.VerticalOffset;
                await Navigate("设置");
                Check(OverviewBackdrop.Visibility == Visibility.Collapsed, theme + ": settings disconnect the overview color layer.");
                await Navigate(albumKey, album); await Settle(8);
                Check(Math.Abs(motion.Viewer.VerticalOffset - offset) <= 1 && ReferenceEquals(album.HeroBackground, brushBeforeScroll)
                    && ReferenceEquals(OverviewBackdrop.Background, brushBeforeScroll)
                    && ReferenceEquals(ElementCompositionPreview.GetElementVisual(album.HeroLayer).Clip, originalClip),
                    theme + ": returning preserves the gradient, its clip and the scrolled viewport.");
                await Navigate(artistKey, artist);
                Check(Matches(artist.HeroBackground, HeroPalette.Create(dark, warm))
                    && ReferenceEquals(OverviewBackdrop.Background, artist.HeroBackground),
                    theme + ": artist pages use their own image hue and a matching backdrop.");
                artist.ApplyHeroPalette(dark, null); SyncOverviewBackground(artist.SongList);
                Check(Matches(artist.HeroBackground, neutral), theme + ": missing artist artwork falls back to the same neutral palette.");
            }
            Check(automaticLookup is null && libraryEnrichment is null && videoLookups.Count == 0,
                "Color checks start no metadata, lyrics, video or whole-library image production.");
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
                    Passed = failures.Count == 0, CheckCount = checks.Count, Checks = checks, Failures = failures,
                    Captures = captures, Diagnostics = diagnostics, Previews = previews,
                    ElapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1),
                    Limitations = "Synthetic native controls and controlled artwork colors; Root pixels use the existing opaque Mica substitute. Checks theme/gradient integration, clipping and return state without real providers, physical input, frame-time measurement or user visual acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { queueCloseApproved = true; Close(); }
        }
    }
}
