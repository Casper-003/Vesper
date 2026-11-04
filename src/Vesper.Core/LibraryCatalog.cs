namespace Vesper.Core;

public sealed record CatalogArtist(string Id, string Name)
{
    public IReadOnlyList<MusicExternalId> ExternalIds { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record CatalogAlbum(string Id, string Name, string Artist)
{
    public string ArtistId { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string Year { get; init; } = "";
    public string OriginSongId { get; init; } = "";
    public IReadOnlyList<MusicExternalId> ExternalIds { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

// Track position belongs to a release membership, never to SongId itself.
public sealed record CatalogTrack(string Id, string SongId, string AlbumId, int? DiscNumber = null, int? TrackNumber = null)
{
    public string Title { get; init; } = "";
    public bool FromMetadata { get; init; }
}

public sealed record CatalogSongCredit(string ArtistId, string Role);
public sealed record CatalogSongAssociation(IReadOnlyList<CatalogSongCredit> Credits, string? AlbumId = null);

/// <summary>Persistent local identities. Search projections can be discarded without changing these IDs.</summary>
public sealed class LibraryCatalog
{
    private readonly object gate = new();
    private List<CatalogArtist>? indexedArtists;
    private List<CatalogAlbum>? indexedAlbums;
    private Dictionary<string, CatalogArtist> artistById = [];
    private Dictionary<string, CatalogAlbum> albumById = [];

    public List<CatalogArtist> Artists { get; set; } = [];
    public List<CatalogAlbum> Albums { get; set; } = [];
    public List<CatalogTrack> Tracks { get; set; } = [];
    public Dictionary<string, CatalogSongAssociation> SongAssociations { get; set; } = [];

    public void Reconcile(LibraryState library)
    {
        lock (gate)
        {
            var artists = new List<CatalogArtist>(Artists);
            var albums = new List<CatalogAlbum>(Albums);
            var associations = new Dictionary<string, CatalogSongAssociation>();
            var songIds = library.Songs.Select(song => song.Id).ToHashSet(StringComparer.Ordinal);
            var tracks = Tracks.Where(track => !track.FromMetadata && songIds.Contains(track.SongId)).ToList();
            // Name lookups are precomputed once, not scanned for every song in a large library.
            var namedArtists = artists.GroupBy(artist => Key(artist.Name)).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var artist in artists)
                foreach (string alias in artist.Aliases)
                    AddName(namedArtists, Key(alias), artist);
            var namedAlbums = albums.GroupBy(album => Key(album.Name)).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var album in albums)
                foreach (string alias in album.Aliases)
                    AddName(namedAlbums, Key(alias), album);
            var externalArtists = artists.SelectMany(artist => artist.ExternalIds.Select(id => (Key: ExternalKey(id), Artist: artist)))
                .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Artist);
            var externalAlbums = albums.SelectMany(album => album.ExternalIds.Where(id => id.Kind == "release")
                .Select(id => (Key: ExternalKey(id), Album: album))).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Album);
            var priorTracks = Tracks.Where(track => track.FromMetadata).GroupBy(track => track.SongId)
                .ToDictionary(group => group.Key, group => group.First());
            var artistsById = artists.ToDictionary(artist => artist.Id);
            var albumsById = albums.ToDictionary(album => album.Id);
            foreach (var song in library.Songs)
            {
                var metadata = (library.Details.GetValueOrDefault(song.Id) ?? new()).Effective(song.Title);
                var priorAssociation = SongAssociations.GetValueOrDefault(song.Id);
                var credits = new List<CatalogSongCredit>();
                foreach (var credit in metadata.ArtistCredits)
                {
                    if (string.IsNullOrWhiteSpace(credit.Name)) continue;
                    var artist = ResolveArtist(credit.Name.Trim(), credit.ExternalIds ?? [],
                        priorAssociation?.Credits.Where(relation => relation.Role == credit.Role).Select(relation => relation.ArtistId) ?? []);
                    var relation = new CatalogSongCredit(artist.Id, string.IsNullOrWhiteSpace(credit.Role) ? "performer" : credit.Role);
                    if (!credits.Contains(relation)) credits.Add(relation);
                }
                CatalogAlbum? album = null;
                if (!string.IsNullOrWhiteSpace(metadata.Album))
                {
                    string albumArtist = string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.Artist : metadata.AlbumArtist;
                    CatalogArtist? artist = null;
                    if (!string.IsNullOrWhiteSpace(albumArtist))
                    {
                        var performer = credits.Where(credit => credit.Role == "performer").Select(credit => artistsById[credit.ArtistId])
                            .FirstOrDefault(value => Key(value.Name) == Key(albumArtist) || value.Aliases.Any(alias => Key(alias) == Key(albumArtist)));
                        artist = performer ?? ResolveArtist(albumArtist.Trim(), metadata.ExternalIds.Where(id => id.Kind == "album-artist")
                            .Select(id => id with { Kind = "artist" }).ToArray(),
                            priorAssociation?.AlbumId is { } previousAlbumId && albumsById.GetValueOrDefault(previousAlbumId) is { } previousAlbum
                                ? new[] { previousAlbum.ArtistId } : []);
                    }
                    var ids = CleanIds(metadata.ExternalIds.Where(id => id.Kind is "release" or "release-group"));
                    var releaseIds = ids.Where(id => id.Kind == "release").ToArray();
                    album = releaseIds.Select(id => externalAlbums.GetValueOrDefault(ExternalKey(id))).FirstOrDefault(value => value is not null);
                    if (album is null && priorTracks.TryGetValue(song.Id, out var priorTrack)
                        && namedAlbums.GetValueOrDefault(Key(metadata.Album))?.FirstOrDefault(value => value.Id == priorTrack.AlbumId) is { } previousRelease
                        && (previousRelease.ArtistId.Length == 0 || previousRelease.ArtistId == (artist?.Id ?? ""))
                        && CompatibleYear(previousRelease.Year, metadata.Year)
                        && !Conflict(previousRelease.ExternalIds, ids))
                    {
                        // Enrich an already-associated provisional local entity; a same-name stranger
                        // is insufficient evidence to transfer its identity or merge two releases.
                        album = previousRelease;
                        var combined = CleanIds(album.ExternalIds.Concat(ids));
                        if (!combined.SequenceEqual(album.ExternalIds) || album.ArtistId.Length == 0 && artist is not null)
                        {
                            var updated = album with { ExternalIds = combined, ArtistId = artist?.Id ?? album.ArtistId,
                                Artist = artist?.Name ?? album.Artist };
                            albums[albums.IndexOf(album)] = updated; albumsById[updated.Id] = updated;
                            foreach (var list in namedAlbums.Values) for (int i = 0; i < list.Count; i++) if (list[i].Id == album.Id) list[i] = updated;
                            foreach (var id in combined.Where(id => id.Kind == "release")) externalAlbums[ExternalKey(id)] = updated;
                            album = updated;
                        }
                    }
                    if (album is null && namedAlbums.TryGetValue(Key(metadata.Album), out var candidates))
                    {
                        // A name is not evidence that two different releases (or unknown artists) are the same.
                        // Unidentified releases stay provisional and do not inherit an arbitrary online release.
                        album = candidates.FirstOrDefault(candidate => candidate.ArtistId.Length > 0 && candidate.ArtistId == artist?.Id
                            && CompatibleYear(candidate.Year, metadata.Year)
                            && !candidate.ExternalIds.Any(id => id.Kind == "release") && releaseIds.Length == 0);
                        album ??= candidates.FirstOrDefault(candidate => candidate.OriginSongId == song.Id && candidate.ArtistId.Length == 0
                            && artist is null && !candidate.ExternalIds.Any(id => id.Kind == "release") && releaseIds.Length == 0);
                        if (album is null && priorTracks.TryGetValue(song.Id, out var previous)
                            && candidates.FirstOrDefault(candidate => candidate.Id == previous.AlbumId) is { } prior
                            && prior.ArtistId.Length == 0 && artist is null && releaseIds.Length == 0)
                            album = prior;
                    }
                    if (album is null)
                    {
                        album = new(NewId("album"), metadata.Album.Trim(), albumArtist.Trim())
                        {
                            ArtistId = artist?.Id ?? "", OriginSongId = artist is null ? song.Id : "",
                            Year = metadata.Year, CoverUrl = metadata.CoverUrl, ExternalIds = ids
                        };
                        albums.Add(album); albumsById[album.Id] = album; AddName(namedAlbums, Key(album.Name), album);
                        foreach (var id in releaseIds) externalAlbums[ExternalKey(id)] = album;
                    }
                    else if (album.CoverUrl.Length == 0 && metadata.CoverUrl.Length > 0 || album.Year.Length == 0 && metadata.Year.Length > 0)
                    {
                        var updated = album with { CoverUrl = album.CoverUrl.Length > 0 ? album.CoverUrl : metadata.CoverUrl,
                            Year = album.Year.Length > 0 ? album.Year : metadata.Year };
                        albums[albums.IndexOf(album)] = updated; albumsById[updated.Id] = updated;
                        // Keep the lookup values aligned with the immutable replacement.
                        foreach (var list in namedAlbums.Values) for (int i = 0; i < list.Count; i++) if (list[i].Id == album.Id) list[i] = updated;
                        foreach (var id in updated.ExternalIds.Where(id => id.Kind == "release")) externalAlbums[ExternalKey(id)] = updated;
                        album = updated;
                    }
                    var old = priorTracks.GetValueOrDefault(song.Id);
                    tracks.Add(new(old?.AlbumId == album.Id ? old.Id : NewId("track"), song.Id, album.Id,
                        Positive(metadata.DiscNumber), Positive(metadata.TrackNumber)) { Title = metadata.Title, FromMetadata = true });
                }
                associations[song.Id] = new(credits.ToArray(), album?.Id);
            }
            var titles = library.Songs.ToDictionary(song => song.Id,
                song => (library.Details.GetValueOrDefault(song.Id) ?? new()).Effective(song.Title).Title);
            Artists = artists; Albums = albums; SongAssociations = associations;
            Tracks = tracks.Select(track => track with { Title = titles.GetValueOrDefault(track.SongId) ?? track.Title }).ToList();

            CatalogArtist ResolveArtist(string name, IReadOnlyList<MusicExternalId> supplied, IEnumerable<string>? priorIds = null)
            {
                var ids = CleanIds(supplied);
                var existing = ids.Select(id => externalArtists.GetValueOrDefault(ExternalKey(id))).FirstOrDefault(value => value is not null);
                existing ??= (priorIds ?? []).Select(id => artistsById.GetValueOrDefault(id)).OfType<CatalogArtist>()
                    .FirstOrDefault(candidate => (Key(candidate.Name) == Key(name) || candidate.Aliases.Any(alias => Key(alias) == Key(name)))
                        && !Conflict(candidate.ExternalIds, ids));
                if (existing is null && namedArtists.TryGetValue(Key(name), out var candidates))
                    existing = candidates.FirstOrDefault(candidate => !Conflict(candidate.ExternalIds, ids)
                        && (ids.Length == 0 && candidate.ExternalIds.Count == 0 || ids.Length > 0 && candidate.ExternalIds.Intersect(ids).Any()));
                if (existing is not null)
                {
                    var combined = CleanIds(existing.ExternalIds.Concat(ids));
                    if (!combined.SequenceEqual(existing.ExternalIds))
                    {
                        var updated = existing with { ExternalIds = combined };
                        artists[artists.IndexOf(existing)] = updated; artistsById[updated.Id] = updated;
                        foreach (var list in namedArtists.Values) for (int i = 0; i < list.Count; i++) if (list[i].Id == existing.Id) list[i] = updated;
                        foreach (var id in combined) externalArtists[ExternalKey(id)] = updated;
                        existing = updated;
                    }
                    return existing;
                }
                var created = new CatalogArtist(NewId("artist"), name) { ExternalIds = ids };
                artists.Add(created); artistsById[created.Id] = created; AddName(namedArtists, Key(name), created);
                foreach (var id in ids) externalArtists[ExternalKey(id)] = created;
                return created;
            }
        }
    }

    public IReadOnlyList<CatalogArtist> GetSongArtists(string songId)
    {
        lock (gate)
        {
            EnsureIndexes();
            return (SongAssociations.GetValueOrDefault(songId)?.Credits ?? []).Where(credit => credit.Role == "performer")
                .Select(credit => artistById.GetValueOrDefault(credit.ArtistId)).OfType<CatalogArtist>().DistinctBy(artist => artist.Id).ToArray();
        }
    }

    public IReadOnlyList<CatalogSongCredit> GetSongCredits(string songId)
    { lock (gate) return (SongAssociations.GetValueOrDefault(songId)?.Credits ?? []).ToArray(); }

    public CatalogAlbum? GetSongAlbum(string songId)
    {
        lock (gate)
        {
            EnsureIndexes();
            return SongAssociations.GetValueOrDefault(songId)?.AlbumId is { } id ? albumById.GetValueOrDefault(id) : null;
        }
    }

    public IReadOnlyList<string> GetArtistSongIds(string artistId)
    {
        lock (gate) return SongAssociations.Where(pair => pair.Value.Credits.Any(credit => credit.ArtistId == artistId && credit.Role == "performer"))
            .Select(pair => pair.Key).ToArray();
    }

    public IReadOnlyList<CatalogAlbum> GetArtistAlbums(string artistId)
    {
        lock (gate)
        {
            var songs = GetArtistSongIds(artistId).ToHashSet(StringComparer.Ordinal);
            var albumIds = Tracks.Where(track => songs.Contains(track.SongId)).Select(track => track.AlbumId).ToHashSet(StringComparer.Ordinal);
            return Albums.Where(album => albumIds.Contains(album.Id)).OrderBy(album => album.Name, StringComparer.OrdinalIgnoreCase).ThenBy(album => album.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<CatalogTrack> GetAlbumTracks(string albumId)
    {
        lock (gate) return Tracks.Where(track => track.AlbumId == albumId)
            .OrderBy(track => track.DiscNumber is > 0 ? track.DiscNumber.Value : int.MaxValue)
            .ThenBy(track => track.TrackNumber ?? int.MaxValue).ThenBy(track => Key(track.Title), StringComparer.Ordinal)
            .ThenBy(track => track.SongId, StringComparer.Ordinal).ThenBy(track => track.Id, StringComparer.Ordinal).ToArray();
    }

    public CatalogTrack AddAlbumTrack(string albumId, string songId, int? discNumber = null, int? trackNumber = null)
    {
        lock (gate)
        {
            if (!Albums.Any(album => album.Id == albumId)) throw new ArgumentException("专辑不存在。", nameof(albumId));
            if (!SongAssociations.ContainsKey(songId)) throw new ArgumentException("歌曲不存在。", nameof(songId));
            var track = new CatalogTrack(NewId("track"), songId, albumId, Positive(discNumber), Positive(trackNumber));
            Tracks = [.. Tracks, track]; return track;
        }
    }

    public bool RenameArtist(string id, string name)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            int index = Artists.FindIndex(artist => artist.Id == id); if (index < 0) return false;
            var old = Artists[index]; var updated = old with { Name = name.Trim(), Aliases = old.Aliases.Append(old.Name).Distinct().ToArray() };
            Artists = [.. Artists.Select(artist => artist.Id == id ? updated : artist)];
            Albums = [.. Albums.Select(album => album.ArtistId == id ? album with { Artist = updated.Name } : album)];
            return true;
        }
    }

    public bool RenameAlbum(string id, string name)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var old = Albums.FirstOrDefault(album => album.Id == id); if (old is null) return false;
            var updated = old with { Name = name.Trim(), Aliases = old.Aliases.Append(old.Name).Distinct().ToArray() };
            Albums = [.. Albums.Select(album => album.Id == id ? updated : album)]; return true;
        }
    }

