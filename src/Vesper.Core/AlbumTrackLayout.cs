using System.Globalization;

namespace Vesper.Core;

public sealed record AlbumTrackPosition(int? DiscNumber, int? TrackNumber);
public sealed record AlbumTrackLabel(string Number, string DiscHeading);

/// <summary>Labels already-ordered local release memberships without inventing missing positions.</summary>
public static class AlbumTrackLayout
{
    public static IReadOnlyList<AlbumTrackLabel> Build(IReadOnlyList<AlbumTrackPosition> positions)
    {
        var discs = positions.Select(position => Positive(position.DiscNumber)).ToArray();
        bool multiDisc = discs.Where(disc => disc.HasValue).Distinct().Count() > 1 || discs.Any(disc => disc > 1);
        var labels = new AlbumTrackLabel[positions.Count];
        for (int index = 0; index < positions.Count; index++)
        {
            int? track = Positive(positions[index].TrackNumber);
            string number = track?.ToString(CultureInfo.InvariantCulture) ?? "—";
            string heading = multiDisc && (index == 0 || discs[index] != discs[index - 1])
                ? discs[index] is { } disc ? "Disc " + disc.ToString(CultureInfo.InvariantCulture) : "未标注盘号" : "";
            labels[index] = new(number, heading);
        }
        return labels;
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
