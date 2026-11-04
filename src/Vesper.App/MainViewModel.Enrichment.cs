using System.Security.Cryptography;
using System.Text;
using Vesper.Core;

namespace Vesper.App;

public sealed record LibraryEnrichmentCandidate(Song Song, string SourceKey, SongDetails Details);

public sealed partial class MainViewModel
{
    public event EventHandler? LibrarySourcesChanged;
    private bool enrichmentBusy;
    private string enrichmentLabel = "", enrichmentTooltip = "";
    private readonly CancellationTokenSource backgroundMetadataLifetime = new();
    private Task backgroundMetadataWrite = Task.CompletedTask;
    private TaskCompletionSource? backgroundMetadataFlush;
    private long backgroundMetadataRevision, persistedMetadataRevision;
    private bool backgroundMetadataWriting, backgroundMetadataRetryScheduled;
    private bool backgroundSearchRefreshRequested, backgroundSearchRefreshing;
    private Task backgroundSearchRefresh = Task.CompletedTask;

    public void SetEnrichmentActivity(bool isBusy, string label, string tooltip)
    {
        if (enrichmentBusy == isBusy && enrichmentLabel == label && enrichmentTooltip == tooltip) return;
        enrichmentBusy = isBusy; enrichmentLabel = label; enrichmentTooltip = tooltip;
        Notify(nameof(HasBackgroundActivity)); Notify(nameof(Idle)); NotifyActivity();
    }

    public IReadOnlyList<LibraryEnrichmentCandidate> CaptureEnrichmentCandidates()
    {
        (Song Song, SongDetails Details)[] snapshot;
        Dictionary<string, FileScanCache> scanCache;
        lock (searchIndexGate)
        {
            if (!initialized || disposed || ReadOnly) return [];
            snapshot = Library.Songs.Where(song => availability.GetValueOrDefault(song.Id)).Select(song =>
                (song with { Sources = new List<string>(song.Sources), KnownSources = new List<string>(song.KnownSources ?? []) },
                    Library.Details.GetValueOrDefault(song.Id) ?? new())).ToArray();
            scanCache = new(Library.ScanCache, StringComparer.OrdinalIgnoreCase);
        }
        // Hashing and work-list construction do not hold the lock used by foreground operations.
        return snapshot.Select(value => new LibraryEnrichmentCandidate(value.Song,
            EnrichmentSourceKey(value.Song, scanCache), value.Details)).ToArray();
    }
    public string? GetEnrichmentSourceKey(string songId)
    {
        lock (searchIndexGate)
        {
            if (disposed || ReadOnly || !availability.GetValueOrDefault(songId)) return null;
            return songItems.TryGetValue(songId, out var item) ? EnrichmentSourceKey(item.Song) : null;
        }
    }
    public CachedArtistProfile? GetCachedArtistProfile(MusicExternalId id)
    {
        lock (searchIndexGate) return Library.ArtistProfiles.GetValueOrDefault(CachedArtistProfile.Key(id));
    }

