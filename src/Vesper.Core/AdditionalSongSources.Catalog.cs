namespace Vesper.Core;

public sealed partial class AdditionalSongSources
{
    private async Task<IReadOnlyList<SongMatch>> SearchDeezer(string title, CancellationToken token)
    {
        using var json = await GetAsync("https://api.deezer.com/search?limit=20&q=" + Uri.EscapeDataString(title), token);
        if (!json.RootElement.TryGetProperty("data", out var tracks)) throw new HttpRequestException("备用资料源暂不可用。");
        var result = new List<SongMatch>();
        foreach (var track in tracks.EnumerateArray())
        {
            string name = MusicBrainzClient.Text(track, "title");
            if (!SongNameMatching.Same(name, title)) continue;
            var album = track.GetProperty("album");
            result.Add(new("deezer:" + track.GetProperty("id"), album.GetProperty("id").ToString(), name,
                MusicBrainzClient.Text(track.GetProperty("artist"), "name"), MusicBrainzClient.Text(album, "title"), "", "",
                CoverUrl: MusicBrainzClient.Text(album, "cover_xl")));
        }
        return result;
    }

    private async Task<IReadOnlyList<LyricsCandidate>> SearchKugou(string title, string artistHint, CancellationToken token)
    {
        string artist = artistHint;
        double duration = 0;
        try
        {
            var entries = await Entries(title, token);
            var group = entries.Where(entry => artistHint.Length == 0 || SongNameMatching.SameArtist(entry.Match.Artist, artistHint))
                .GroupBy(entry => SongNameMatching.Normalize(entry.Match.Artist)).OrderByDescending(group => group.Count()).FirstOrDefault();
            var entry = group?.GroupBy(item => Math.Round(item.Duration / 5)).OrderByDescending(items => items.Count()).First().First();
            if (entry is not null) { artist = entry.Match.Artist; duration = entry.Duration; }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            || ex is OperationCanceledException && !token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        string query = artist.Length > 0 ? artist + " - " + title : title;
        string durationQuery = duration > 0 ? "&duration=" + (long)(duration * 1000) : "";
        using var json = await GetAsync("https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + Uri.EscapeDataString(query) + durationQuery, token);
        if (!json.RootElement.TryGetProperty("candidates", out var candidates)) throw new HttpRequestException("备用歌词源暂不可用。");
        var matches = candidates.EnumerateArray().Where(candidate =>
            SongNameMatching.Same(MusicBrainzClient.Text(candidate, "song"), title)
            && !SongNameMatching.Same(MusicBrainzClient.Text(candidate, "singer"), title)
            && (artist.Length == 0 || SongNameMatching.SameArtist(MusicBrainzClient.Text(candidate, "singer"), artist)))
            .OrderBy(candidate => duration > 0 ? Math.Abs(candidate.GetProperty("duration").GetDouble() / 1000 - duration) : 0).Take(2);
        var result = new List<LyricsCandidate>();
        foreach (var candidate in matches)
        {
            double seconds = candidate.GetProperty("duration").GetDouble() / 1000;
            if (duration > 0 && Math.Abs(seconds - duration) > 5) continue;
            string id = candidate.GetProperty("id").ToString();
            if (!long.TryParse(id, out long lyricId) || lyricId <= 0) continue;
            string access = MusicBrainzClient.Text(candidate, "accesskey");
            using var download = await GetAsync("https://lyrics.kugou.com/download?ver=1&client=pc&fmt=lrc&charset=utf8&id=" + id
                + "&accesskey=" + Uri.EscapeDataString(access), token);
            string encoded = MusicBrainzClient.Text(download.RootElement, "content");
            if (encoded.Length == 0) continue;
            string text;
            try { text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim('\uFEFF'); }
            catch (FormatException) { continue; }
            if (text.Contains('\0') || !text.Contains('[')) continue;
            result.Add(new(lyricId, title, MusicBrainzClient.Text(candidate, "singer"), "", seconds, "", text,
                SourceUrl: "https://www.kugou.com/"));
        }
        return result;
    }
}
