using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vesper.Core;

public sealed record LyricsCandidate(long Id, string Title, string Artist, string Album, double DurationSeconds,
    string PlainText, string SyncedLyrics = "", string WordLyricsFile = "", bool HasWordSync = false, string SourceUrl = "");

public sealed record SongLyrics(string PlainText, string SyncedLyrics, string WordLyricsFile, bool HasWordSync,
    string SourceUrl, string Title, string Artist, double DurationSeconds, bool TimingCompatible);

public sealed class LrcLibClient(HttpClient client)
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static DateTimeOffset nextRequest;
    private static DateTimeOffset retryAfter;

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(string title, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        await gate.WaitAsync(token);
        try
        {
            if (DateTimeOffset.UtcNow < retryAfter) throw new HttpRequestException("歌词服务正在等待重试。");
            var wait = nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(750);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title));
            request.Headers.UserAgent.ParseAdd("Vesper/0.1 (+https://github.com/Casper-003/Vesper)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var after = response.Headers.RetryAfter;
                var until = after?.Date ?? DateTimeOffset.UtcNow.Add(after?.Delta ?? TimeSpan.FromSeconds(30));
                retryAfter = until.AddMilliseconds(Random.Shared.Next(100, 500));
            }
            response.EnsureSuccessStatusCode();
            using var json = await ArtistProfileClient.ReadJsonAsync(response, token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("歌词响应格式无效。");
            var results = new List<LyricsCandidate>();
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var id) || !id.TryGetInt64(out long recordId) || recordId <= 0) continue;
                string plain = MusicBrainzClient.Text(entry, "plainLyrics");
                string synced = MusicBrainzClient.Text(entry, "syncedLyrics");
                string words = MusicBrainzClient.Text(entry, "lyricsfile");
                if (plain.Length == 0 && synced.Length == 0) continue;
                double duration = entry.TryGetProperty("duration", out var length) && length.TryGetDouble(out double seconds) ? seconds : 0;
                bool hasWords = entry.TryGetProperty("hasWordSync", out var wordSync) && wordSync.ValueKind == JsonValueKind.True;
                results.Add(new(recordId, MusicBrainzClient.Text(entry, "trackName"), MusicBrainzClient.Text(entry, "artistName"),
                    MusicBrainzClient.Text(entry, "albumName"), duration, plain, synced, words, hasWords));
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public static LyricsCandidate? Choose(IEnumerable<LyricsCandidate> candidates, IReadOnlyList<SongNameQuery> queries,
        string artist, string album, double duration)
    {
        var exact = candidates.Where(candidate => queries.Any(query => SongNameMatching.Same(query.Title, candidate.Title)))
            .Where(candidate => artist.Length == 0 || SongNameMatching.SameArtist(candidate.Artist, artist)).ToList();
        if (exact.Count == 0) return null;
        if (artist.Length == 0)
        {
            var hinted = exact.Where(candidate => queries.Any(query => query.ArtistHint.Length > 0
                && SongNameMatching.SameArtist(candidate.Artist, query.ArtistHint))).ToList();
            if (hinted.Count > 0) exact = hinted;
            var groups = exact.GroupBy(candidate => SongNameMatching.Normalize(candidate.Artist)).OrderByDescending(group => group.Count()).ToList();
            if (groups.Count > 1 && (groups[0].Count() < 3 || groups[0].Count() < exact.Count * .8)) return null;
            exact = groups[0].ToList();
        }
        return exact.OrderBy(candidate => duration > 0 ? Math.Abs(candidate.DurationSeconds - duration) : 0)
            .ThenBy(candidate => album.Length > 0 && !SongNameMatching.Same(candidate.Album, album))
            .ThenByDescending(candidate => candidate.HasWordSync).ThenByDescending(candidate => candidate.SyncedLyrics.Length > 0)
            .ThenBy(candidate => candidate.Id).First();
    }

    public static SongLyrics ToLyrics(LyricsCandidate match, double duration, string fileName)
    {
        string plain = LyricsDisplay.Clean(match.PlainText.Length > 0 ? match.PlainText : match.SyncedLyrics);
        bool timing = duration > 0 && Math.Abs(match.DurationSeconds - duration) <= 2
            && SongNameMatching.IsLive(fileName) == SongNameMatching.IsLive(match.Title + " " + match.Album);
        return new(plain, match.SyncedLyrics, match.WordLyricsFile, match.HasWordSync,
            match.SourceUrl.Length > 0 ? match.SourceUrl : "https://lrclib.net/api/get/" + match.Id, match.Title, match.Artist, match.DurationSeconds, timing);
    }
}
