namespace Vesper.Core;

public enum LibraryEnrichmentKind { Local, Online }
public enum LibraryEnrichmentOutcome { Completed, Retry }
public sealed record LibraryEnrichmentWork(string SongId, string Key,
    Func<LibraryEnrichmentTicket, Task<LibraryEnrichmentOutcome>> Process);
public sealed record LibraryEnrichmentState(int ActiveCount, int PendingCount, int WaitingRetryCount, int CompletedCount)
{
    public bool IsBusy => ActiveCount > 0 || PendingCount > 0;
}

/// <summary>Check publication authority at the actual write boundary, including work that ignores cancellation.</summary>
public sealed class LibraryEnrichmentTicket
{
    private readonly Func<bool> isCurrent;
    private readonly Func<Action, bool> publish;
    internal LibraryEnrichmentTicket(string songId, string key, CancellationToken token, Func<bool> isCurrent, Func<Action, bool> publish)
    { SongId = songId; Key = key; CancellationToken = token; this.isCurrent = isCurrent; this.publish = publish; }
    public string SongId { get; }
    public string Key { get; }
    public CancellationToken CancellationToken { get; }
    public bool IsCurrent => isCurrent();
    public bool TryPublish(Action action) => publish(action);
}

/// <summary>
/// Independent bounded local/online lanes. Synchronize supplies a lane's entire current eligible library,
/// not just visible rows. Delegates and StateChanged run on arbitrary threads; UI hosts dispatch their work.
/// Successful keys remain deduplicated. Failures retry after a delay; waiting retries do not count as busy.
/// </summary>
public sealed class LibraryEnrichmentQueue : IDisposable
{
    private enum WorkState { Pending, Running, Waiting, Completed, Cancelled }
    private sealed class Entry
    {
        public Entry(LibraryEnrichmentWork work) { Work = work; Token = Cancellation.Token; }
        public LibraryEnrichmentWork Work { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }
        public WorkState State { get; set; }
        public DateTimeOffset RetryAt { get; set; }
        public bool Retired { get; set; }
    }
    private sealed class Lane(int concurrency)
    {
        public int Concurrency { get; } = concurrency;
        public int Active { get; set; }
        public int PendingCount { get; set; }
        public int WaitingCount { get; set; }
        public int CompletedCount { get; set; }
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        public Queue<Entry> Pending { get; } = new();
    }

    private readonly object gate = new();
    private readonly Lane local, online;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan retryDelay;
    private readonly ITimer retryTimer;
    private readonly PriorityQueue<(Lane Lane, Entry Entry, DateTimeOffset At), DateTimeOffset> retries = new();
    private DateTimeOffset? scheduledRetry;
    private TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;

