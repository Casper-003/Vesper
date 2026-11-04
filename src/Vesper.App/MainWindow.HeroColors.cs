using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vesper.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed class ArtworkSwatch(uint color) { internal readonly uint Color = color; }
    private readonly ConditionalWeakTable<BitmapImage, ArtworkSwatch> artworkSwatches = new();
    private bool overviewDark = true;
    private HeroPaletteColors? appliedHeroPalette;

    internal static Windows.UI.Color HeroColor(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private Brush GetOverviewHeroBrush(ListView list) => entityOverviews.TryGetValue(list, out var entity)
        ? entity.HeroBackground : (Brush)Root.Resources["OverviewNeutralBrush"];

    private void ApplyHeroColors(bool dark)
    {
        overviewDark = dark;
        var palette = HeroPalette.Create(dark);
        if (appliedHeroPalette != palette)
        {
            appliedHeroPalette = palette;
            var brush = (LinearGradientBrush)Root.Resources["OverviewNeutralBrush"];
            brush.GradientStops[0].Color = HeroColor(palette.Top);
            brush.GradientStops[1].Color = HeroColor(palette.Middle);
            brush.GradientStops[2].Color = HeroColor(palette.Bottom);
            PageArea.Background = new SolidColorBrush(HeroColor(palette.Bottom));
            foreach (var title in new[] { HomeTitleBold, LibraryTitleBold, FavoriteTitleBold, CreationTitleBold })
                title.Foreground = new SolidColorBrush(HeroColor(palette.Foreground));
        }
        foreach (var entity in entityOverviews.Values.Distinct()) entity.ApplyHeroPalette(dark, entity.ArtworkColor);
        if (overviewBackdropList is { } list) OverviewBackdrop.Background = GetOverviewHeroBrush(list);
    }

    internal void UpdateEntityHeroColors(EntityDetailsView view, object? cover)
    {
        uint? color = cover is BitmapImage bitmap && artworkSwatches.TryGetValue(bitmap, out var swatch) ? swatch.Color : null;
        view.ApplyHeroPalette(overviewDark, color);
        if (ReferenceEquals(overviewBackdropList, view.SongList)) OverviewBackdrop.Background = view.HeroBackground;
    }

    private void RememberArtworkSwatch(BitmapImage image, uint color)
    {
        if (!artworkSwatches.TryGetValue(image, out _)) artworkSwatches.Add(image, new(color));
    }

    private static Task<uint> ReadArtworkSwatchAsync(byte[] bytes, CancellationToken token) => Task.Run(async () =>
    {
        using var stream = new MemoryStream(bytes).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = 64, ScaledHeight = 64 }, ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(token);
        return CoverPalette.DominantArgb(pixels.DetachPixelData());
    }, token);
}
