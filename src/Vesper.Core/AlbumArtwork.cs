namespace Vesper.Core;

public sealed record AlbumArtworkCandidate(string SongId, string LocalIdentity, bool CanReadEmbedded);
public sealed record AlbumArtworkSource(string Key, string CoverUrl = "", string SongId = "", string LocalIdentity = "");

/// <summary>
/// The persisted album image is authoritative. A track's load timing, display order or
/// alternate release artwork never chooses the image for an already-known album.
/// </summary>
public static class AlbumArtwork
{
    public static AlbumArtworkSource? Choose(CatalogAlbum album, IEnumerable<AlbumArtworkCandidate> candidates)
    {
        if (Uri.TryCreate(album.CoverUrl, UriKind.Absolute, out var url) && ArtworkDiskCache.IsAllowed(url))
            return new(album.Id + "\nurl\n" + url.AbsoluteUri, CoverUrl: url.AbsoluteUri);
        var local = candidates.Where(candidate => candidate.CanReadEmbedded)
            .OrderBy(candidate => candidate.SongId, StringComparer.Ordinal).FirstOrDefault();
        return local is null ? null : new(album.Id + "\nlocal\n" + local.SongId + "\n" + local.LocalIdentity,
            SongId: local.SongId, LocalIdentity: local.LocalIdentity);
    }
}
