using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vesper.Core;

public sealed record SongMatch(string RecordingId, string ReleaseId, string Title, string Artist,
    string Album, string Year, string Version, string ReleaseType = "", string ReleaseStatus = "", string CoverUrl = "")
{
    public IReadOnlyList<MusicArtistCredit> ArtistCredits { get; init; } = [];
    public string Description => string.Join(" · ", new[] { Album, Year, Version }.Where(value => value.Length > 0));
}

public sealed record SongSearchHint(string Title, string Artist, string FileName)
{
    public static SongSearchHint From(string fileName, SongMetadata metadata)
    {
        var plan = SongNameMatching.Plan(fileName, metadata.Title, metadata.Artist);
        var first = plan.FirstOrDefault();
        return new(first?.Title ?? fileName, first?.ArtistHint ?? metadata.Artist, fileName);
    }
}

public sealed class MusicBrainzClient(HttpClient client)
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static DateTimeOffset nextRequest;

    public async Task<IReadOnlyList<SongMatch>> SearchAsync(string title, string artist, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        static string Quote(string value) => "\"" + value.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string query = "recording:" + Quote(title);
        if (!string.IsNullOrWhiteSpace(artist)) query += " AND artist:" + Quote(artist);
        using var json = await GetAsync("recording/?fmt=json&limit=25&query=" + Uri.EscapeDataString(query), token);
        var matches = new List<SongMatch>();
        if (!json.RootElement.TryGetProperty("recordings", out var recordings)) return matches;
        foreach (var recording in recordings.EnumerateArray())
        {
            string recordingId = Text(recording, "id");
            if (!Guid.TryParse(recordingId, out _)) continue;
            string name = Text(recording, "title");
            string credit = recording.TryGetProperty("artist-credit", out var artists)
                ? string.Concat(artists.EnumerateArray().Select(a => Text(a, "name") + Text(a, "joinphrase"))) : "";
            var credits = recording.TryGetProperty("artist-credit", out var creditArray) ? creditArray.EnumerateArray()
                .Where(value => Text(value, "name").Length > 0).Select(value => new MusicArtistCredit(Text(value, "name"),
                    ExternalIds: value.TryGetProperty("artist", out var artist) && Guid.TryParse(Text(artist, "id"), out _)
                        ? new[] { new MusicExternalId("musicbrainz", "artist", Text(artist, "id")) } : [])).ToArray() : [];
            string version = Text(recording, "disambiguation");
            if (recording.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
                foreach (var release in releases.EnumerateArray())
                {
                    string releaseId = Text(release, "id");
                    if (!Guid.TryParse(releaseId, out _)) continue;
                    matches.Add(new(recordingId, releaseId, name, credit, Text(release, "title"),
                        Text(release, "date"), string.Join(" · ", new[] { version, Text(release, "disambiguation"), Text(release, "country") }.Where(s => s.Length > 0)),
                        release.TryGetProperty("release-group", out var group) ? Text(group, "primary-type") : "", Text(release, "status"))
                        { ArtistCredits = credits });
                }
            else matches.Add(new(recordingId, "", name, credit, "", Text(recording, "first-release-date"), version) { ArtistCredits = credits });
        }
        return matches.DistinctBy(match => (match.RecordingId, match.ReleaseId)).ToList();
    }

    public async Task<SongMetadata> GetMetadataAsync(SongMatch match, CancellationToken token = default)
    {
        string publisher = "", date = match.Year;
        if (Guid.TryParse(match.ReleaseId, out _))
        {
            using var json = await GetAsync($"release/{match.ReleaseId}?fmt=json&inc=labels", token);
            var release = json.RootElement;
            if (Text(release, "date") is { Length: > 0 } actualDate) date = actualDate;
            if (release.TryGetProperty("label-info", out var labels))
                publisher = string.Join(" / ", labels.EnumerateArray()
                    .Where(label => label.TryGetProperty("label", out _)).Select(label => Text(label.GetProperty("label"), "name"))
                    .Where(value => value.Length > 0 && !value.StartsWith('[')).Distinct());
        }
        return new(match.Title, match.Artist, match.Album, publisher, date.Length >= 4 ? date[..4] : date,
            SourceUrl: $"https://musicbrainz.org/recording/{match.RecordingId}",
            CoverUrl: Guid.TryParse(match.ReleaseId, out _) ? $"https://coverartarchive.org/release/{match.ReleaseId}/front-500" : "")
        {
            // Choosing a likely recording/release for descriptive metadata does not prove
            // that the local file belongs to that edition. Do not bind a release or its track order.
            ExternalIds = Guid.TryParse(match.RecordingId, out _) ? [new("musicbrainz", "recording", match.RecordingId)] : [],
            ArtistCredits = match.ArtistCredits
        };
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            TimeSpan delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://musicbrainz.org/ws/2/" + path);
            request.Headers.UserAgent.ParseAdd("Vesper/0.1 (+https://github.com/Casper-003/Vesper)");
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        }
        finally { gate.Release(); }
    }

    internal static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}