    public Task<bool> SaveBackgroundAutomaticLookupAsync(string id, SongLookupInput expected, SongLookupResult result,
        Func<bool>? canPublish = null)
    {
        lock (searchIndexGate)
        {
            if (disposed || ReadOnly || !(canPublish?.Invoke() ?? true) || GetLookupInput(id)?.Key != expected.Key) return Task.FromResult(false);
            var current = Library.Details.GetValueOrDefault(id) ?? new();
            if (current.Online != expected.Details.Online) return Task.FromResult(false);
            var updated = current with
            {
                Online = current.Online is null || current.Online.SourceUrl.Contains("lrclib.net", StringComparison.OrdinalIgnoreCase)
                    ? result.Metadata ?? current.Online : current.Online,
                OnlineLyrics = current.OnlineLyrics ?? result.Lyrics,
                Lookup = new(expected.Key, clock.GetUtcNow(), result.MetadataOutcome, result.LyricsOutcome)
            };
            Library.Details[id] = updated;
            backgroundMetadataRevision++;
            // Lyrics and retry timestamps do not change the searchable projection.
            if (updated.Online != current.Online) QueueBackgroundSearchRefresh();
            QueueBackgroundMetadataWrite();
            FindSong(id)?.ApplyDetails(updated);
            if (updated.Online != current.Online) RefreshDetailsPresentation();
        }
        LibrarySourcesChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public Task<bool> SaveBackgroundAssetsAsync(string id, string sourceKey, SongLookupInput expected,
        IReadOnlyList<MusicArtistCredit> credits, IReadOnlyList<CachedArtistProfile> profiles, bool unavailable, Func<bool> canPublish,
        DateTimeOffset? creditsCheckedAt = null, bool missingArtwork = false)
    {
        lock (searchIndexGate)
        {
            if (disposed || ReadOnly || !canPublish() || GetEnrichmentSourceKey(id) != sourceKey
                || GetLookupInput(id)?.Key != expected.Key) return Task.FromResult(false);
            var current = Library.Details.GetValueOrDefault(id) ?? new();
            if (current.Online != expected.Details.Online || current.Local != expected.Details.Local) return Task.FromResult(false);
            SongMetadata? AddCredits(SongMetadata? metadata)
            {
                if (metadata is null || credits.Count == 0) return metadata;
                var combined = metadata.ArtistCredits.ToList();
                foreach (var credit in credits)
                {
                    int index = combined.FindIndex(old => old.Role == credit.Role && (
                        (old.ExternalIds ?? []).Intersect(credit.ExternalIds ?? []).Any()
                        || old.Name == credit.Name && !(old.ExternalIds ?? []).Any(prior => (credit.ExternalIds ?? []).Any(next =>
                            prior.Provider == next.Provider && prior.Kind == next.Kind && prior.Value != next.Value))));
                    if (index < 0) combined.Add(credit);
                    else
                    {
                        var previous = combined[index];
                        var ids = (previous.ExternalIds ?? []).Concat(credit.ExternalIds ?? []).Distinct().ToArray();
                        if (!ids.SequenceEqual(previous.ExternalIds ?? [])) combined[index] = previous with { ExternalIds = ids };
                    }
                }
                return combined.SequenceEqual(metadata.ArtistCredits) ? metadata : metadata with { ArtistCredits = combined.ToArray() };
            }
            var online = AddCredits(current.Online);
            var local = current.Online is null ? AddCredits(current.Local) : current.Local;
            var updated = current with { Online = online, Local = local };
            string assetKey = SongAssetState.Identity(expected with { Details = updated });
            updated = updated with { Assets = new(assetKey, clock.GetUtcNow(), unavailable)
                { MissingArtwork = missingArtwork, CreditsCheckedAt = creditsCheckedAt
                    ?? (current.Assets?.Key == assetKey ? current.Assets.CreditsCheckedAt : null) } };
            Library.Details[id] = updated;
            foreach (var profile in profiles) Library.ArtistProfiles[CachedArtistProfile.Key(profile.Identity)] = profile;
            backgroundMetadataRevision++;
            if (online != current.Online || local != current.Local) QueueBackgroundSearchRefresh();
            QueueBackgroundMetadataWrite();
            FindSong(id)?.ApplyDetails(updated);
        }
        return Task.FromResult(true);
    }
    private string EnrichmentSourceKey(Song song) => EnrichmentSourceKey(song, Library.ScanCache);
    private static string EnrichmentSourceKey(Song song, IReadOnlyDictionary<string, FileScanCache> scanCache)
    {
        string identity = string.Join('\n', song.Sources.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var cache = scanCache.GetValueOrDefault(path);
            return path.ToUpperInvariant() + "\t" + cache?.Length + "\t" + cache?.ModifiedUtcTicks;
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public Task<bool> SaveBackgroundLocalMetadataAsync(string id, string expectedSourceKey,
        LibraryMetadataRead read, Func<bool> canPublish)
    {
        LibrarySongItem? song;
        lock (searchIndexGate)
        {
            song = FindSong(id);
            if (!initialized || disposed || ReadOnly || !canPublish() || GetEnrichmentSourceKey(id) != expectedSourceKey
                || song?.Song.Sources.Contains(read.Path, StringComparer.OrdinalIgnoreCase) != true) return Task.FromResult(false);
            var previous = Library.Details.GetValueOrDefault(id) ?? new();
            Library.Details[id] = previous with { Local = read.Metadata, LocalReadKey = expectedSourceKey, LocalResolution = read.Resolution };
            backgroundMetadataRevision++;
            QueueBackgroundSearchRefresh();
            QueueBackgroundMetadataWrite();
        }
        song.ApplyDetails(Library.Details[id]);
        song.Resolution = read.Resolution; song.MetadataLoaded = true;
        RefreshDetailsPresentation();
        LibrarySourcesChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    private void QueueBackgroundSearchRefresh()
    {
        backgroundSearchRefreshRequested = true;
        if (backgroundSearchRefreshing) return;
        backgroundSearchRefreshing = true;
        backgroundSearchRefresh = RefreshBackgroundSearchAsync();
    }
    private async Task RefreshBackgroundSearchAsync()
    {
        try
        {
            await Task.Delay(750, backgroundMetadataLifetime.Token);
            while (!disposed)
            {
                Task? pending;
                lock (searchIndexGate) pending = searchBuildTask;
                if (pending is not null) await pending;
                bool started;
                lock (searchIndexGate)
                {
                    if (disposed || ReadOnly || !backgroundSearchRefreshRequested) return;
                    if (searchIndexBuilding) continue;
                    backgroundSearchRefreshRequested = false;
                    started = MarkSearchIndexDirty();
                }
                AnnounceSearchIndexBuild(started);
                // Complete each captured batch before requesting the next. A steady
                // stream of tag reads must not keep invalidating every index build.
            }
        }
        catch (OperationCanceledException) when (backgroundMetadataLifetime.IsCancellationRequested) { }
        finally
        {
            lock (searchIndexGate)
            {
                backgroundSearchRefreshing = false;
                if (!disposed && backgroundSearchRefreshRequested) QueueBackgroundSearchRefresh();
            }
        }
    }

    // Local tags arrive much faster than online matches. Coalesce their durable
    // writes instead of serializing the entire library once for every file.
    private void QueueBackgroundMetadataWrite()
    {
        if (backgroundMetadataWriting || disposed || ReadOnly) return;
        backgroundMetadataWriting = true;
        backgroundMetadataFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        backgroundMetadataWrite = WriteBackgroundMetadataAsync(backgroundMetadataFlush.Task);
    }
    private async Task WriteBackgroundMetadataAsync(Task flush)
    {
        try
        {
            await Task.WhenAny(Task.Delay(1500, backgroundMetadataLifetime.Token), flush);
            while (!disposed)
            {
                // Wait for the captured index batch, not the refresh pump: a continuous
                // stream of tags must not postpone durable progress until the scan ends.
                Task? indexBatch;
                lock (searchIndexGate) indexBatch = searchBuildTask;
                if (indexBatch is not null) await indexBatch;
                long revision;
                Task write;
                lock (searchIndexGate)
                {
                    revision = backgroundMetadataRevision;
                    if (revision == persistedMetadataRevision) return;
                    write = Store.SaveAsync(Library, reconcileCatalog: false);
                }
                try
                {
                    await write;
                    lock (searchIndexGate) persistedMetadataRevision = Math.Max(persistedMetadataRevision, revision);
                }
                catch (Exception ex)
                {
                    ReportProblem($"后台歌曲资料保存失败，将稍后重试：{ex.Message}");
                    if (!backgroundMetadataRetryScheduled)
                    { backgroundMetadataRetryScheduled = true; _ = RetryBackgroundMetadataWriteAsync(); }
                    return;
                }
                lock (searchIndexGate) if (backgroundMetadataRevision == persistedMetadataRevision) return;
                await Task.Delay(1500, backgroundMetadataLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (backgroundMetadataLifetime.IsCancellationRequested) { }
        finally
        {
            lock (searchIndexGate)
            {
                backgroundMetadataWriting = false;
                if (!disposed && !backgroundMetadataRetryScheduled && backgroundMetadataRevision != persistedMetadataRevision)
                    QueueBackgroundMetadataWrite();
            }
        }
    }
    private async Task RetryBackgroundMetadataWriteAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), clock, backgroundMetadataLifetime.Token);
            lock (searchIndexGate)
            {
                backgroundMetadataRetryScheduled = false;
                if (backgroundMetadataRevision != persistedMetadataRevision) QueueBackgroundMetadataWrite();
            }
        }
        catch (OperationCanceledException) when (backgroundMetadataLifetime.IsCancellationRequested) { }
    }
    public async Task<bool> FlushBackgroundMetadataAsync()
    {
        while (true)
        {
            Task indexRefresh;
            lock (searchIndexGate) indexRefresh = backgroundSearchRefresh;
            await indexRefresh;
            lock (searchIndexGate)
                if (disposed || ReferenceEquals(indexRefresh, backgroundSearchRefresh) && !backgroundSearchRefreshing) break;
        }
        Task write;
        lock (searchIndexGate)
        {
            if (!backgroundMetadataWriting && backgroundMetadataRevision != persistedMetadataRevision) QueueBackgroundMetadataWrite();
            backgroundMetadataFlush?.TrySetResult();
            write = backgroundMetadataWrite;
        }
        await write;
        lock (searchIndexGate) return backgroundMetadataRevision == persistedMetadataRevision;
    }
}