    public LibraryCatalog Snapshot()
    {
        lock (gate) return new()
        {
            Artists = Artists.Select(artist => artist with { ExternalIds = artist.ExternalIds.ToArray(), Aliases = artist.Aliases.ToArray() }).ToList(),
            Albums = Albums.Select(album => album with { ExternalIds = album.ExternalIds.ToArray(), Aliases = album.Aliases.ToArray() }).ToList(),
            Tracks = [.. Tracks], SongAssociations = SongAssociations.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Credits = pair.Value.Credits.ToArray() })
        };
    }

    private void EnsureIndexes()
    {
        if (!ReferenceEquals(indexedArtists, Artists)) { artistById = Artists.ToDictionary(artist => artist.Id); indexedArtists = Artists; }
        if (!ReferenceEquals(indexedAlbums, Albums)) { albumById = Albums.ToDictionary(album => album.Id); indexedAlbums = Albums; }
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
    private static string NewId(string kind) => kind + ":" + Guid.NewGuid().ToString("N");
    private static string Key(string text) => string.Join(' ', text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string ExternalKey(MusicExternalId id) => Key(id.Provider) + ":" + Key(id.Kind) + ":" + id.Value.Trim();
    private static MusicExternalId[] CleanIds(IEnumerable<MusicExternalId> ids) => ids
        .Where(id => !string.IsNullOrWhiteSpace(id.Provider) && !string.IsNullOrWhiteSpace(id.Kind) && !string.IsNullOrWhiteSpace(id.Value))
        .Select(id => new MusicExternalId(id.Provider.Trim().ToLowerInvariant(), id.Kind.Trim().ToLowerInvariant(), id.Value.Trim())).Distinct().ToArray();
    private static bool Conflict(IReadOnlyList<MusicExternalId> left, IReadOnlyList<MusicExternalId> right) => left.Any(a => right.Any(b => a.Provider == b.Provider && a.Kind == b.Kind && a.Value != b.Value));
    private static bool CompatibleYear(string left, string right) => left.Length == 0 || right.Length == 0 || left == right;
    private static void AddName<T>(Dictionary<string, List<T>> lookup, string name, T value)
    {
        if (!lookup.TryGetValue(name, out var values)) lookup[name] = values = [];
        if (!values.Contains(value)) values.Add(value);
    }
}
