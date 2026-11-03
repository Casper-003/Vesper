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
        lock (searchIndexGate)
        {
            if (!initialized || disposed || ReadOnly) return [];
            return Library.Songs.Where(song => availability.GetValueOrDefault(song.Id)).Select(song =>
                new LibraryEnrichmentCandidate(song with { Sources = [.. song.Sources], KnownSources = [.. song.KnownSources ?? []] },
                    EnrichmentSourceKey(song), Library.Details.GetValueOrDefault(song.Id) ?? new())).ToArray();
        }
    }
    public string? GetEnrichmentSourceKey(string songId)
    {
        lock (searchIndexGate)
        {
            if (disposed || ReadOnly || !availability.GetValueOrDefault(songId)) return null;
            return songItems.TryGetValue(songId, out var item) ? EnrichmentSourceKey(item.Song) : null;
        }
    }
    private string EnrichmentSourceKey(Song song)
    {
        string identity = string.Join('\n', song.Sources.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var cache = Library.ScanCache.GetValueOrDefault(path);
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
            await Task.Delay(150, backgroundMetadataLifetime.Token);
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
            await Task.WhenAny(Task.Delay(500, backgroundMetadataLifetime.Token), flush);
            while (!disposed)
            {
                long revision;
                Task write;
                lock (searchIndexGate)
                {
                    revision = backgroundMetadataRevision;
                    if (revision == persistedMetadataRevision) return;
                    write = Store.SaveAsync(Library);
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
                await Task.Delay(500, backgroundMetadataLifetime.Token);
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
