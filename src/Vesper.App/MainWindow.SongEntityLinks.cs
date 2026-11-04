using Vesper.Core;

namespace Vesper.App;

internal sealed record SongEntityLinkTarget(string Id, string Name, IReadOnlyList<string> Aliases);
internal sealed record SongEntityLinkState(string SongId, string ArtistText, string AlbumText,
    IReadOnlyList<SongEntityLinkTarget> Artists, SongEntityLinkTarget? Album);

public sealed partial class MainWindow
{
    internal SongEntityLinkState GetSongEntityLinks(string id)
    {
        // FindSong resolves duplicate-file aliases before asking for memberships.
        // A displayed name alone is never used to find/create a navigation target.
        var song = viewModel.FindSong(id);
        if (song is null) return new("", "", "", [], null);
        string artistText = song.ArtistDetail, albumText = song.Album;
        bool hasArtist = HasKnownSongEntityName(artistText, false), hasAlbum = HasKnownSongEntityName(albumText, true);
        if (!hasArtist && !hasAlbum) return new(song.Song.Id, artistText, albumText, [], null);
        var catalog = viewModel.GetCatalogSnapshot();
        var artists = hasArtist ? catalog.GetSongArtists(song.Song.Id)
            .Where(artist => HasKnownSongEntityName(artist.Name, false))
            .Select(artist => new SongEntityLinkTarget(artist.Id, artist.Name, artist.Aliases)).ToArray() : [];
        var album = hasAlbum ? catalog.GetSongAlbum(song.Song.Id) : null;
        return new(song.Song.Id, artistText, albumText, artists,
            album is not null && HasKnownSongEntityName(album.Name, true) ? new(album.Id, album.Name, album.Aliases) : null);
    }

    internal void OpenSongArtist(string songId, string artistId)
    {
        // Re-read the current song association at activation time. A result from
        // before a manual correction must not navigate to the old artist.
        if (GetSongEntityLinks(songId).Artists.Any(artist => artist.Id == artistId)) OpenArtist(artistId);
    }

    internal void OpenSongAlbum(string songId, string albumId)
    {
        if (GetSongEntityLinks(songId).Album?.Id == albumId) OpenAlbum(albumId);
    }

    private static bool HasKnownSongEntityName(string value, bool album)
    {
        string text = value.Trim();
        if (text.Length == 0 || text.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
        return album ? !text.Equals("未知专辑", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("unknown album", StringComparison.OrdinalIgnoreCase)
            : !text.Equals("未知艺人", StringComparison.OrdinalIgnoreCase) && !text.Equals("未知歌手", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("未知艺术家", StringComparison.OrdinalIgnoreCase) && !text.Equals("unknown artist", StringComparison.OrdinalIgnoreCase);
    }
}
