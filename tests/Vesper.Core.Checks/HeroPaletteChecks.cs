using Vesper.Core;

internal static class HeroPaletteChecks
{
    public static void Run(Action<bool, string> check)
    {
        var dark = HeroPalette.Create(true);
        var light = HeroPalette.Create(false);
        check(dark is { Top: 0xFF303034, Middle: 0xFF29292C, Bottom: 0xFF242426 }
            && light is { Top: 0xFFE6E4E0, Middle: 0xFFEFEEEB, Bottom: 0xFFF5F4F2 },
            "无封面时页头使用稳定石墨灰或暖灰，渐变终点与正文中性底色一致");

        uint[] grayscale = [0xFF000000, 0xFF404040, 0xFF808080, 0xFFC0C0C0, 0xFFFFFFFF];
        check(grayscale.All(color => HeroPalette.Create(true, color) == dark && HeroPalette.Create(false, color) == light)
            && HeroPalette.Create(true, 0x00FF0000) == dark && HeroPalette.Create(false, 0x7F00FF00) == light,
            "黑白灰封面和不可见颜色保留中性底色，不将封面亮度直接变成刺眼或过暗的大背景");

        var red = HeroPalette.Create(true, 0xFFFF0000);
        var green = HeroPalette.Create(true, 0xFF00FF00);
        var blue = HeroPalette.Create(true, 0xFF0000FF);
        check(Red(red.Top) > Green(red.Top) && Red(red.Top) > Blue(red.Top)
            && Green(green.Top) > Red(green.Top) && Green(green.Top) > Blue(green.Top)
            && Blue(blue.Top) > Red(blue.Top) && Blue(blue.Top) > Green(blue.Top)
            && new[] { red.Top, green.Top, blue.Top }.Distinct().Count() == 3,
            "页头保留封面的红绿蓝色相辨识，使用柔和色调而不是统一主题强调色");

        bool opaque = true, restrained = true, readable = true, stableBodyAndText = true, deterministic = true;
        double lowestContrast = double.MaxValue;
        // Exercise chromatic extremes and intermediate hues, then sample both
        // actual gradient segments rather than checking their endpoints alone.
        int[] channels = [0, 32, 64, 96, 128, 160, 192, 224, 255];
        foreach (bool isDark in new[] { true, false })
        foreach (int r in channels)
        foreach (int g in channels)
        foreach (int b in channels)
        {
            uint artwork = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;
            var palette = HeroPalette.Create(isDark, artwork);
            var neutral = isDark ? dark : light;
            uint[] stops = [palette.Top, palette.Middle, palette.Bottom];
            opaque &= stops.Append(palette.Foreground).Append(palette.SecondaryForeground).All(color => color >> 24 == 255);
            restrained &= stops.All(color => Spread(color) <= (isDark ? 30 : 35))
                && stops.All(color => isDark ? Luminance(color) < .08 : Luminance(color) > .65);
            stableBodyAndText &= palette.Bottom == neutral.Bottom && palette.Foreground == neutral.Foreground
                && palette.SecondaryForeground == neutral.SecondaryForeground;
            deterministic &= HeroPalette.Create(isDark, artwork) == palette;
            for (int segment = 0; segment < stops.Length - 1; segment++)
            for (int step = 0; step <= 10; step++)
            {
                uint sample = Interpolate(stops[segment], stops[segment + 1], step / 10d);
                double contrast = Math.Min(Contrast(palette.Foreground, sample), Contrast(palette.SecondaryForeground, sample));
                lowestContrast = Math.Min(lowestContrast, contrast);
                readable &= contrast >= 4.5;
            }
        }
        check(opaque && restrained,
            "深浅主题在饱和与中间色封面下均生成不透明、低饱和且亮度受控的背景");
        check(readable && lowestContrast >= 4.5,
            "主副标题在全部抽样封面及两段渐变中保持至少4.5比1的文字对比度");
        check(stableBodyAndText && deterministic,
            "同一封面生成相同配色，封面色相不改变正文底色或主题文字色，避免背景随刷新漂移");
    }

    private static int Red(uint color) => (byte)(color >> 16);
    private static int Green(uint color) => (byte)(color >> 8);
    private static int Blue(uint color) => (byte)color;
    private static int Spread(uint color) => Math.Max(Red(color), Math.Max(Green(color), Blue(color)))
        - Math.Min(Red(color), Math.Min(Green(color), Blue(color)));
    private static double Luminance(uint color)
    {
        static double Linear(int value)
        {
            double channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(Red(color)) + .7152 * Linear(Green(color)) + .0722 * Linear(Blue(color));
    }
    private static double Contrast(uint text, uint background)
    {
        double foreground = Luminance(text), surface = Luminance(background);
        return (Math.Max(foreground, surface) + .05) / (Math.Min(foreground, surface) + .05);
    }
    private static uint Interpolate(uint first, uint second, double progress)
    {
        static int Blend(int a, int b, double p) => (int)Math.Round(a + (b - a) * p);
        return 0xFF000000u | (uint)Blend(Red(first), Red(second), progress) << 16
            | (uint)Blend(Green(first), Green(second), progress) << 8 | (uint)Blend(Blue(first), Blue(second), progress);
    }
}
