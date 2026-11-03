using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Core;

public enum LookupOutcome { Matched, NotFound, NeedsChoice, Unavailable }
public sealed record SongLookupState(string Key, DateTimeOffset CheckedAt, LookupOutcome Metadata, LookupOutcome Lyrics);
public sealed record SongLookupResult(SongMetadata? Metadata, SongLyrics? Lyrics, LookupOutcome MetadataOutcome, LookupOutcome LyricsOutcome);

public sealed record SongLookupInput(string FileName, SongDetails Details)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Providers = 2, FileName, Details.Local?.Title, Artist = Details.Local?.Artist, Duration = Details.Local?.DurationSeconds,
        Details.Corrections
    }))));
    public bool ShouldLookup(DateTimeOffset now) => Details.Lookup is not { } saved || saved.Key != Key
        || now - saved.CheckedAt > (saved.Metadata == LookupOutcome.Unavailable || saved.Lyrics == LookupOutcome.Unavailable
            ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1)) && (saved.Metadata != LookupOutcome.Matched || saved.Lyrics != LookupOutcome.Matched);
}

public sealed class AutomaticSongLookup(
    Func<string, CancellationToken, Task<IReadOnlyList<SongMatch>>> searchMetadata,
    Func<SongMatch, CancellationToken, Task<SongMetadata>> getMetadata,
    Func<string, CancellationToken, Task<IReadOnlyList<LyricsCandidate>>> searchLyrics,
    Func<string, string, CancellationToken, Task<IReadOnlyList<LyricsCandidate>>>? searchLyricsWithArtist = null)
{
    public async Task<SongLookupResult> LookupAsync(SongLookupInput input, CancellationToken token = default)
    {
        SongMetadata known = input.Details.Effective("");
        bool needMetadata = input.Details.Online is null || input.Details.Online.SourceUrl.Contains("lrclib.net", StringComparison.OrdinalIgnoreCase);
        bool needLyrics = string.IsNullOrWhiteSpace(known.Lyrics);
        var queries = SongNameMatching.Plan(input.FileName, known.Title, known.Artist);
        var songs = new List<SongMatch>();
        var lyrics = new List<LyricsCandidate>();
        bool metadataFailed = false, lyricsFailed = false;
        foreach (var query in queries)
        {
            token.ThrowIfCancellationRequested();
            // 两个来源均先按歌名查，艺人只在本地核对，不收紧服务端查询导致漏搜。
            async Task SearchMetadata()
            {
                if (!needMetadata) return;
                try { songs.AddRange(await searchMetadata(query.Title, token)); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { metadataFailed = true; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException) { metadataFailed = true; }
            }
            async Task SearchLyrics()
            {
                if (!needLyrics) return;
                try { lyrics.AddRange(await (searchLyricsWithArtist is null ? searchLyrics(query.Title, token)
                    : searchLyricsWithArtist(query.Title, known.Artist.Length > 0 ? known.Artist : query.ArtistHint, token))); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { lyricsFailed = true; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException) { lyricsFailed = true; }
            }
            await Task.WhenAll(SearchMetadata(), SearchLyrics());
            var early = SongNameMatching.ChooseMetadata(songs, queries, known.Artist, input.FileName, lyrics);
            string artist = early?.Artist ?? known.Artist;
            var lyric = LrcLibClient.Choose(lyrics, queries, artist, known.Album, known.DurationSeconds);
            bool artistConfirmed = artist.Length > 0 && (known.Artist.Length > 0 || queries.Any(q => SongNameMatching.SameArtist(artist, q.ArtistHint)));
            if ((!needMetadata || early is not null) && (!needLyrics || lyric is not null) && (queries.Count == 1 || artistConfirmed)) break;
        }
        SongMetadata? metadata = null;
        LookupOutcome metadataOutcome = LookupOutcome.Matched;
        if (needMetadata)
        {
            var chosen = SongNameMatching.ChooseMetadata(songs, queries, known.Artist, input.FileName, lyrics);
            if (chosen is not null)
            {
                try { metadata = await getMetadata(chosen, token); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException
                    || ex is OperationCanceledException && !token.IsCancellationRequested)
                {
                    // 标签接口暂不可用时，搜索已确认的基本资料仍可使用。
                    metadata = new(chosen.Title, chosen.Artist, chosen.Album, Year: chosen.Year.Length >= 4 ? chosen.Year[..4] : chosen.Year,
                        SourceUrl: "https://musicbrainz.org/recording/" + chosen.RecordingId,
                        CoverUrl: chosen.ReleaseId.Length > 0 ? "https://coverartarchive.org/release/" + chosen.ReleaseId + "/front-500" : "")
                    { ExternalIds = [new("musicbrainz", "recording", chosen.RecordingId)], ArtistCredits = chosen.ArtistCredits };
                }
            }
            else metadataOutcome = metadataFailed ? LookupOutcome.Unavailable
                : songs.Any(song => queries.Any(query => SongNameMatching.Same(song.Title, query.Title))) ? LookupOutcome.NeedsChoice : LookupOutcome.NotFound;
        }
        string expectedArtist = known.Artist.Length > 0 ? known.Artist : metadata?.Artist ?? "";
        var lyricMatch = needLyrics ? LrcLibClient.Choose(lyrics, queries, expectedArtist, metadata?.Album ?? known.Album, known.DurationSeconds) : null;
        var resultLyrics = lyricMatch is not null ? LrcLibClient.ToLyrics(lyricMatch, known.DurationSeconds, input.FileName) : null;
        LookupOutcome lyricOutcome = !needLyrics || lyricMatch is not null ? LookupOutcome.Matched : lyricsFailed ? LookupOutcome.Unavailable
            : lyrics.Any(lyric => queries.Any(query => SongNameMatching.Same(lyric.Title, query.Title))) ? LookupOutcome.NeedsChoice : LookupOutcome.NotFound;
        // 资料库未收录时，可靠的歌词曲目身份仍能提供歌名、艺人；不复制不可靠的专辑广告字段。
        if (needMetadata && metadata is null && lyricMatch is not null)
        {
            metadata = new(lyricMatch.Title, lyricMatch.Artist, SourceUrl: resultLyrics!.SourceUrl);
            metadataOutcome = LookupOutcome.Matched;
        }
        return new(metadata, resultLyrics, metadataOutcome, lyricOutcome);
    }
}