    public LibraryEnrichmentQueue(int localConcurrency = 2, int onlineConcurrency = 1,
        TimeSpan? retryDelay = null, TimeProvider? timeProvider = null)
    {
        if (localConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(localConcurrency));
        if (onlineConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(onlineConcurrency));
        this.retryDelay = retryDelay ?? TimeSpan.FromMinutes(1);
        if (this.retryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        local = new(localConcurrency); online = new(onlineConcurrency);
        idle.TrySetResult();
        retryTimer = this.timeProvider.CreateTimer(_ => RetryDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? StateChanged;
    public LibraryEnrichmentState State(LibraryEnrichmentKind kind)
    { lock (gate) return ReadState(GetLane(kind)); }
    public int ActiveCount { get { lock (gate) return local.Active + online.Active; } }
    public int PendingCount { get { lock (gate) return ReadState(local).PendingCount + ReadState(online).PendingCount; } }
    public bool IsBusy { get { lock (gate) return !disposed && (ReadState(local).IsBusy || ReadState(online).IsBusy); } }

    /// <summary>Wait for running/ready work only. Delayed retries and work submitted later are excluded.</summary>
    public Task WhenIdleAsync()
    { lock (gate) return disposed ? Task.CompletedTask : idle.Task; }

    public void Synchronize(LibraryEnrichmentKind kind, IEnumerable<LibraryEnrichmentWork> work)
    {
        var desired = new Dictionary<string, LibraryEnrichmentWork>(StringComparer.Ordinal);
        foreach (var item in work)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SongId);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentNullException.ThrowIfNull(item.Process);
            if (!desired.TryAdd(item.SongId, item) && desired[item.SongId].Key != item.Key)
                throw new ArgumentException("同一曲目在一次队列快照中具有多个当前版本。", nameof(work));
        }
        var retired = new List<Entry>();
        bool changed = false;
        List<(Lane Lane, Entry Entry)> start;
        lock (gate)
        {
            if (disposed) return;
            Lane lane = GetLane(kind);
            foreach (var pair in lane.Entries.ToArray())
            {
                if (!desired.TryGetValue(pair.Key, out var replacement) || pair.Value.Work.Key != replacement.Key)
                {
                    SetState(lane, pair.Value, WorkState.Cancelled);
                    lane.Entries.Remove(pair.Key); pair.Value.Retired = true; retired.Add(pair.Value); changed = true;
                }
            }
            foreach (var item in desired.Values)
            {
                if (lane.Entries.ContainsKey(item.SongId)) continue;
                var entry = new Entry(item); lane.Entries.Add(item.SongId, entry); lane.Pending.Enqueue(entry);
                lane.PendingCount++; changed = true;
            }
            if (retired.Count > 0)
            {
                // Release obsolete queued closures even when an old running delegate ignores cancellation.
                var pending = lane.Pending.Where(entry => IsCurrentLocked(lane, entry) && entry.State == WorkState.Pending).ToArray();
                lane.Pending.Clear(); foreach (var entry in pending) lane.Pending.Enqueue(entry);
            }
            start = PumpLocked();
            UpdateTimerLocked(); UpdateIdleLocked();
        }
        foreach (var entry in retired) _ = CancelRetiredAsync(entry);
        Start(start); if (changed || start.Count > 0) RaiseChanged();
    }

    private List<(Lane Lane, Entry Entry)> PumpLocked()
    {
        var start = new List<(Lane, Entry)>();
        foreach (var lane in new[] { local, online })
        {
            while (!disposed && lane.Active < lane.Concurrency && lane.Pending.TryDequeue(out var entry))
            {
                if (!IsCurrentLocked(lane, entry) || entry.State != WorkState.Pending) continue;
                SetState(lane, entry, WorkState.Running); lane.Active++; start.Add((lane, entry));
            }
        }
        return start;
    }

    private void Start(List<(Lane Lane, Entry Entry)> work)
    { foreach (var item in work) _ = Task.Run(() => RunAsync(item.Lane, item.Entry)); }

    private async Task RunAsync(Lane lane, Entry entry)
    {
        var token = entry.Token;
        var ticket = new LibraryEnrichmentTicket(entry.Work.SongId, entry.Work.Key, token,
            () => { lock (gate) return CanPublishLocked(lane, entry); },
            action => { lock (gate) { if (!CanPublishLocked(lane, entry)) return false; action(); return true; } });
        LibraryEnrichmentOutcome outcome = LibraryEnrichmentOutcome.Completed;
        bool cancelled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            outcome = await entry.Work.Process(ticket).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception) { outcome = LibraryEnrichmentOutcome.Retry; }
        List<(Lane Lane, Entry Entry)> start;
        lock (gate)
        {
            lane.Active--;
            if (IsCurrentLocked(lane, entry))
            {
                if (cancelled || token.IsCancellationRequested) SetState(lane, entry, WorkState.Cancelled);
                else if (outcome == LibraryEnrichmentOutcome.Retry)
                {
                    SetState(lane, entry, WorkState.Waiting); entry.RetryAt = timeProvider.GetUtcNow() + retryDelay;
                    retries.Enqueue((lane, entry, entry.RetryAt), entry.RetryAt);
                }
                else SetState(lane, entry, WorkState.Completed);
            }
            start = PumpLocked();
            if (!disposed) UpdateTimerLocked();
            UpdateIdleLocked();
        }
        Start(start); RaiseChanged();
    }

