using System.Text.Json;

namespace Vesper.Core;

public sealed partial class AdditionalSongSources(HttpClient http, MusicBrainzClient musicBrainz, LrcLibClient lrcLib,
    TimeProvider? timeProvider = null)
{
    private sealed record Entry(SongMatch Match, long Id, double Duration);
    private sealed record CachedEntries(List<Entry> Rows, DateTimeOffset ExpiresAt);
    private sealed class CachedSearch(Func<Task<CachedEntries>> load)
    {
        internal CachedEntries? Completed;
        internal long LastAccess;
        internal readonly Lazy<Task<CachedEntries>> Work = new(() =>
        {
            var task = load();
            // A caller may cancel its wait while the shared request finishes.
            // Observe a later fault even when there are no remaining waiters.
            _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    private readonly object searchCacheGate = new();
    private readonly Dictionary<string, CachedSearch> searches = new(StringComparer.Ordinal);
    private long searchCacheAccess;
    private const int SearchCacheCapacity = 64;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan searchCacheLifetime = TimeSpan.FromMinutes(15);
    private async Task<JsonDocument> GetAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (request.RequestUri!.Host == "music.163.com") request.Headers.Referrer = new("https://music.163.com/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Vesper/0.1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ArtistProfileClient.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
    }
    private async Task<List<Entry>> SearchNetease(string title, CancellationToken token)
    {
        using var json = await GetAsync("https://music.163.com/api/cloudsearch/pc?s=" + Uri.EscapeDataString(title) + "&type=1&limit=20", token);
        if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new HttpRequestException("备用歌曲源暂不可用。");
        var entries = new List<Entry>();
        if (!result.TryGetProperty("songs", out var songs)) return entries;
        foreach (var song in songs.EnumerateArray())
        {
            long id = song.GetProperty("id").GetInt64();
            string name = MusicBrainzClient.Text(song, "name");
            if (!SongNameMatching.Same(name, title)) continue;
            string artist = string.Join(" / ", song.GetProperty("ar").EnumerateArray().Select(a => MusicBrainzClient.Text(a, "name")));
            var album = song.GetProperty("al");
            string cover = MusicBrainzClient.Text(album, "picUrl").Replace("http://", "https://");
            long published = song.TryGetProperty("publishTime", out var publishedAt) && publishedAt.TryGetInt64(out long date) ? date : 0;
            string year = published > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(published).Year.ToString() : "";
            entries.Add(new(new("netease:" + id, album.GetProperty("id").ToString(), name, artist,
                MusicBrainzClient.Text(album, "name"), year, "", CoverUrl: cover), id, song.GetProperty("dt").GetDouble() / 1000));
        }
        return entries;
    }
    private async Task<List<Entry>> Entries(string title, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CachedSearch search;
            lock (searchCacheGate)
            {
                PruneSearchesLocked(clock.GetUtcNow());
                if (!searches.TryGetValue(title, out search!))
                    searches[title] = search = CreateSearch(title);
                search.LastAccess = ++searchCacheAccess;
            }
            // Each waiter owns only its wait. The shared request has GetAsync's
            // bounded timeout and is not cancelled by an unrelated caller.
            var cached = await search.Work.Value.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (clock.GetUtcNow() < cached.ExpiresAt) return cached.Rows;
            RemoveSearch(title, search);
        }
    }

    private CachedSearch CreateSearch(string title)
    {
        CachedSearch? search = null;
        search = new(async () =>
        {
            try
            {
                var rows = await SearchNetease(title, CancellationToken.None);
                var cached = new CachedEntries(rows, clock.GetUtcNow() + searchCacheLifetime);
                lock (searchCacheGate)
                {
                    // Record completion before publishing the task result so even
                    // a request with no remaining waiters obeys the memory bound.
                    search!.Completed = cached;
                    search.LastAccess = ++searchCacheAccess;
                    PruneSearchesLocked(clock.GetUtcNow());
                }
                return cached;
            }
            catch
            {
                // Evict faults even if every waiter has already cancelled, and
                // never let an old failure remove a newer retry's cache entry.
                RemoveSearch(title, search!);
                throw;
            }
        });
        return search;
    }

    private void RemoveSearch(string title, CachedSearch search)
    {
        lock (searchCacheGate)
            if (searches.TryGetValue(title, out var current) && ReferenceEquals(current, search)) searches.Remove(title);
    }

    private void PruneSearchesLocked(DateTimeOffset now)
    {
        foreach (string title in searches.Where(pair => pair.Value.Completed is { } cached && cached.ExpiresAt <= now)
            .Select(pair => pair.Key).ToArray()) searches.Remove(title);
        // Active shared requests are never evicted: another waiter must join the
        // same bounded request instead of duplicating network work or its rows.
        var completed = searches.Where(pair => pair.Value.Completed is not null).ToArray();
        foreach (var entry in completed.OrderBy(pair => pair.Value.LastAccess).Take(Math.Max(0, completed.Length - SearchCacheCapacity)))
            searches.Remove(entry.Key);
    }

    private static async Task<IReadOnlyList<T>> Combine<T>(CancellationToken token, params Func<Task<IReadOnlyList<T>>>[] sources)
    {
        token.ThrowIfCancellationRequested();
        var outcomes = await Task.WhenAll(sources.Select(async source =>
        {
            try { return (Rows: await source(), Error: (Exception?)null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException
                or InvalidOperationException or KeyNotFoundException or FormatException)
            { return (Rows: (IReadOnlyList<T>)Array.Empty<T>(), Error: ex); }
        })).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        var rows = outcomes.SelectMany(result => result.Rows).ToArray();
        if (rows.Length == 0 && outcomes.Any(result => result.Error is not null)) throw new HttpRequestException("检索来源暂不可用。");
        return rows;
    }
    public Task<IReadOnlyList<SongMatch>> SearchMetadata(string title, CancellationToken token) => Combine<SongMatch>(token,
        () => musicBrainz.SearchAsync(title, "", token), async () => (await Entries(title, token)).Select(entry => entry.Match).ToList(),
        () => SearchDeezer(title, token));
    public Task<SongMetadata> GetMetadata(SongMatch match, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return match.RecordingId.StartsWith("deezer:")
        ? Task.FromResult(new SongMetadata(match.Title, match.Artist, match.Album,
            SourceUrl: "https://www.deezer.com/track/" + match.RecordingId[7..], CoverUrl: match.CoverUrl)
            { ArtistCredits = match.ArtistCredits })
        : match.RecordingId.StartsWith("netease:")
        ? Task.FromResult(new SongMetadata(match.Title, match.Artist, match.Album, Year: match.Year,
            SourceUrl: "https://music.163.com/song?id=" + match.RecordingId[8..], CoverUrl: match.CoverUrl))
        : musicBrainz.GetMetadataAsync(match, token);
    }
    public Task<IReadOnlyList<LyricsCandidate>> SearchLyricsWithArtist(string title, string artist, CancellationToken token) => SearchLyrics(title, token, artist);
    public Task<IReadOnlyList<LyricsCandidate>> SearchLyrics(string title, CancellationToken token, string artistHint = "") => Combine<LyricsCandidate>(token,
        () => lrcLib.SearchAsync(title, token), async () =>
        {
            var entries = await Entries(title, token);
            var lyrics = new List<LyricsCandidate>();
            foreach (var entry in entries.Where(entry => artistHint.Length == 0 || SongNameMatching.SameArtist(entry.Match.Artist, artistHint)).Take(3))
            {
                using var json = await GetAsync($"https://music.163.com/api/song/lyric?id={entry.Id}&lv=-1&kv=-1&tv=-1", token);
                if (!json.RootElement.TryGetProperty("lrc", out var lrc)) continue;
                string text = MusicBrainzClient.Text(lrc, "lyric");
                if (text.Length > 0) lyrics.Add(new(entry.Id, entry.Match.Title, entry.Match.Artist, entry.Match.Album,
                    entry.Duration, "", text, SourceUrl: "https://music.163.com/song?id=" + entry.Id));
            }
            return lyrics;
        }, () => SearchKugou(title, artistHint, token));
}
