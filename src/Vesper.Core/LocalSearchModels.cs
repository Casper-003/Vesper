namespace Vesper.Core;

/// <summary>An immutable projection of accepted library data. Unproven grouping is deliberately absent.</summary>
public sealed record SearchDocument(string SongId, string Title, string Artist, string Album, bool IsAvailable)
{
    public string? GroupId { get; init; }
    public string? VersionId { get; init; }
    public string VersionName { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.MaxValue;
    public bool IsOriginalMv { get; init; }
    public string? AlbumId { get; init; }
    public string AlbumArtist { get; init; } = "";
    public IReadOnlyList<SearchArtistCredit>? Artists { get; init; }
    public IReadOnlyList<SearchResource>? Resources { get; init; }
}

public sealed record SearchArtistCredit(string Name, string? Id = null);
public sealed record SearchResource(string Id, string SongId, string Path, bool IsAvailable,
    int Resolution = 0, int SourceOrder = 0);

public sealed record SearchOptions(int SongPageSize = 30, int ArtistPageSize = 5, int AlbumPageSize = 5)
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(180);
    internal void Validate()
    {
        if (SongPageSize <= 0 || ArtistPageSize <= 0 || AlbumPageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(SongPageSize), "Search page sizes must be positive.");
    }
}

public enum SearchStatus { Empty, Success, NoMatch }
public enum SearchField { Title, Artist, Album, Version }
public enum SearchMatchKind { TextExact, TextPrefix, TextContains, PinyinExact, PinyinPrefix, InitialsExact, InitialsPrefix, Version }
public sealed record SearchTermEvidence(string Term, SearchField Field, SearchMatchKind Kind, bool IsLiteral, string VersionId);
public sealed record MatchEvidence(IReadOnlyList<SearchTermEvidence> Terms, int Rank, bool HasVersionIntent = false)
{
    public string Description => string.Join("、", Terms.Select(term => term.Field switch
    {
        SearchField.Title => term.Kind is SearchMatchKind.PinyinExact or SearchMatchKind.PinyinPrefix ? "歌名全拼" : term.Kind is SearchMatchKind.InitialsExact or SearchMatchKind.InitialsPrefix ? "歌名首字母" : "歌名",
        SearchField.Artist => term.Kind is SearchMatchKind.PinyinExact or SearchMatchKind.PinyinPrefix ? "艺人全拼" : term.Kind is SearchMatchKind.InitialsExact or SearchMatchKind.InitialsPrefix ? "艺人首字母" : "艺人",
        SearchField.Album => "专辑", _ => "版本"
    }).Distinct());
}

public sealed record VersionHit(string VersionId, string SongId, string VersionName, bool IsAvailable,
    string? PreferredResourceId, int Resolution, MatchEvidence? MatchEvidence)
{
    public IReadOnlyList<SearchResource> Resources { get; init; } = [];
    public DateTimeOffset AddedAt { get; init; }
    public bool IsOriginalMv { get; init; }
}
public sealed record SongGroupHit(string GroupId, string Title, string Artist,
    string RepresentativeVersionId, string BestMatchedVersionId, IReadOnlyList<string> MatchedVersionIds,
    IReadOnlyList<VersionHit> Versions, bool IsAvailable, MatchEvidence MatchEvidence);
public sealed record ArtistHit(string Id, string Name, MatchEvidence MatchEvidence, int SongGroupCount);
public sealed record AlbumHit(string Id, string Name, string Artist, MatchEvidence MatchEvidence, int SongGroupCount);
public sealed record ResultPage<T>(IReadOnlyList<T> Items, bool HasMore, string? ContinuationToken);

/// <summary>One sorted query snapshot. Paging is bound to this response, category and index revision.</summary>
public sealed class SearchResponse
{
    private readonly string pageIdentity = Guid.NewGuid().ToString("N");
    private readonly IReadOnlyList<SongGroupHit> songs;
    private readonly IReadOnlyList<ArtistHit> artists;
    private readonly IReadOnlyList<AlbumHit> albums;
    private readonly SearchOptions options;
    public string Query { get; }
    public long QueryId { get; }
    public long IndexRevision { get; }
    public SearchStatus Status { get; }
    public ResultPage<SongGroupHit> Songs => GetSongsPage();
    public ResultPage<ArtistHit> Artists => GetArtistsPage();
    public ResultPage<AlbumHit> Albums => GetAlbumsPage();
    public int SongCount => songs.Count;
    public int ArtistCount => artists.Count;
    public int AlbumCount => albums.Count;

    internal SearchResponse(string query, long queryId, long revision, SearchStatus status,
        IReadOnlyList<SongGroupHit> songs, IReadOnlyList<ArtistHit> artists, IReadOnlyList<AlbumHit> albums, SearchOptions options)
    {
        Query = query; QueryId = queryId; IndexRevision = revision; Status = status;
        this.songs = songs; this.artists = artists; this.albums = albums; this.options = options;
    }
    public ResultPage<SongGroupHit> GetSongsPage(string? continuationToken = null) => Page(songs, options.SongPageSize, "songs", continuationToken);
    public ResultPage<ArtistHit> GetArtistsPage(string? continuationToken = null) => Page(artists, options.ArtistPageSize, "artists", continuationToken);
    public ResultPage<AlbumHit> GetAlbumsPage(string? continuationToken = null) => Page(albums, options.AlbumPageSize, "albums", continuationToken);
    private ResultPage<T> Page<T>(IReadOnlyList<T> source, int size, string category, string? token)
    {
        int offset = 0;
        if (token is not null)
        {
            string prefix = $"{pageIdentity}:{QueryId}:{IndexRevision}:{category}:";
            if (!token.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(token.AsSpan(prefix.Length), out offset)
                || offset < 0 || offset > source.Count || offset % size != 0)
                throw new ArgumentException("The continuation does not belong to this query snapshot and category.", nameof(token));
        }
        int end = (int)Math.Min(source.Count, (long)offset + size);
        var items = new T[end - offset];
        for (int i = offset; i < end; i++) items[i - offset] = source[i];
        bool hasMore = end < source.Count;
        return new(Array.AsReadOnly(items), hasMore, hasMore ? $"{pageIdentity}:{QueryId}:{IndexRevision}:{category}:{end}" : null);
    }
}
