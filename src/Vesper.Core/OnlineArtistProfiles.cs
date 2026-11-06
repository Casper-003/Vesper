using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vesper.Core;

public sealed record ArtistProfile(string Name, string Description, string ImageUrl, string SourceUrl)
{
    public string ImageSourceUrl { get; init; } = "";
}

/// <summary>Fetch a known artist's profile without guessing identities from a display name.</summary>
public sealed class ArtistProfileClient(HttpClient client, MusicBrainzClient musicBrainz)
{
    private const int MaximumJsonBytes = 2 * 1024 * 1024;

    public Task<ArtistProfile?> GetAsync(MusicExternalId identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (identity.Kind != "artist") return Task.FromResult<ArtistProfile?>(null);
        return identity.Provider.ToLowerInvariant() switch
        {
            "musicbrainz" when Guid.TryParse(identity.Value, out _) => GetMusicBrainzAsync(identity.Value, token),
            "deezer" when long.TryParse(identity.Value, out long id) && id > 0 => GetDeezerAsync(id, token),
            _ => Task.FromResult<ArtistProfile?>(null)
        };
    }

    private async Task<ArtistProfile?> GetMusicBrainzAsync(string id, CancellationToken token)
    {
        using var artist = await musicBrainz.GetAsync($"artist/{id}?fmt=json&inc=url-rels", token).ConfigureAwait(false);
        var entry = artist.RootElement;
        if (!string.Equals(MusicBrainzClient.Text(entry, "id"), id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("艺人资料身份不一致。");
        string name = Limit(MusicBrainzClient.Text(entry, "name"), 512);
        if (name.Length == 0) return null;
        string description = Limit(MusicBrainzClient.Text(entry, "disambiguation"), 2000);
        string source = "https://musicbrainz.org/artist/" + id;
        string imageUrl = "", imageSource = "";
        if (entry.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            string? wikidataId = relations.EnumerateArray().Where(relation => MusicBrainzClient.Text(relation, "type") == "wikidata"
                    && relation.TryGetProperty("url", out _))
                .Select(relation => WikidataId(MusicBrainzClient.Text(relation.GetProperty("url"), "resource")))
                .FirstOrDefault(value => value is not null);
            if (wikidataId is not null)
            {
                using var data = await GetJsonAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=descriptions%7Cclaims"
                    + "&languages=zh-hans%7Czh%7Cen&ids=" + wikidataId, token).ConfigureAwait(false);
                if (data.RootElement.TryGetProperty("entities", out var entities)
                    && entities.TryGetProperty(wikidataId, out var entity) && !entity.TryGetProperty("missing", out _))
                {
                    if (entity.TryGetProperty("descriptions", out var descriptions))
                        foreach (string language in new[] { "zh-hans", "zh", "en" })
                            if (descriptions.TryGetProperty(language, out var localized)
                                && MusicBrainzClient.Text(localized, "value") is { Length: > 0 } text)
                            { description = Limit(text, 2000); source = "https://www.wikidata.org/wiki/" + wikidataId; break; }
                    if (entity.TryGetProperty("claims", out var claims) && claims.TryGetProperty("P18", out var images)
                        && images.ValueKind == JsonValueKind.Array)
                    {
                        string file = images.EnumerateArray().Where(image => MusicBrainzClient.Text(image, "rank") != "deprecated")
                            .OrderByDescending(image => MusicBrainzClient.Text(image, "rank") == "preferred")
                            .Select(ImageFile).FirstOrDefault(value => value.Length > 0) ?? "";
                        if (file.Length > 0)
                        {
                            (imageUrl, imageSource) = await GetCommonsImageAsync(file, token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        return new(name, description, imageUrl, source) { ImageSourceUrl = imageSource };
    }

    private async Task<ArtistProfile?> GetDeezerAsync(long id, CancellationToken token)
    {
        using var json = await GetJsonAsync("https://api.deezer.com/artist/" + id, token).ConfigureAwait(false);
        var entry = json.RootElement;
        if (entry.TryGetProperty("error", out _)) throw new HttpRequestException("艺人资料服务暂不可用。");
        if (!entry.TryGetProperty("id", out var returnedId) || !returnedId.TryGetInt64(out long actual) || actual != id)
            throw new InvalidDataException("艺人资料身份不一致。");
        string name = Limit(MusicBrainzClient.Text(entry, "name"), 512);
        if (name.Length == 0) return null;
        string image = new[] { "picture_big", "picture_medium", "picture" }.Select(key => Https(MusicBrainzClient.Text(entry, key)))
            .FirstOrDefault(value => value.Length > 0) ?? "";
        string source = "https://www.deezer.com/artist/" + id;
        return new(name, "", image, source) { ImageSourceUrl = source };
    }

    private async Task<(string Url, string Source)> GetCommonsImageAsync(string file, CancellationToken token)
    {
        using var json = await GetJsonAsync("https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2"
            + "&prop=imageinfo&iiprop=url&iiurlwidth=512&iiurlheight=512&titles=" + Uri.EscapeDataString("File:" + file), token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array) return ("", "");
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0) continue;
            // Do not fall back to an unbounded original photograph when thumbnail generation fails.
            string url = Https(MusicBrainzClient.Text(info[0], "thumburl"));
            if (Uri.TryCreate(url, UriKind.Absolute, out var image)
                && image.Host is "upload.wikimedia.org" or "thumb.wikimedia.org")
                return (url, "https://commons.wikimedia.org/wiki/File:" + Uri.EscapeDataString(file));
        }
        return ("", "");
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Vesper/0.1 (+https://github.com/Casper-003/Vesper)");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("error", out _))
        { json.Dispose(); throw new HttpRequestException("艺人资料服务暂不可用。"); }
        return json;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumJsonBytes) throw new InvalidDataException("资料响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumJsonBytes) throw new InvalidDataException("资料响应过大。");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: token).ConfigureAwait(false);
    }

    private static string? WikidataId(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Host != "www.wikidata.org"
            || url.Scheme is not ("https" or "http")) return null;
        string path = url.AbsolutePath;
        if (!path.StartsWith("/wiki/", StringComparison.Ordinal) && !path.StartsWith("/entity/", StringComparison.Ordinal)) return null;
        string id = path[(path.LastIndexOf('/') + 1)..];
        return Regex.IsMatch(id, "^Q[1-9][0-9]{0,14}$", RegexOptions.CultureInvariant) ? id : null;
    }

    private static string ImageFile(JsonElement image)
    {
        if (!image.TryGetProperty("mainsnak", out var snak) || MusicBrainzClient.Text(snak, "snaktype") != "value"
            || !snak.TryGetProperty("datavalue", out var data)) return "";
        string file = MusicBrainzClient.Text(data, "value");
        return file.Length is > 0 and <= 512 ? file : "";
    }

    private static string Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && value.Length <= 4096 ? value : "";
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
