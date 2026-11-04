using Vesper.Core;

namespace Vesper.App;

public sealed record LibraryMetadataRead(string Path, SongMetadata Metadata, string Resolution = "");

public sealed record LibraryEnrichmentControllerState(LibraryEnrichmentState Local, LibraryEnrichmentState Online)
{
    public LibraryEnrichmentState Video { get; init; } = new(0, 0, 0, 0);
    public bool IsBusy => Local.IsBusy || Online.IsBusy || Video.IsBusy;
    public string Label => Local.IsBusy ? "读取本地歌曲资料" : Online.IsBusy ? "补全歌曲资料" : Video.IsBusy ? "匹配歌曲 MV" : "就绪";
    public string Tooltip => $"本地资料：{Local.ActiveCount} 首读取中，{Local.PendingCount} 首等待；"
        + $"联网资料：{Online.ActiveCount} 首处理中，{Online.PendingCount} 首等待。"
        + $"MV 匹配：{Video.ActiveCount} 首处理中，{Video.PendingCount} 首等待。"
        + (Local.WaitingRetryCount + Online.WaitingRetryCount + Video.WaitingRetryCount > 0
            ? $"{Local.WaitingRetryCount + Online.WaitingRetryCount + Video.WaitingRetryCount} 首稍后重试。" : "")
        + "可继续浏览、点歌和管理目录。";
}

/// <summary>
/// Library-wide data production, independent of views. The host supplies media/network readers
/// and a dispatcher; only detached inputs leave that dispatcher, and every write rechecks authority.
/// </summary>
public sealed class LibraryEnrichmentController : IDisposable
{
    private static readonly TimeSpan SynchronizationDelay = TimeSpan.FromSeconds(1);
    private readonly MainViewModel model;
    private readonly Func<Song, CancellationToken, Task<LibraryMetadataRead?>> readLocal;
    private readonly Func<SongLookupInput, CancellationToken, Task<SongLookupResult>> lookup;
    private readonly Func<LibraryEnrichmentTicket, string, Task<LibraryEnrichmentOutcome>>? synchronizeAssets;
    private readonly Func<LibraryEnrichmentTicket, string, Task<LibraryEnrichmentOutcome>>? synchronizeVideo;
    private readonly Func<Func<Task>, Task> dispatch;
    private readonly TimeProvider clock;
    private readonly LibraryEnrichmentQueue queue;
    private readonly LibraryEnrichmentQueue? videoQueue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Task synchronization = Task.CompletedTask;
    private bool started, disposed, synchronizationRequested, synchronizing;

    public LibraryEnrichmentController(MainViewModel model,
        Func<Song, CancellationToken, Task<LibraryMetadataRead?>> readLocal,
        Func<SongLookupInput, CancellationToken, Task<SongLookupResult>> lookup,
        Func<Func<Task>, Task>? dispatch = null, TimeProvider? clock = null,
        Func<LibraryEnrichmentTicket, string, Task<LibraryEnrichmentOutcome>>? synchronizeAssets = null,
        int onlineConcurrency = 1,
        Func<LibraryEnrichmentTicket, string, Task<LibraryEnrichmentOutcome>>? synchronizeVideo = null)
    {
        this.model = model;
        this.readLocal = readLocal;
        this.lookup = lookup;
        this.synchronizeAssets = synchronizeAssets;
        this.synchronizeVideo = synchronizeVideo;
        this.dispatch = dispatch ?? (action => action());
        this.clock = clock ?? TimeProvider.System;
        queue = new(onlineConcurrency: onlineConcurrency, timeProvider: this.clock);
        queue.StateChanged += OnQueueStateChanged;
        if (synchronizeVideo is not null)
        {
            videoQueue = new(localConcurrency: 1, onlineConcurrency: 1, timeProvider: this.clock);
            videoQueue.StateChanged += OnQueueStateChanged;
        }
    }

    public event EventHandler? StateChanged;
    public LibraryEnrichmentControllerState State => new(queue.State(LibraryEnrichmentKind.Local), queue.State(LibraryEnrichmentKind.Online))
        { Video = videoQueue?.State(LibraryEnrichmentKind.Online) ?? new(0, 0, 0, 0) };

    public void Start()
    {
        lock (gate)
        {
            if (started || disposed) return;
            started = true;
            model.LibrarySourcesChanged += OnLibrarySourcesChanged;
        }
        RequestSynchronization();
    }

