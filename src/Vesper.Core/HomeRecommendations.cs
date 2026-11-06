namespace Vesper.Core;

// Window/session state, never persisted. Capture the first non-empty available
// library once; filtering or later imports must not reshuffle or refill it.
public sealed class HomeRecommendations(int count = 12, Random? random = null)
{
    private string[]? selected;
    private readonly Random random = random ?? Random.Shared;
    public IReadOnlyList<string> Select(IEnumerable<Song> available)
    {
        if (selected is not null) return selected.ToArray();
        var ids = available.Select(song => song.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];
        random.Shuffle(ids);
        selected = ids.Take(Math.Max(1, count)).ToArray();
        return selected.ToArray();
    }
}
