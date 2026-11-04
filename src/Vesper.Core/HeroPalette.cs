namespace Vesper.Core;

public readonly record struct HeroPaletteColors(uint Top, uint Middle, uint Bottom,
    uint Foreground, uint SecondaryForeground);

/// <summary>Quiet artwork hues over a stable neutral surface, independent of the control accent.</summary>
public static class HeroPalette
{
    public static HeroPaletteColors Create(bool dark, uint? artworkArgb = null)
    {
        var neutral = dark
            ? new HeroPaletteColors(0xFF303034, 0xFF29292C, 0xFF242426, 0xFFF4F4F5, 0xFFC2C2C6)
            : new HeroPaletteColors(0xFFE6E4E0, 0xFFEFEEEB, 0xFFF5F4F2, 0xFF202024, 0xFF535359);
        if (artworkArgb is not { } artwork || artwork >> 24 < 128) return neutral;

        // Add only a small chromatic departure from gray. Keeping the source's
        // brightness out of the surface prevents white art from washing out dark
        // headers, and black art from turning a light theme into a dark block.
        return neutral with
        {
            Top = Tint(neutral.Top, artwork, dark ? .09 : .10),
            Middle = Tint(neutral.Middle, artwork, .045)
        };
    }

    private static uint Tint(uint neutral, uint artwork, double amount)
    {
        int red = (byte)(artwork >> 16), green = (byte)(artwork >> 8), blue = (byte)artwork;
        double gray = red * .2126 + green * .7152 + blue * .0722;
        int Channel(int value, int source) => Math.Clamp(
            (int)Math.Round(value + (source - gray) * amount, MidpointRounding.AwayFromZero), 0, 255);
        return 0xFF000000u | (uint)Channel((byte)(neutral >> 16), red) << 16
            | (uint)Channel((byte)(neutral >> 8), green) << 8 | (uint)Channel((byte)neutral, blue);
    }
}