    private void OnLibrarySourcesChanged(object? sender, EventArgs args) => RequestSynchronization();
    private void OnQueueStateChanged(object? sender, EventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RequestSynchronization()
    {
        lock (gate)
        {
            if (!started || disposed) return;
            synchronizationRequested = true;
            if (synchronizing) return;
            synchronizing = true;
            synchronization = Task.Run(SynchronizeAsync);
        }
    }

    private async Task SynchronizeAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(SynchronizationDelay, lifetime.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (disposed) return;
                    synchronizationRequested = false;
                }
                // Capture detached values under the model's lock and build the full lane
                // snapshots on this worker, not in a foreground dispatcher callback.
                if (lifetime.IsCancellationRequested) return;
                var candidates = model.CaptureEnrichmentCandidates();
                var local = new List<LibraryEnrichmentWork>();
                var online = new List<LibraryEnrichmentWork>();
                var videos = new List<LibraryEnrichmentWork>();
                foreach (var candidate in candidates)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    string id = candidate.Song.Id, sourceKey = candidate.SourceKey;
                    if (candidate.Details.LocalReadKey != sourceKey)
                    {
                        var song = candidate.Song;
                        local.Add(new(id, sourceKey, ticket => ReadLocalAsync(ticket, song, sourceKey)));
                        // A changed source must finish its own tag read before its online identity is selected.
                        continue;
                    }
                    string path = candidate.Song.Sources.Concat(candidate.Song.KnownSources ?? []).FirstOrDefault() ?? candidate.Song.Title;
                    var input = new SongLookupInput(Path.GetFileName(path), candidate.Details);
                    if (synchronizeVideo is not null && SongVideoLookupState.CanSearch(input, path))
                    {
                        string videoKey = sourceKey + "\n" + input.Key + "\n" + SongVideoLookupState.Identity(input, sourceKey);
                        videos.Add(new(id, videoKey, ticket => SynchronizeVideoAsync(ticket, sourceKey, videoKey)));
                    }
                    if (synchronizeAssets is null && !NeedsOnlineLookup(input, candidate.Song.Title)) continue;
                    string lookupKey = input.Key;
                    string contentKey = synchronizeAssets is null ? "" : "\n" + SongAssetState.Identity(input);
                    online.Add(new(id, sourceKey + "\n" + lookupKey + contentKey, ticket => LookupAsync(ticket, sourceKey, lookupKey)));
                }
                // Keep cooling-down work in the snapshot so synchronization cannot reset its retry time.
                queue.Synchronize(LibraryEnrichmentKind.Local, local);
                queue.Synchronize(LibraryEnrichmentKind.Online, online);
                videoQueue?.Synchronize(LibraryEnrichmentKind.Online, videos);
                lock (gate)
                {
                    if (!synchronizationRequested) return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                synchronizing = false;
                if (!disposed && synchronizationRequested)
                { synchronizing = true; synchronization = Task.Run(SynchronizeAsync); }
            }
        }
    }

    private bool CanPublish(LibraryEnrichmentTicket ticket, string sourceKey) => !lifetime.IsCancellationRequested
        && ticket.IsCurrent && model.GetEnrichmentSourceKey(ticket.SongId) == sourceKey;

    private async Task<LibraryEnrichmentOutcome> SynchronizeVideoAsync(LibraryEnrichmentTicket ticket, string sourceKey, string videoKey)
    {
        bool current = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)
            && model.Library.Details.GetValueOrDefault(ticket.SongId)?.LocalReadKey == sourceKey
            && model.GetLookupInput(ticket.SongId) is { } input
            && sourceKey + "\n" + input.Key + "\n" + SongVideoLookupState.Identity(input, sourceKey) == videoKey).ConfigureAwait(false);
        if (!current) return LibraryEnrichmentOutcome.Completed;
        return await synchronizeVideo!(ticket, sourceKey).ConfigureAwait(false);
    }

    private async Task<LibraryEnrichmentOutcome> ReadLocalAsync(LibraryEnrichmentTicket ticket, Song song, string sourceKey)
    {
        bool needed = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)
            && model.Library.Details.GetValueOrDefault(ticket.SongId)?.LocalReadKey != sourceKey).ConfigureAwait(false);
        if (!needed) return LibraryEnrichmentOutcome.Completed;
        var result = await readLocal(song, ticket.CancellationToken).ConfigureAwait(false);
        ticket.CancellationToken.ThrowIfCancellationRequested();
        if (result is null) return LibraryEnrichmentOutcome.Retry;
        bool saved = await RunOnDispatcherAsync(() => model.SaveBackgroundLocalMetadataAsync(ticket.SongId, sourceKey,
            result, () => CanPublish(ticket, sourceKey))).ConfigureAwait(false);
        RequestSynchronization();
        if (saved) return LibraryEnrichmentOutcome.Completed;
        bool current = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)
            && model.Library.Details.GetValueOrDefault(ticket.SongId)?.LocalReadKey != sourceKey).ConfigureAwait(false);
        return current ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;
    }

    private async Task<LibraryEnrichmentOutcome> LookupAsync(LibraryEnrichmentTicket ticket, string sourceKey, string lookupKey)
    {
        var input = await ReadOnDispatcherAsync(() =>
        {
            if (!CanPublish(ticket, sourceKey) || model.Library.Details.GetValueOrDefault(ticket.SongId)?.LocalReadKey != sourceKey) return null;
            var current = model.GetLookupInput(ticket.SongId);
            return current?.Key == lookupKey ? current : null;
        }).ConfigureAwait(false);
        if (input is null) return LibraryEnrichmentOutcome.Completed;
        if (NeedsOnlineLookup(input, "") && input.ShouldLookup(clock.GetUtcNow()))
        {
            SongLookupResult result;
            try { result = await lookup(input, ticket.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ticket.CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { result = new(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable); }
            ticket.CancellationToken.ThrowIfCancellationRequested();
            bool saved = await RunOnDispatcherAsync(() => model.SaveBackgroundAutomaticLookupAsync(ticket.SongId, input, result,
                () => CanPublish(ticket, sourceKey))).ConfigureAwait(false);
            RequestSynchronization();
            if (!saved)
            {
                bool current = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)).ConfigureAwait(false);
                return current ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;
            }
        }
        if (synchronizeAssets is not null && !await ReadOnDispatcherAsync(() =>
            model.GetLookupInput(ticket.SongId) is { } current && ticket.Key == sourceKey + "\n" + current.Key
                + "\n" + SongAssetState.Identity(current)).ConfigureAwait(false))
        {
            // Identity discovery changed the content key. Let its new ticket own the
            // asset phase instead of downloading once with each of the two tickets.
            RequestSynchronization();
            return LibraryEnrichmentOutcome.Completed;
        }
        // Artwork and author information have their own durable retry state. A lyric
        // provider cooling down must not stop an already-known album or author image.
        var assets = synchronizeAssets is not null && await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)).ConfigureAwait(false)
            ? await synchronizeAssets(ticket, sourceKey).ConfigureAwait(false) : LibraryEnrichmentOutcome.Completed;
        ticket.CancellationToken.ThrowIfCancellationRequested();
        bool remaining = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)
            && model.GetLookupInput(ticket.SongId) is { } current
            && NeedsOnlineLookup(current, model.FindSong(ticket.SongId)?.Song.Title ?? "")).ConfigureAwait(false);
        return remaining || assets == LibraryEnrichmentOutcome.Retry ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;
    }

    private bool NeedsOnlineLookup(SongLookupInput input, string fallbackTitle) => NeedsOnlineMetadata(input, fallbackTitle)
        || string.IsNullOrWhiteSpace(input.Details.Effective(fallbackTitle).Lyrics);

    private bool NeedsOnlineMetadata(SongLookupInput input, string fallbackTitle)
    {
        var details = input.Details;
        var effective = details.Effective(fallbackTitle);
        bool missing = effective.Title.Length == 0
            || effective.Artist.Length == 0 && details.Corrections?.Artist.Trim().Length != 0
            || effective.Album.Length == 0 && details.Corrections?.Album.Trim().Length != 0;
        // Complete local display tags do not contain online identities, portraits or
        // album artwork. Discover those once when the host enables asset collection.
        bool discoverAssets = synchronizeAssets is not null && effective.Artist.Length > 0;
        if (!missing && !discoverAssets) return false;
        // The existing automatic lookup only replaces absent metadata or its LRCLIB fallback.
        // A successful partial match is retained rather than repeatedly requesting missing album data.
        if (details.Online is { } online && !online.SourceUrl.Contains("lrclib.net", StringComparison.OrdinalIgnoreCase)) return false;
        return details.Lookup is not { } saved || saved.Key != input.Key || saved.Metadata != LookupOutcome.Matched;
    }

    private async Task<T> ReadOnDispatcherAsync<T>(Func<T> read)
    {
        T result = default!;
        await dispatch(() => { result = read(); return Task.CompletedTask; }).ConfigureAwait(false);
        return result;
    }
    private async Task<T> RunOnDispatcherAsync<T>(Func<Task<T>> action)
    {
        T result = default!;
        await dispatch(async () => { result = await action(); }).ConfigureAwait(false);
        return result;
    }

    /// <summary>Wait for current production and its coalesced index handoffs; delayed retries remain scheduled.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task pending;
            lock (gate) { if (disposed) return; pending = synchronization; }
            await pending.ConfigureAwait(false);
            await Task.WhenAll(queue.WhenIdleAsync(), videoQueue?.WhenIdleAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            await RunOnDispatcherAsync(model.FlushBackgroundMetadataAsync).ConfigureAwait(false);
            lock (gate)
                if (disposed || !synchronizing && !synchronizationRequested && ReferenceEquals(pending, synchronization)) return;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            model.LibrarySourcesChanged -= OnLibrarySourcesChanged;
        }
        lifetime.Cancel();
        queue.StateChanged -= OnQueueStateChanged;
        queue.Dispose();
        if (videoQueue is not null)
        {
            videoQueue.StateChanged -= OnQueueStateChanged;
            videoQueue.Dispose();
        }
    }
}