    private bool CanPublishLocked(Lane lane, Entry entry) => IsCurrentLocked(lane, entry)
        && entry.State == WorkState.Running && !entry.Token.IsCancellationRequested;
    private bool IsCurrentLocked(Lane lane, Entry entry) => !disposed && !entry.Retired
        && lane.Entries.TryGetValue(entry.Work.SongId, out var current) && ReferenceEquals(current, entry);

    private void RetryDue()
    {
        List<(Lane Lane, Entry Entry)> start;
        lock (gate)
        {
            if (disposed) return;
            scheduledRetry = null;
            DateTimeOffset now = timeProvider.GetUtcNow();
            PruneRetriesLocked();
            while (retries.TryPeek(out var waiting, out var at) && at <= now)
            {
                retries.Dequeue();
                if (IsCurrentLocked(waiting.Lane, waiting.Entry) && waiting.Entry.State == WorkState.Waiting && waiting.Entry.RetryAt == waiting.At)
                { SetState(waiting.Lane, waiting.Entry, WorkState.Pending); waiting.Lane.Pending.Enqueue(waiting.Entry); }
                PruneRetriesLocked();
            }
            start = PumpLocked(); UpdateTimerLocked(); UpdateIdleLocked();
        }
        Start(start); RaiseChanged();
    }

    private void UpdateTimerLocked()
    {
        PruneRetriesLocked();
        DateTimeOffset? next = retries.TryPeek(out _, out var at) ? at : null;
        if (next == scheduledRetry) return;
        scheduledRetry = next;
        var delay = next is { } scheduledAt ? scheduledAt - timeProvider.GetUtcNow() : Timeout.InfiniteTimeSpan;
        if (next is not null && delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        retryTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void UpdateIdleLocked()
    {
        if (disposed || !(ReadState(local).IsBusy || ReadState(online).IsBusy)) idle.TrySetResult();
        else if (idle.Task.IsCompleted) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void PruneRetriesLocked()
    {
        while (retries.TryPeek(out var waiting, out _) && (!IsCurrentLocked(waiting.Lane, waiting.Entry)
            || waiting.Entry.State != WorkState.Waiting || waiting.Entry.RetryAt != waiting.At)) retries.Dequeue();
    }
    private static void SetState(Lane lane, Entry entry, WorkState state)
    {
        switch (entry.State) { case WorkState.Pending: lane.PendingCount--; break; case WorkState.Waiting: lane.WaitingCount--; break; case WorkState.Completed: lane.CompletedCount--; break; }
        entry.State = state;
        switch (state) { case WorkState.Pending: lane.PendingCount++; break; case WorkState.Waiting: lane.WaitingCount++; break; case WorkState.Completed: lane.CompletedCount++; break; }
    }
    private static LibraryEnrichmentState ReadState(Lane lane) => new(lane.Active, lane.PendingCount, lane.WaitingCount, lane.CompletedCount);
    private Lane GetLane(LibraryEnrichmentKind kind) => kind switch
    { LibraryEnrichmentKind.Local => local, LibraryEnrichmentKind.Online => online, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };

    private static async Task CancelRetiredAsync(Entry entry)
    {
        try { await entry.Cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A client's cancellation callback must not break queue retirement. */ }
        finally { entry.Cancellation.Dispose(); }
    }
    private void RaiseChanged()
    {
        if (StateChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception) { /* Observers cannot strand a reserved worker slot. */ }
    }

    public void Dispose()
    {
        Entry[] retired;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            retired = local.Entries.Values.Concat(online.Entries.Values).ToArray();
            foreach (var entry in retired) entry.Retired = true;
            local.Entries.Clear(); online.Entries.Clear(); local.Pending.Clear(); online.Pending.Clear();
            local.PendingCount = online.PendingCount = local.WaitingCount = online.WaitingCount = local.CompletedCount = online.CompletedCount = 0;
            retries.Clear();
            UpdateIdleLocked();
        }
        retryTimer.Dispose();
        foreach (var entry in retired) _ = CancelRetiredAsync(entry);
        RaiseChanged();
    }
}
