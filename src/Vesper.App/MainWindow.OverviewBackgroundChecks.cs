using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private async Task<object> CheckOverviewBackgroundAsync(List<string> failures)
    {
        if (Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is not { Length: > 0 })
            throw new InvalidOperationException("Background checks require isolated runtime mode.");
        var originalRows = viewModel.Results.ToArray();
        var samples = new List<object>();
        int checks = 0;
        void Check(bool condition, string message)
        { checks++; if (!condition) failures.Add("Overview background: " + message); }
        void Navigate(string key) => ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest(key, this), new EntranceNavigationTransitionInfo());
        async Task<int[]> Sample(Point point, string label, Stopwatch clock)
        {
            double started = clock.Elapsed.TotalMilliseconds;
            var capture = await CaptureQueueRootAsync();
            int x = (int)Math.Round(point.X * capture.Width / Root.ActualWidth);
            int y = (int)Math.Round(point.Y * capture.Height / Root.ActualHeight);
            if (x < 1 || y < 1 || x + 1 >= capture.Width || y + 1 >= capture.Height)
                throw new InvalidOperationException("Background sample lies outside Root.");
            int[] sum = new int[3];
            for (int py = y - 1; py <= y + 1; py++)
                for (int px = x - 1; px <= x + 1; px++)
                {
                    int index = (py * capture.Width + px) * 4;
                    sum[0] += capture.Pixels[index + 2]; sum[1] += capture.Pixels[index + 1]; sum[2] += capture.Pixels[index];
                }
            int[] rgb = sum.Select(value => (int)Math.Round(value / 9d)).ToArray();
            samples.Add(new { Label = label, CaptureStartMilliseconds = Math.Round(started, 2),
                CaptureEndMilliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 2), Rgb = rgb,
                OverviewBackdrop.ActualWidth, OverviewBackdrop.ActualHeight,
                Visibility = OverviewBackdrop.Visibility.ToString() });
            return rgb;
        }
        try
        {
            for (int i = 0; i < 60; i++)
                viewModel.Results.Add(new(new Song($"backdrop-{i}", $"Background check {i}", [])) { MetadataLoaded = true });
            Navigate("首页"); await Task.Delay(520); Root.UpdateLayout(); ConfigureOverview(HomeOverview);
            var clock = Stopwatch.StartNew();
            Point top = HomePage.TransformToVisual(Root).TransformPoint(new Point(200, 20));
            int[] steady = await Sample(top, "HomeSteady", clock);
            foreach (string key in new[] { "曲库", "收藏", "首页" })
            {
                clock.Restart(); Navigate(key);
                foreach (int due in new[] { 0, 70, 160, 300, 450 })
                {
                    double remaining = due - clock.Elapsed.TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                    int[] rgb = await Sample(top, $"{key}@{due}ms", clock);
                    Check(Enumerable.Range(0, 3).All(channel => Math.Abs(rgb[channel] - steady[channel]) <= 12),
                        $"native entrance exposed a different upper background on {key} ({string.Join(",", rgb)} vs {string.Join(",", steady)}).");
                }
            }

            Navigate("曲库"); await Task.Delay(520); Root.UpdateLayout(); ConfigureOverview(LibrarySongs);
            var motion = overviewMotions[LibrarySongs];
            var heroClip = ElementCompositionPreview.GetElementVisual(LibraryHeroLayer).Clip;
            // The title uses XAML facade Scale/Translation; asking for its
            // handout visual is forbidden while those animations are active.
            var titleMotion = LibraryTitleMotion;
            var title = LibraryTitleBold;
            motion.Viewer.ChangeView(null, Math.Min(motion.Viewer.ScrollableHeight, LibraryHeroLayer.Height + 80), null, true);
            await Task.Delay(100);
            Check(motion.Viewer.VerticalOffset > LibraryHeroLayer.Height - 48,
                "the compact-header fixture must actually scroll past the collapse point.");
            Check(OverviewBackdrop.Visibility == Visibility.Visible && ReferenceEquals(overviewBackdropList, LibrarySongs)
                && Math.Abs(OverviewBackdrop.Height - LibraryHeroLayer.Height) < .5
                && Math.Abs(OverviewBackdrop.ActualHeight - LibraryHeroLayer.ActualHeight) < .5,
                "the background must follow the active header's bound height.");
            Check(overviewBackdropClip is not null && !ReferenceEquals(overviewBackdropClip, heroClip)
                && ReferenceEquals(overviewBackdropSourceClip, heroClip)
                && ReferenceEquals(ElementCompositionPreview.GetElementVisual(OverviewBackdrop).Clip, overviewBackdropClip),
                "the background must read the original header clip through its own clip.");
            // This left margin is below the 48-unit compact header, outside all
            // song rows. Real pixels must be unchanged when the backdrop is hidden.
            Point belowHeader = LibraryPage.TransformToVisual(Root).TransformPoint(new Point(8, 64));
            int[] visible = await Sample(belowHeader, "CompactBelowHeaderVisible", clock);
            int[] hidden;
            var visibility = OverviewBackdrop.Visibility;
            try { OverviewBackdrop.Visibility = Visibility.Collapsed; hidden = await Sample(belowHeader, "CompactBelowHeaderHidden", clock); }
            finally { OverviewBackdrop.Visibility = visibility; }
            Check(Enumerable.Range(0, 3).All(channel => Math.Abs(visible[channel] - hidden[channel]) <= 3),
                "the compact background leaked into the song-list margin below the header.");

            Navigate("设置");
            Check(OverviewBackdrop.Visibility == Visibility.Collapsed && overviewBackdropList is null && overviewBackdropSourceClip is null,
                "non-overview content must hide and disconnect the background.");
            // Exercise the actual navigation-selection path; do not substitute
            // Entrance for the already accepted Create suppression policy.
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => (string)item.Tag == "创作");
            await Task.Delay(280);
            Check((ContentFrame.Content as ShellPage)?.PageKey == "创作" && !queueSidebarVisible
                && !queueSidebarAnimating && QueueSidebarSlot.Visibility == Visibility.Collapsed,
                "the existing Create navigation/sidebar behavior must remain intact.");
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => (string)item.Tag == "曲库");
            await Task.Delay(280);
            Check(ReferenceEquals(overviewMotions[LibrarySongs], motion)
                && ReferenceEquals(ElementCompositionPreview.GetElementVisual(LibraryHeroLayer).Clip, heroClip)
                && ReferenceEquals(LibraryTitleMotion, titleMotion) && ReferenceEquals(LibraryTitleBold, title)
                && queueSidebarVisible && !queuePanelOpen,
                "returning from Create must preserve the original header/title objects and ordinary sidebar.");
        }
        finally
        {
            viewModel.Results.Clear();
            foreach (var row in originalRows) viewModel.Results.Add(row);
        }
        return new { Checks = checks, Theme = Root.ActualTheme.ToString(), Samples = samples,
            Note = "Unmodified system-accent binding; 3x3 Root render samples at actual reported times. Native Mica uses the existing opaque test base. RenderTargetBitmap and object checks do not replace user visual acceptance; Create's exact Suppress selection is also checked by source review." };
    }
}
