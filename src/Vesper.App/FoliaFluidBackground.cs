using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Vesper.Core;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Vesper.App;

/// <summary>
/// Folia「流光」风格原生流体取色背景：以深色基底叠加四层缓慢漂移呼吸的径向柔光色球，
/// 在小窗舞台（无歌词）与全屏演唱舞台（歌词底层）间复用。
/// </summary>
internal sealed class FoliaFluidBackground : Grid
{
    private readonly Border baseLayer = new();
    private readonly Grid orbStage = new() { IsHitTestVisible = false };
    private readonly Ellipse primaryOrb = CreateOrb(HorizontalAlignment.Left, VerticalAlignment.Top);
    private readonly Ellipse secondaryOrb = CreateOrb(HorizontalAlignment.Right, VerticalAlignment.Bottom);
    private readonly Ellipse accentOrb = CreateOrb(HorizontalAlignment.Right, VerticalAlignment.Top);
    private readonly Ellipse counterOrb = CreateOrb(HorizontalAlignment.Left, VerticalAlignment.Bottom);
    private readonly CompositeTransform primaryTransform = new();
    private readonly CompositeTransform secondaryTransform = new();
    private readonly CompositeTransform accentTransform = new();
    private readonly CompositeTransform counterTransform = new();
    private readonly Border vignetteLayer = new() { IsHitTestVisible = false };
    private readonly RectangleGeometry stageClip = new();

    private Storyboard? fluidStoryboard;
    private uint? lastArgb;
    private bool active;
    private bool animationsRunning;

    public FoliaFluidBackground()
    {
        IsHitTestVisible = false;
        Clip = stageClip;
        Children.Add(baseLayer);

        primaryOrb.RenderTransform = primaryTransform;
        secondaryOrb.RenderTransform = secondaryTransform;
        accentOrb.RenderTransform = accentTransform;
        counterOrb.RenderTransform = counterTransform;

        orbStage.Children.Add(primaryOrb);
        orbStage.Children.Add(secondaryOrb);
        orbStage.Children.Add(accentOrb);
        orbStage.Children.Add(counterOrb);
        Children.Add(orbStage);

        var vignette = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        vignette.GradientStops.Add(new GradientStop { Color = Color.FromArgb(42, 0, 0, 0), Offset = 0 });
        vignette.GradientStops.Add(new GradientStop { Color = Color.FromArgb(14, 0, 0, 0), Offset = 0.45 });
        vignette.GradientStops.Add(new GradientStop { Color = Color.FromArgb(88, 0, 0, 0), Offset = 1 });
        vignetteLayer.Background = vignette;
        Children.Add(vignetteLayer);

        ApplyPaletteColors(null);

        SizeChanged += OnStageSizeChanged;
        Loaded += (_, _) => SyncAnimations();
        Unloaded += (_, _) => StopAnimations();
    }

    internal double StageCornerRadius { get; set; }

    internal void SetPresentation(uint? dominantArgb, bool isActive)
    {
        if (lastArgb != dominantArgb)
        {
            lastArgb = dominantArgb;
            ApplyPaletteColors(dominantArgb);
        }
        if (active != isActive)
        {
            active = isActive;
            SyncAnimations();
        }
    }

    private static Ellipse CreateOrb(HorizontalAlignment hAlign, VerticalAlignment vAlign) => new()
    {
        HorizontalAlignment = hAlign,
        VerticalAlignment = vAlign,
        RenderTransformOrigin = new Point(0.5, 0.5)
    };

    private void ApplyPaletteColors(uint? dominantArgb)
    {
        FluidStagePalette palette = CoverPalette.CreateFluidPalette(dominantArgb);
        baseLayer.Background = new SolidColorBrush(FromArgb(palette.DeepBase));
        primaryOrb.Fill = CreateSoftOrbBrush(palette.PrimaryOrb, 175, 72);
        secondaryOrb.Fill = CreateSoftOrbBrush(palette.SecondaryOrb, 165, 64);
        accentOrb.Fill = CreateSoftOrbBrush(palette.AccentOrb, 145, 52);
        counterOrb.Fill = CreateSoftOrbBrush(BlendColor(palette.PrimaryOrb, palette.AccentOrb), 130, 44);
    }

