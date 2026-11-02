using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Isolated control regression mode. It never initializes the user's library or opens media.
    private async Task RunOverviewRuntimeChecksAsync()
    {
        string? outputPath = Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH");
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        var clock = Stopwatch.StartNew();
        var failures = new List<string>();
        var samples = new List<OverviewRuntimeSample>();
        int paintedPixelChecks = 0, maximumContainers = 0, hoverChecks = 0;
        double hoverLoopMilliseconds = 0;
        object? layoutFeedback = null, overviewBackground = null;
        try
        {
            viewModel.Results.Clear();
            for (int i = 0; i < 1007; i++)
                viewModel.Results.Add(new(new Song($"runtime-{i:D4}", $"Runtime track {i:D4}", [$"synthetic-{i:D4}.mp4"]))
                { MetadataLoaded = true });
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("曲库", this));
            LibrarySongs.IsHitTestVisible = false;
            await Task.Delay(350);
            Root.UpdateLayout();
            ConfigureOverview(LibrarySongs);
            await Task.Delay(80);
            var scroll = FindChild<ScrollViewer>(LibrarySongs) ?? throw new InvalidOperationException("List ScrollViewer was not created.");
            if (scroll.ViewportHeight <= 0 || LibrarySongs.ActualHeight <= 0)
                throw new InvalidOperationException("List has no finite viewport.");

            double[] fractions = [0, .01, .15, .95, .4, .8, .25, .65, .05, 1, .3, .9, .1, .7, .55, .2, .97, .38, .05, .88, .02, .78, .33, .99];
            for (int step = 0; step < fractions.Length; step++)
            {
                if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Control regression exceeded its 20 second budget.");
                double target = scroll.ScrollableHeight * fractions[step];
                var stepClock = Stopwatch.StartNew();
                scroll.ChangeView(null, target, null, true);
                LibrarySongs.UpdateLayout();
                double synchronousMilliseconds = stepClock.Elapsed.TotalMilliseconds;
                await Task.Delay(40);
                LibrarySongs.UpdateLayout();
                if (LibrarySongs.ItemsPanelRoot is not ItemsStackPanel panel)
                    throw new InvalidOperationException("List no longer uses a virtualizing ItemsStackPanel.");
                var containers = panel.Children.OfType<ListViewItem>()
                    .Where(item => LibrarySongs.IndexFromContainer(item) >= 0).ToArray();
                maximumContainers = Math.Max(maximumContainers, containers.Length);
                var visible = new List<(int Index, SongRowSurface Surface, Point Position)>();
                foreach (var container in containers)
                {
                    int index = LibrarySongs.IndexFromContainer(container);
                    if (container.ContentTemplateRoot is not SongRowSurface surface)
                    { failures.Add($"step {step}: missing row content at index {index}"); continue; }
                    var position = surface.TransformToVisual(LibrarySongs).TransformPoint(default);
                    if (position.Y < 1 || position.Y + surface.ActualHeight >= LibrarySongs.ActualHeight - 1) continue;
                    visible.Add((index, surface, position));
                    if (surface.RowIndex != index)
                        failures.Add($"step {step}: stale row parity/index {surface.RowIndex}, expected {index}");
                    if (!ReferenceEquals(surface.DataContext, viewModel.Results[index]))
                        failures.Add($"step {step}: stale row context at {index}");
                    var expected = RuntimeRowBrush(index, surface.IsHovered);
                    if (!ReferenceEquals(surface.Background, expected))
                        failures.Add($"step {step}: wrong content background at {index}");
                }
                var indices = visible.Select(row => row.Index).Order().ToArray();
                if (indices.Length == 0) failures.Add($"step {step}: no fully visible rows");
                for (int i = 1; i < indices.Length; i++)
                    if (indices[i] != indices[i - 1] + 1) failures.Add($"step {step}: non-contiguous visible indices");
                // Capture both ends and intermediate recycled viewports, before any pointer sweep can repair them.
                if (step % 4 == 0 || step == fractions.Length - 1)
                {
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(LibrarySongs);
                    var buffer = await bitmap.GetPixelsAsync();
                    byte[] pixels = new byte[checked((int)buffer.Length)];
                    using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                    if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || pixels.Length == 0)
                        throw new InvalidOperationException("RenderTargetBitmap returned no rendered pixels.");
                    double sx = bitmap.PixelWidth / LibrarySongs.ActualWidth;
                    double sy = bitmap.PixelHeight / LibrarySongs.ActualHeight;
                    foreach (var row in visible)
                    {
                        int x = (int)((row.Position.X + row.Surface.ActualWidth * .7) * sx);
                        int y = (int)((row.Position.Y + row.Surface.ActualHeight * .5) * sy);
                        if (x < 0 || x >= bitmap.PixelWidth || y < 0 || y >= bitmap.PixelHeight) continue;
                        int offset = (y * bitmap.PixelWidth + x) * 4;
                        var color = ((SolidColorBrush)RuntimeRowBrush(row.Index, row.Surface.IsHovered)).Color;
                        paintedPixelChecks++;
                        if (Math.Abs(pixels[offset] - color.B) > 3 || Math.Abs(pixels[offset + 1] - color.G) > 3
                            || Math.Abs(pixels[offset + 2] - color.R) > 3 || pixels[offset + 3] < 250)
                            failures.Add($"step {step}: rendered background mismatch at {row.Index}: BGRA {pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]}, expected {color.B},{color.G},{color.R},255");
                    }
                }
                samples.Add(new(step, Math.Round(scroll.VerticalOffset, 1), containers.Length, indices.Length,
                    indices.FirstOrDefault(-1), indices.LastOrDefault(-1), Math.Round(synchronousMilliseconds, 3),
                    Math.Round(stepClock.Elapsed.TotalMilliseconds, 3)));
            }
            if (maximumContainers >= 200) failures.Add($"Too many native containers realized: {maximumContainers}/1007.");
            if (paintedPixelChecks == 0) failures.Add("No actual painted row pixels were checked.");

            if (LibrarySongs.ItemsPanelRoot is ItemsStackPanel lastPanel)
            {
                var pair = lastPanel.Children.OfType<ListViewItem>()
                    .Where(item => LibrarySongs.IndexFromContainer(item) >= 0)
                    .Select(item => item.ContentTemplateRoot).OfType<SongRowSurface>().Take(2).ToArray();
                if (pair.Length != 2) failures.Add("Not enough realized row surfaces for hover/recycle regression.");
                else
                {
                    foreach (var surface in pair)
                    {
                        int index = surface.RowIndex;
                        surface.SetHovered(true);
                        if (!surface.IsHovered || !surface.HasVisibleHoverLayer || !ReferenceEquals(surface.ActiveBrush, RuntimeRowBrush(index, true))
                            || !ReferenceEquals(surface.Background, RuntimeRowBrush(index, false))
                            || !Equals(surface.HoverLayerColor, ((SolidColorBrush)RuntimeRowBrush(index, true)).Color))
                            failures.Add("Hover must toggle the composition layer without mutating the XAML stripe.");
                        surface.Prepare(index + 1, RuntimeRowBrush(index + 1, false), RuntimeRowBrush(index + 1, true));
                        if (surface.IsHovered || surface.HasVisibleHoverLayer || !ReferenceEquals(surface.Background, RuntimeRowBrush(index + 1, false)))
                            failures.Add("A recycled hovered row retained its old highlight/background.");
                        surface.Prepare(index, RuntimeRowBrush(index, false), RuntimeRowBrush(index, true));
                        hoverChecks += 2;
                    }
                    pair[0].SetHovered(true); pair[1].SetHovered(true); pair[0].SetHovered(false);
                    if (pair[0].IsHovered || !pair[1].IsHovered || pair[0].HasVisibleHoverLayer || !pair[1].HasVisibleHoverLayer)
                        failures.Add("Entering a new row must immediately clear the previous owner; late exit must not clear the new row.");
                    hoverChecks++;
                    var palette = (SolidColorBrush)RuntimeRowBrush(pair[1].RowIndex, true);
                    var savedColor = palette.Color;
                    palette.Color = Windows.UI.Color.FromArgb(255, 81, 82, 83); pair[1].RefreshHoverPalette();
                    if (!Equals(pair[1].HoverLayerColor, palette.Color)) failures.Add("Composition hover color did not follow palette refresh.");
                    palette.Color = savedColor; pair[1].RefreshHoverPalette(); hoverChecks++;
                    ApplyOverviewTheme(true);
                    var even = ((SolidColorBrush)Root.Resources["OverviewEvenBrush"]).Color;
                    var evenHover = ((SolidColorBrush)Root.Resources["OverviewEvenHoverBrush"]).Color;
                    var odd = ((SolidColorBrush)Root.Resources["OverviewOddBrush"]).Color;
                    var oddHover = ((SolidColorBrush)Root.Resources["OverviewOddHoverBrush"]).Color;
                    if (evenHover.R <= even.R || oddHover.R >= odd.R) failures.Add("Dark palette hover must brighten light rows and darken dark rows.");
                    ApplyOverviewTheme(false);
                    even = ((SolidColorBrush)Root.Resources["OverviewEvenBrush"]).Color;
                    evenHover = ((SolidColorBrush)Root.Resources["OverviewEvenHoverBrush"]).Color;
                    odd = ((SolidColorBrush)Root.Resources["OverviewOddBrush"]).Color;
                    oddHover = ((SolidColorBrush)Root.Resources["OverviewOddHoverBrush"]).Color;
                    if (evenHover.R <= even.R || oddHover.R >= odd.R) failures.Add("Light palette hover must brighten light rows and darken dark rows.");
                    hoverChecks += 2;
                    var hoverClock = Stopwatch.StartNew();
                    for (int i = 0; i < 1000; i++)
                    { pair[i % 2].SetHovered(true); pair[(i + 1) % 2].SetHovered(false); }
                    hoverLoopMilliseconds = hoverClock.Elapsed.TotalMilliseconds;
                    foreach (var surface in pair) surface.SetHovered(false);
                }
            }
            layoutFeedback = await CheckOverviewFeedbackAsync(failures);
            overviewBackground = await CheckOverviewBackgroundAsync(failures);
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            try
            {
                var report = new
                {
                    Passed = failures.Count == 0, SongCount = 1007, ViewportJumps = samples.Count,
                    MaximumRealizedContainers = maximumContainers, PaintedPixelChecks = paintedPixelChecks,
                    HoverAndRecycleChecks = hoverChecks, ThousandHoverStateChangesMilliseconds = Math.Round(hoverLoopMilliseconds, 3),
                    LayoutFeedback = layoutFeedback, OverviewBackground = overviewBackground,
                    ElapsedMilliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 1), Samples = samples, Failures = failures,
                    Limitations = "Isolated synthetic controls; programmatic ChangeView/layout and direct hover-state calls, not physical pointer timing, media IO, display capture or visual acceptance. RenderTargetBitmap checks row stripes and overview background patches using a test-only opaque Mica substitute; composition hover checks verify layer state/color, not rendered hover pixels."
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { Close(); }
        }
    }
    private Brush RuntimeRowBrush(int index, bool hovered) => (Brush)Root.Resources[
        hovered ? index % 2 == 0 ? "OverviewEvenHoverBrush" : "OverviewOddHoverBrush"
            : index % 2 == 0 ? "OverviewEvenBrush" : "OverviewOddBrush"];
    private sealed record OverviewRuntimeSample(int Step, double Offset, int RealizedContainers, int VisibleRows,
        int FirstIndex, int LastIndex, double SynchronousLayoutMilliseconds, double TotalStepMilliseconds);
}
