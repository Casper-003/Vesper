namespace Vesper.Core;

public sealed record SongMetadata(string Title = "", string Artist = "", string Album = "",
    string Publisher = "", string Year = "", string Lyrics = "", string SourceUrl = "", string CoverUrl = "",
    double DurationSeconds = 0)
{
    public string AlbumArtist { get; init; } = "";
    public int? DiscNumber { get; init; }
    public int? TrackNumber { get; init; }
    public IReadOnlyList<MusicExternalId> ExternalIds { get; init; } = [];
    public IReadOnlyList<MusicArtistCredit> ArtistCredits { get; init; } = [];
}

public sealed record MusicExternalId(string Provider, string Kind, string Value);
public sealed record MusicArtistCredit(string Name, string Role = "performer", IReadOnlyList<MusicExternalId>? ExternalIds = null);

public sealed record SongCorrections(string Title, string Artist, string Album, DateTimeOffset ModifiedAt);

// 资料与扫描得到的文件记录分开保存，扫描快照不能覆盖用户刚刚完成的修正。
public sealed record SongDetails(SongMetadata? Local = null, SongMetadata? Online = null,
    SongCorrections? Corrections = null, MusicVideoMatch? Video = null, SongLyrics? OnlineLyrics = null, SongLookupState? Lookup = null)
{
    public string LocalReadKey { get; init; } = "";
    public string LocalResolution { get; init; } = "";
    public SongAssetState? Assets { get; init; }
    public SongVideoLookupState? VideoLookup { get; init; }
    public SongMetadata Effective(string fallbackTitle)
    {
        var local = Local ?? new();
        var online = Online ?? new();
        static string Text(string? value) => value?.Trim() ?? "";
        static string First(params string?[] values) => values.Select(Text).FirstOrDefault(value => value.Length > 0) ?? "";
        var result = new SongMetadata(
            First(Corrections?.Title, online.Title, local.Title, fallbackTitle),
            Corrections is { } edit ? Text(edit.Artist) : First(online.Artist, local.Artist),
            Corrections is { } correction ? Text(correction.Album) : First(online.Album, local.Album),
            First(online.Publisher, local.Publisher), First(online.Year, local.Year),
            First(local.Lyrics, OnlineLyrics?.PlainText), Text(online.SourceUrl), Text(online.CoverUrl), local.DurationSeconds);

        static bool Same(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        // A correction changes this song's attribution, not the old release's track position.
        // Never transfer numbering or an external identity solely because it was cached before an edit.
        bool artistEdited = Corrections is not null && !Same(result.Artist, First(online.Artist, local.Artist));
        bool albumEdited = Corrections is not null && !Same(result.Album, First(online.Album, local.Album));
        SongMetadata? albumSource = null;
        if (!artistEdited && !albumEdited && result.Album.Length > 0)
        {
            bool localAttribution = local.Artist.Length == 0 || Same(result.Artist, local.Artist);
            if (localAttribution && Same(result.Album, local.Album) && (local.TrackNumber is > 0 || local.DiscNumber is > 0
                || local.AlbumArtist.Length > 0 || local.ExternalIds.Any(id => id.Kind is "release" or "release-group"))) albumSource = local;
            else if (Same(result.Album, online.Album)) albumSource = online;
            else if (localAttribution && Same(result.Album, local.Album)) albumSource = local;
        }
        var identitySource = Same(result.Title, online.Title) && Same(result.Artist, online.Artist) ? online
            : Same(result.Title, local.Title) && Same(result.Artist, local.Artist) ? local : null;
        var artistSource = !artistEdited && Same(result.Artist, local.Artist) && local.ExternalIds.Any(id => id.Kind == "artist") ? local
            : !artistEdited && Same(result.Artist, online.Artist) ? online : null;
        var ids = (identitySource?.ExternalIds ?? []).Where(id => id.Kind is not ("artist" or "release" or "release-group" or "album-artist"))
            .Concat((artistSource?.ExternalIds ?? []).Where(id => id.Kind == "artist"))
            .Concat((albumSource?.ExternalIds ?? []).Where(id => id.Kind is "release" or "release-group" or "album-artist"))
            .Distinct().ToArray();
        if (ReferenceEquals(albumSource, local) && Same(result.Album, online.Album) && Same(result.Artist, online.Artist))
        {
            // Only explicit structured release identities can enrich local membership.
            // Automatic descriptive candidates deliberately never supply these identities.
            var accepted = online.ExternalIds.Where(id => id.Kind is "release" or "release-group" or "album-artist")
                .Where(id => !ids.Any(old => old.Provider == id.Provider && old.Kind == id.Kind && old.Value != id.Value));
            ids = ids.Concat(accepted).Distinct().ToArray();
        }
        var creditSource = !artistEdited && Same(result.Artist, local.Artist) && local.ArtistCredits.Count > 0 ? local
            : !artistEdited && Same(result.Artist, online.Artist) ? online : null;
        var credits = (creditSource?.ArtistCredits ?? []).Where(credit => !string.IsNullOrWhiteSpace(credit.Name)).ToList();
        if (!artistEdited && Same(result.Artist, online.Artist))
        {
            foreach (var credit in online.ArtistCredits.Where(credit => credit.Role != "performer"))
                if (!credits.Any(old => Same(old.Name, credit.Name) && old.Role == credit.Role
                    && !(old.ExternalIds ?? []).Any(prior => (credit.ExternalIds ?? []).Any(next =>
                        prior.Provider == next.Provider && prior.Kind == next.Kind && prior.Value != next.Value)))) credits.Add(credit);
            for (int i = 0; i < credits.Count; i++)
            {
                var credit = credits[i];
                var matching = online.ArtistCredits.Where(other => Same(other.Name, credit.Name) && other.Role == credit.Role).ToArray();
                if (matching.Length != 1) continue;
                var prior = credit.ExternalIds ?? [];
                var accepted = (matching[0].ExternalIds ?? []).Where(id => !prior.Any(old => old.Provider == id.Provider
                    && old.Kind == id.Kind && old.Value != id.Value));
                credits[i] = credit with { ExternalIds = prior.Concat(accepted).Distinct().ToArray() };
            }
        }
        if (!credits.Any(credit => credit.Role == "performer") && result.Artist.Length > 0)
            credits.Add(new(result.Artist, ExternalIds: ids.Where(id => id.Kind == "artist").ToArray()));
        return result with
        {
            AlbumArtist = albumSource?.AlbumArtist ?? "",
            CoverUrl = albumEdited || artistEdited ? "" : result.CoverUrl,
            DiscNumber = albumSource?.DiscNumber is > 0 ? albumSource.DiscNumber : null,
            TrackNumber = albumSource?.TrackNumber is > 0 ? albumSource.TrackNumber : null,
            ExternalIds = ids, ArtistCredits = credits.ToArray()
        };
    }

    public SongDetails Merge(SongDetails other) => new(Local ?? other.Local, Online ?? other.Online,
        Corrections is null || other.Corrections?.ModifiedAt > Corrections.ModifiedAt ? other.Corrections : Corrections,
        Video ?? other.Video, OnlineLyrics ?? other.OnlineLyrics, Lookup ?? other.Lookup);
}
