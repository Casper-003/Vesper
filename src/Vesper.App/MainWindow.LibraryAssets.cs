using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private ArtistProfileClient? artistProfileClient;
    private readonly Dictionary<string, BitmapImage> artistImages = [];
    private string? pendingArtistImage;

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
                        saved = new(identity, profile, now);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    { saved = new(identity, saved?.Profile, now, Unavailable: true); }
                    profiles.Add(saved);
                }
                failed |= saved.Unavailable;
                if (saved.Profile?.ImageUrl is { Length: > 0 } image)
                    await CacheImageAsync(image);
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
                if (published && profiles.Count > 0) RequestEntityRefresh();
            }).ConfigureAwait(false);
        else if (coverReady)
            await DispatchEnrichmentAsync(() =>
            { if (Current()) QueueVisibleArtworkForSong(ticket.SongId); return Task.CompletedTask; }).ConfigureAwait(false);
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

    private object? GetArtistImage(CatalogArtist artist)
    {
        var profile = artist.ExternalIds.Select(viewModel.GetCachedArtistProfile)
            .Select(value => value?.Profile).FirstOrDefault(value => value?.ImageUrl.Length > 0);
        if (profile is null || !Uri.TryCreate(profile.ImageUrl, UriKind.Absolute, out var url) || !ArtworkDiskCache.IsAllowed(url)) return null;
        string path = artworkCache.GetCachePath(url);
        if (artistImages.TryGetValue(path, out var image)) return image;
        if (pendingArtistImage != path) { pendingArtistImage = path; _ = LoadArtistImageAsync(path); }
        return null;
    }

    private async Task LoadArtistImageAsync(string path)
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
            RequestEntityRefresh();
        }
        catch (Exception) { }
        finally
        {
            if (entered) coverSlots.Release();
            if (pendingArtistImage == path) pendingArtistImage = null;
        }
    }
}
