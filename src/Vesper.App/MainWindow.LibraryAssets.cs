using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private ArtistProfileClient? artistProfileClient;
    private readonly Dictionary<string, BitmapImage> artistImages = [];
    private readonly Dictionary<string, Task> artistImageLoads = [];
    private readonly Dictionary<string, DateTimeOffset> artistImageRetries = [];

    private async Task<LibraryEnrichmentOutcome> SynchronizeLibraryAssetsAsync(LibraryEnrichmentTicket ticket, string sourceKey)
    {
        SongLookupInput? input = null;
        bool Current() => !closed && ticket.IsCurrent && viewModel.GetEnrichmentSourceKey(ticket.SongId) == sourceKey;
        await DispatchEnrichmentAsync(() =>
        {
            if (Current() && viewModel.GetLookupInput(ticket.SongId) is { } current
                && ticket.Key.StartsWith(sourceKey + "\n" + current.Key + "\n", StringComparison.Ordinal)) input = current;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (input is null) return LibraryEnrichmentOutcome.Completed;
        var token = ticket.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;
        bool due = input.Details.Assets?.ShouldRefresh(SongAssetState.Identity(input), now) != false;
        if (!due && input.Details.Assets is { } savedState && (savedState.Unavailable || savedState.MissingArtwork))
            return LibraryEnrichmentOutcome.Retry;
        bool failed = false;
        bool missingArtwork = false;
        bool coverReady = false;
        bool artistArtworkReady = false;
        DateTimeOffset? creditsCheckedAt = input.Details.Assets?.Key == SongAssetState.Identity(input)
            ? input.Details.Assets.CreditsCheckedAt : null;
        var credits = new List<MusicArtistCredit>();
        var profiles = new List<CachedArtistProfile>();
        var metadata = input.Details.Effective("");
        try
        {
            // The online lane is serial; share foreground limits without creating tasks per image.
            if (due && (creditsCheckedAt is null || now - creditsCheckedAt >= TimeSpan.FromDays(30))
                && metadata.ExternalIds.FirstOrDefault(id => id.Provider == "musicbrainz" && id.Kind == "recording") is { } recording)
            {
                try
                {
                    credits.AddRange((await SongLookup.GetRecordingCreditsAsync(recording.Value, deadline.Token).ConfigureAwait(false))
                        .Where(credit => credit.Role != "performer"));
                    creditsCheckedAt = now;
                }
                catch (Exception) when (!token.IsCancellationRequested) { failed = true; }
            }
            var identities = metadata.ArtistCredits.Concat(credits).SelectMany(credit => credit.ExternalIds ?? [])
                .Concat(metadata.ExternalIds.Where(id => id.Kind == "artist")).Where(id => id.Kind == "artist").Distinct().ToArray();
            foreach (var identity in identities)
            {
                token.ThrowIfCancellationRequested();
                var saved = viewModel.GetCachedArtistProfile(identity);
                if (saved is null || saved.ShouldRefresh(now))
                {
                    try
                    {
                        var profile = await artistProfileClient!.GetAsync(identity, deadline.Token).ConfigureAwait(false);
                        saved = new(identity, profile, now) { ImageLookupVersion = CachedArtistProfile.CurrentImageLookupVersion };
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    { saved = new(identity, saved?.Profile, now, Unavailable: true)
                        { ImageLookupVersion = CachedArtistProfile.CurrentImageLookupVersion }; }
                    profiles.Add(saved);
                }
                failed |= saved.Unavailable;
                if (saved.Profile?.ImageUrl is { Length: > 0 } image)
                    artistArtworkReady |= await CacheImageAsync(image);
                if (saved.Profile?.LogoUrl is { Length: > 0 } logo)
                    artistArtworkReady |= await CacheImageAsync(logo);
            }
            if (metadata.CoverUrl.Length > 0)
            {
                string cover = metadata.CoverUrl;
                if (cover.EndsWith("/front-500", StringComparison.Ordinal))
                {
                    try
                    { await artworkCache.GetAsync(new Uri(cover[..^3] + "1200"), deadline.Token).ConfigureAwait(false); coverReady = true; }
                    catch (Exception) when (!token.IsCancellationRequested) { coverReady = await CacheImageAsync(cover); }
                }
                else coverReady = await CacheImageAsync(cover);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { failed = true; }
        token.ThrowIfCancellationRequested();
        bool published = false;
        bool writeNeeded = due || profiles.Count > 0 || failed || missingArtwork;
        // Timestamp-only cached hits require no model write or UI notification.
        if (writeNeeded)
            await DispatchEnrichmentAsync(async () =>
            {
                published = await viewModel.SaveBackgroundAssetsAsync(ticket.SongId, sourceKey, input, credits, profiles, failed, Current,
                    creditsCheckedAt, missingArtwork);
                if (published && coverReady) QueueVisibleArtworkForSong(ticket.SongId);
                if (published && (profiles.Count > 0 || artistArtworkReady)) RequestEntityRefresh(artistAssetsChanged: true);
            }).ConfigureAwait(false);
        else if (coverReady || artistArtworkReady)
            await DispatchEnrichmentAsync(() =>
            {
                if (Current())
                {
                    if (coverReady) QueueVisibleArtworkForSong(ticket.SongId);
                    if (artistArtworkReady) RequestEntityRefresh(artistAssetsChanged: true);
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        return failed || missingArtwork || writeNeeded && !published ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;

        async Task<bool> CacheImageAsync(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !ArtworkDiskCache.IsAllowed(url)) return false;
            try { await artworkCache.GetAsync(url, deadline.Token).ConfigureAwait(false); return true; }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            { missingArtwork = true; }
            catch (Exception) when (!token.IsCancellationRequested) { failed = true; }
            return false;
        }
    }

    private object? GetArtistImage(CatalogArtist artist, bool preferLogo = false) => GetArtistImage(artist, preferLogo, out _);

    private object? GetArtistImage(CatalogArtist artist, bool preferLogo, out bool isLogo)
    {
        isLogo = false;
        var profiles = artist.ExternalIds.Select(viewModel.GetCachedArtistProfile).Select(value => value?.Profile).OfType<ArtistProfile>().ToArray();
        var candidates = (preferLogo ? profiles.Select(profile => (Url: profile.LogoUrl, Logo: true)) : [])
            .Concat(profiles.Select(profile => (Url: profile.ImageUrl, Logo: false)));
        bool requested = false;
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url) || !ArtworkDiskCache.IsAllowed(url)) continue;
            string path = artworkCache.GetCachePath(url);
            if (artistImages.TryGetValue(path, out var image)) { isLogo = candidate.Logo; return image; }
            if (!requested && (!artistImageRetries.TryGetValue(path, out var retry) || retry <= DateTimeOffset.UtcNow) && File.Exists(path))
            { requested = true; _ = LoadArtistImageAsync(path); }
        }
        return null;
    }

    private Task LoadArtistImageAsync(string path)
    {
        if (closed || artistImages.ContainsKey(path)) return Task.CompletedTask;
        if (artistImageLoads.TryGetValue(path, out var pending)) return pending;
        var work = ReadArtistImageAsync(path); artistImageLoads[path] = work;
        _ = ForgetArtistImageAsync(path, work); return work;
    }
    private async Task ForgetArtistImageAsync(string path, Task work)
    { await work; if (artistImageLoads.GetValueOrDefault(path) == work) artistImageLoads.Remove(path); }

    private async Task ReadArtistImageAsync(string path)
    {
        if (closed || artistImages.ContainsKey(path)) return;
        bool entered = false;
        try
        {
            // Only a visible entity asks for a bitmap. Background synchronization never decodes.
            await coverSlots.WaitAsync(metadataCancellation.Token); entered = true;
            if (closed || artistImages.ContainsKey(path)) return;
            if (!await Task.Run(() => File.Exists(path) && new FileInfo(path).Length is > 0 and <= ArtworkDiskCache.MaximumBytes)) return;
            byte[] bytes = await File.ReadAllBytesAsync(path, metadataCancellation.Token);
            uint swatch = await ReadArtworkSwatchAsync(bytes, metadataCancellation.Token);
            var image = new BitmapImage { DecodePixelWidth = 600, DecodePixelHeight = 600 };
            using var source = new MemoryStream(bytes).AsRandomAccessStream();
            await image.SetSourceAsync(source);
            if (closed) return;
            RememberArtworkSwatch(image, swatch);
            if (artistImages.Count >= 8) artistImages.Remove(artistImages.Keys.First());
            artistImages[path] = image;
            artistImageRetries.Remove(path);
            RequestEntityRefresh();
        }
        catch (Exception)
        {
            if (artistImageRetries.Count >= 64) artistImageRetries.Remove(artistImageRetries.Keys.First());
            artistImageRetries[path] = DateTimeOffset.UtcNow.AddMinutes(1);
            RequestEntityRefresh();
        }
        finally
        {
            if (entered) coverSlots.Release();
        }
    }
}
