using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Core;

public sealed record CachedArtistProfile(MusicExternalId Identity, ArtistProfile? Profile, DateTimeOffset CheckedAt, bool Unavailable = false)
{
    // v1 accepts the Commons thumbnail CDN. Retry legacy empty-image successes once
    // without invalidating valid portraits or repeatedly refetching partial results.
    public const int CurrentImageLookupVersion = 1;
    public int ImageLookupVersion { get; init; }
    public static string Key(MusicExternalId id) => id.Provider.ToLowerInvariant() + ":" + id.Kind + ":" + id.Value;
    public bool ShouldRefresh(DateTimeOffset now) => !Unavailable && Profile is { ImageUrl.Length: 0 }
        && ImageLookupVersion < CurrentImageLookupVersion || now - CheckedAt >= (Unavailable ? TimeSpan.FromMinutes(1)
        : Profile is null ? TimeSpan.FromDays(1) : TimeSpan.FromDays(30));
}

public sealed record SongAssetState(string Key, DateTimeOffset CheckedAt, bool Unavailable)
{
    public bool MissingArtwork { get; init; }
    public DateTimeOffset? CreditsCheckedAt { get; init; }
    public bool ShouldRefresh(string key, DateTimeOffset now) => key != Key
        || now - CheckedAt >= (Unavailable ? TimeSpan.FromMinutes(1) : MissingArtwork ? TimeSpan.FromDays(1) : TimeSpan.FromDays(30));
    public static string Identity(SongLookupInput input)
    {
        var metadata = input.Details.Effective("");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { Version = 1, input.Key, metadata.Title, metadata.Artist, metadata.Album, metadata.CoverUrl,
            metadata.ExternalIds, metadata.ArtistCredits }))));
    }
}
