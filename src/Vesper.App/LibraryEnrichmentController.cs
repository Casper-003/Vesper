using Vesper.Core;

namespace Vesper.App;

public sealed record LibraryMetadataRead(string Path, SongMetadata Metadata, string Resolution = "");

public sealed record LibraryEnrichmentControllerState(LibraryEnrichmentState Local, LibraryEnrichmentState Online)
{
    public bool IsBusy => Local.IsBusy || Online.IsBusy;
    public string Label => Local.IsBusy ? "读取本地歌曲资料" : Online.IsBusy ? "补全歌曲资料" : "就绪";
    public string Tooltip => $"本地资料：{Local.ActiveCount} 首读取中，{Local.PendingCount} 首等待；"
        + $"联网资料：{Online.ActiveCount} 首处理中，{Online.PendingCount} 首等待。"
        + (Local.WaitingRetryCount + Online.WaitingRetryCount > 0 ? $"{Local.WaitingRetryCount + Online.WaitingRetryCount} 首稍后重试。" : "")
        + "可继续浏览、点歌和管理目录。";
}

/// <summary>
/// Library-wide data production, independent of views. The host supplies media/network readers
/// and a dispatcher; only detached inputs leave that dispatcher, and every write rechecks authority.
/// </summary>
public sealed class LibraryEnrichmentController : IDisposable
{
    private static readonly TimeSpan SynchronizationDelay = TimeSpan.FromMilliseconds(100);
    private readonly MainViewModel model;
    private readonly Func<Song, CancellationToken, Task<LibraryMetadataRead?>> readLocal;
    private readonly Func<SongLookupInput, CancellationToken, Task<SongLookupResult>> lookup;
    private readonly Func<Func<Task>, Task> dispatch;
    private readonly TimeProvider clock;
    private readonly LibraryEnrichmentQueue queue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Task synchronization = Task.CompletedTask;
    private bool started, disposed, synchronizationRequested, synchronizing;

    public LibraryEnrichmentController(MainViewModel model,
        Func<Song, CancellationToken, Task<LibraryMetadataRead?>> readLocal,
        Func<SongLookupInput, CancellationToken, Task<SongLookupResult>> lookup,
        Func<Func<Task>, Task>? dispatch = null, TimeProvider? clock = null)
    {
        this.model = model;
        this.readLocal = readLocal;
        this.lookup = lookup;
        this.dispatch = dispatch ?? (action => action());
        this.clock = clock ?? TimeProvider.System;
        queue = new(timeProvider: this.clock);
        queue.StateChanged += OnQueueStateChanged;
    }

    public event EventHandler? StateChanged;
    public LibraryEnrichmentControllerState State => new(queue.State(LibraryEnrichmentKind.Local), queue.State(LibraryEnrichmentKind.Online));

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
                await dispatch(() =>
                {
                    if (lifetime.IsCancellationRequested) return Task.CompletedTask;
                    var candidates = model.CaptureEnrichmentCandidates();
                    var local = new List<LibraryEnrichmentWork>();
                    var online = new List<LibraryEnrichmentWork>();
                    foreach (var candidate in candidates)
                    {
                        string id = candidate.Song.Id, sourceKey = candidate.SourceKey;
                        if (candidate.Details.LocalReadKey != sourceKey)
                        {
                            var song = candidate.Song;
                            local.Add(new(id, sourceKey, ticket => ReadLocalAsync(ticket, song, sourceKey)));
                            // A changed source must finish its own tag read before its online identity is selected.
                            continue;
                        }
                        var input = model.GetLookupInput(id);
                        if (input is null || !NeedsOnlineMetadata(input, candidate.Song.Title)) continue;
                        string lookupKey = input.Key;
                        online.Add(new(id, sourceKey + "\n" + lookupKey, ticket => LookupAsync(ticket, sourceKey, lookupKey)));
                    }
                    // These are complete lane snapshots, including cooling-down failures. Omitting a
                    // not-yet-due online task here would retire its ticket and bypass the retry cooldown.
                    queue.Synchronize(LibraryEnrichmentKind.Local, local);
                    queue.Synchronize(LibraryEnrichmentKind.Online, online);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
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
            return current?.Key == lookupKey && NeedsOnlineMetadata(current, model.FindSong(ticket.SongId)?.Song.Title ?? "") ? current : null;
        }).ConfigureAwait(false);
        if (input is null) return LibraryEnrichmentOutcome.Completed;
        if (!input.ShouldLookup(clock.GetUtcNow())) return LibraryEnrichmentOutcome.Retry;
        SongLookupResult result;
        try { result = await lookup(input, ticket.CancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ticket.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { result = new(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable); }
        ticket.CancellationToken.ThrowIfCancellationRequested();
        bool saved = await RunOnDispatcherAsync(() => model.SaveAutomaticLookupAsync(ticket.SongId, input, result,
            () => CanPublish(ticket, sourceKey))).ConfigureAwait(false);
        RequestSynchronization();
        if (!saved)
        {
            bool current = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)).ConfigureAwait(false);
            return current ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;
        }
        bool remaining = await ReadOnDispatcherAsync(() => CanPublish(ticket, sourceKey)
            && model.GetLookupInput(ticket.SongId) is { } current
            && NeedsOnlineMetadata(current, model.FindSong(ticket.SongId)?.Song.Title ?? "")).ConfigureAwait(false);
        return remaining ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed;
    }

    private static bool NeedsOnlineMetadata(SongLookupInput input, string fallbackTitle)
    {
        var details = input.Details;
        var effective = details.Effective(fallbackTitle);
        bool missing = effective.Title.Length == 0
            || effective.Artist.Length == 0 && details.Corrections?.Artist.Trim().Length != 0
            || effective.Album.Length == 0 && details.Corrections?.Album.Trim().Length != 0;
        if (!missing) return false;
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
            await queue.WhenIdleAsync().ConfigureAwait(false);
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
    }
}