public sealed record MusicVideoMatch(string Bvid, string Title, string Uploader, int DurationSeconds)
{
    public string Url => "https://www.bilibili.com/video/" + Bvid;
}

public sealed class BilibiliVideoClient(HttpClient client)
{
    public async Task<IReadOnlyList<MusicVideoMatch>> SearchAsync(string title, string artist, CancellationToken token = default)
    {
        string query = $"{artist} {title} MV".Trim();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.bilibili.com/x/web-interface/search/type?search_type=video&page=1&keyword=" + Uri.EscapeDataString(query));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Vesper/0.1");
        request.Headers.Referrer = new("https://www.bilibili.com/");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!json.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
            throw new HttpRequestException("B 站检索暂不可用。");
        if (!json.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("result", out var results)) return [];
        return results.EnumerateArray().Select(video => new MusicVideoMatch(MusicBrainzClient.Text(video, "bvid"),
            WebUtility.HtmlDecode(Regex.Replace(MusicBrainzClient.Text(video, "title"), "<[^>]+>", "")),
            MusicBrainzClient.Text(video, "author"), Duration(MusicBrainzClient.Text(video, "duration"))))
            .Where(video => Regex.IsMatch(video.Bvid, @"^BV[0-9A-Za-z]{10}$")).ToList();
    }

    private static int Duration(string value)
    {
        int result = 0;
        foreach (string part in value.Split(':'))
        { if (!int.TryParse(part, out int number) || number < 0 || number > 999) return 0; result = result * 60 + number; }
        return result;
    }

    public static MusicVideoMatch? ChooseReliable(IEnumerable<MusicVideoMatch> candidates, string title, string artist,
        double durationSeconds, string fileName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || durationSeconds <= 0) return null;
        static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        string expectedTitle = Normalize(title), expectedArtist = Normalize(artist);
        if (expectedTitle.Length == 0 || expectedArtist.Length == 0) return null;
        bool HasName(string text, string name) => Regex.IsMatch(text.Normalize(),
            @"(?<![\p{L}\p{N}])" + Regex.Escape(name.Normalize()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string[] versions = ["现场", "LIVE", "翻唱", "COVER", "REMIX", "伴奏", "INSTRUMENTAL"];
        string[] excluded = ["混剪", "饭制", "剪辑", "AMV", "MAD", "歌词版", "纯音乐"];
        string expectedVersion = Normalize(fileName);
        return candidates.Where(video =>
        {
            string text = Normalize(video.Title);
            return HasName(video.Title, title) && HasName(video.Title, artist)
                && (text.Contains("MV") || text.Contains("MUSICVIDEO") || text.Contains("音乐录影带"))
                && versions.All(version => text.Contains(version) == expectedVersion.Contains(version))
                && excluded.All(marker => !text.Contains(marker))
                && video.DurationSeconds > 0 && Math.Abs(video.DurationSeconds - durationSeconds) <= 3;
        }).OrderBy(video => Math.Abs(video.DurationSeconds - durationSeconds)).FirstOrDefault();
    }
}