    private static RadialGradientBrush CreateSoftOrbBrush(uint argb, byte coreAlpha, byte midAlpha)
    {
        Color rgb = FromArgb(argb);
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(coreAlpha, rgb.R, rgb.G, rgb.B), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(midAlpha, rgb.R, rgb.G, rgb.B), Offset = 0.48 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(14, rgb.R, rgb.G, rgb.B), Offset = 0.80 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B), Offset = 1.0 });
        return brush;
    }

    private static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static uint BlendColor(uint first, uint second)
    {
        byte r = (byte)((((byte)(first >> 16)) + ((byte)(second >> 16))) / 2);
        byte g = (byte)((((byte)(first >> 8)) + ((byte)(second >> 8))) / 2);
        byte b = (byte)((((byte)first) + ((byte)second)) / 2);
        return 0xFF000000u | (uint)r << 16 | (uint)g << 8 | b;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        double h = e.NewSize.Height;
        if (w <= 0 || h <= 0) return;

        stageClip.Rect = new Rect(0, 0, w, h);

        double baseDim = Math.Max(w, h);
        LayoutOrb(primaryOrb, baseDim * 0.96, new Thickness(-baseDim * 0.24, -baseDim * 0.28, 0, 0));
        LayoutOrb(secondaryOrb, baseDim * 0.92, new Thickness(0, 0, -baseDim * 0.22, -baseDim * 0.28));
        LayoutOrb(accentOrb, baseDim * 0.78, new Thickness(0, -baseDim * 0.18, -baseDim * 0.14, 0));
        LayoutOrb(counterOrb, baseDim * 0.82, new Thickness(-baseDim * 0.14, 0, 0, -baseDim * 0.20));

        if (active)
        {
            StopAnimations();
            SyncAnimations();
        }
    }

    private static void LayoutOrb(Ellipse orb, double diameter, Thickness margin)
    {
        orb.Width = diameter;
        orb.Height = diameter;
        orb.Margin = margin;
    }

    private void SyncAnimations()
    {
        if (!active || !IsLoaded || ActualWidth < 8 || ActualHeight < 8 || !new UISettings().AnimationsEnabled)
        {
            StopAnimations();
            return;
        }
        if (animationsRunning) return;

        double dx = Math.Max(28, ActualWidth * 0.22);
        double dy = Math.Max(22, ActualHeight * 0.20);
        var storyboard = new Storyboard();

        AddOrbAnimations(storyboard, primaryTransform, 14, 19, dx * 0.95, dy * 0.70, 0.94, 1.14);
        AddOrbAnimations(storyboard, secondaryTransform, 17, 15, -dx * 0.90, -dy * 0.65, 1.12, 0.92);
        AddOrbAnimations(storyboard, accentTransform, 13, 16, -dx * 0.80, dy * 0.80, 0.92, 1.18);
        AddOrbAnimations(storyboard, counterTransform, 16, 14, dx * 0.75, -dy * 0.72, 1.08, 0.93);

        fluidStoryboard = storyboard;
        animationsRunning = true;
        storyboard.Begin();
    }

    private static void AddOrbAnimations(
        Storyboard storyboard,
        CompositeTransform target,
        double txSeconds,
        double tySeconds,
        double toX,
        double toY,
        double fromScale,
        double toScale)
    {
        target.ScaleX = target.ScaleY = fromScale;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        void Add(string property, double from, double to, double seconds)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromSeconds(seconds),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true,
                EasingFunction = ease
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            storyboard.Children.Add(anim);
        }

        Add(nameof(CompositeTransform.TranslateX), 0, toX, txSeconds);
        Add(nameof(CompositeTransform.TranslateY), 0, toY, tySeconds);
        Add(nameof(CompositeTransform.ScaleX), fromScale, toScale, (txSeconds + tySeconds) * 0.45);
        Add(nameof(CompositeTransform.ScaleY), fromScale, toScale, (txSeconds + tySeconds) * 0.52);
    }

    private void StopAnimations()
    {
        if (fluidStoryboard is not null)
        {
            fluidStoryboard.Stop();
            fluidStoryboard = null;
        }
        animationsRunning = false;
        foreach (CompositeTransform t in new[] { primaryTransform, secondaryTransform, accentTransform, counterTransform })
        {
            t.TranslateX = 0;
            t.TranslateY = 0;
            t.ScaleX = 1;
            t.ScaleY = 1;
        }
    }
}
