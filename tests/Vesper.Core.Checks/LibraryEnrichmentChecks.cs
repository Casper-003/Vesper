using Vesper.Core;

internal static class LibraryEnrichmentChecks
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckConcurrencyAsync(check);
        await CheckReplacementAsync(check);
        await CheckRemovalAsync(check);
        await CheckRetryAsync(check);
        await CheckCancellationAsync(check);
        await CheckDisposeAsync(check);
    }

    private static async Task CheckConcurrencyAsync(Action<bool, string> check)
    {
        using var queue = new LibraryEnrichmentQueue();
        var release = Signal(); int localStarted = 0, onlineStarted = 0, activeLocal = 0, activeOnline = 0, maxLocal = 0, maxOnline = 0;
        var published = new System.Collections.Concurrent.ConcurrentBag<string>();
        LibraryEnrichmentWork Work(string id, bool online) => new(id, "source-v1", async ticket =>
        {
            if (online) { Interlocked.Increment(ref onlineStarted); int n = Interlocked.Increment(ref activeOnline); Max(ref maxOnline, n); }
            else { Interlocked.Increment(ref localStarted); int n = Interlocked.Increment(ref activeLocal); Max(ref maxLocal, n); }
            try { await release.Task; ticket.TryPublish(() => published.Add(id)); return LibraryEnrichmentOutcome.Completed; }
            finally { if (online) Interlocked.Decrement(ref activeOnline); else Interlocked.Decrement(ref activeLocal); }
        });
        var local = Enumerable.Range(0, 6).Select(i => Work("local-" + i, false)).ToArray();
        var online = Enumerable.Range(0, 3).Select(i => Work("online-" + i, true)).ToArray();
        queue.Synchronize(LibraryEnrichmentKind.Local, local);
        queue.Synchronize(LibraryEnrichmentKind.Online, online);
        await UntilAsync(() => Volatile.Read(ref localStarted) == 2 && Volatile.Read(ref onlineStarted) == 1);
        check(queue.State(LibraryEnrichmentKind.Local) is { ActiveCount: 2, PendingCount: 4 }
            && queue.State(LibraryEnrichmentKind.Online) is { ActiveCount: 1, PendingCount: 2 },
            "整库本地与联网队列独立保持2和1的有限并发，未入视口歌曲同样排队");
        queue.Synchronize(LibraryEnrichmentKind.Local, [.. local, local[0]]);
        queue.Synchronize(LibraryEnrichmentKind.Online, online);
        check(localStarted == 2 && onlineStarted == 1 && queue.PendingCount == 6,
            "相同songId和来源身份的运行及排队请求合并，不因重复快照增加工作");
        release.TrySetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        queue.Synchronize(LibraryEnrichmentKind.Local, local); queue.Synchronize(LibraryEnrichmentKind.Online, online);
        check(localStarted == 6 && onlineStarted == 3 && published.Distinct().Count() == 9 && maxLocal == 2 && maxOnline == 1,
            "整库所有可用曲目最终完成且各lane从未超限，已成功的相同身份不重复读取");
        var callbackRelease = Signal(); int runs = 0, changes = 0;
        using var callbacks = new LibraryEnrichmentQueue();
        LibraryEnrichmentWork[] same = [new("callback", "v1", async _ => { Interlocked.Increment(ref runs); await callbackRelease.Task; return LibraryEnrichmentOutcome.Completed; })];
        callbacks.StateChanged += (_, _) => { Interlocked.Increment(ref changes); callbacks.Synchronize(LibraryEnrichmentKind.Local, same); };
        callbacks.Synchronize(LibraryEnrichmentKind.Local, same);
        callbackRelease.TrySetResult(); await callbacks.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(runs == 1 && changes <= 3, "状态观察者可重入提交相同快照，不递归触发无变化事件或重复任务");
    }

    private static async Task CheckReplacementAsync(Action<bool, string> check)
    {
        using var queue = new LibraryEnrichmentQueue(localConcurrency: 1);
        var started = Signal(); var release = Signal(); LibraryEnrichmentTicket? oldTicket = null;
        var applied = new List<string>(); int obsoletePendingCalls = 0;
        var old = new LibraryEnrichmentWork("a", "source-old", async ticket =>
        {
            oldTicket = ticket; started.TrySetResult(); await release.Task;
            ticket.TryPublish(() => applied.Add("old")); return LibraryEnrichmentOutcome.Completed;
        });
        queue.Synchronize(LibraryEnrichmentKind.Local,
            [old, new("b", "source-old", _ => { Interlocked.Increment(ref obsoletePendingCalls); return Task.FromResult(LibraryEnrichmentOutcome.Completed); })]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        LibraryEnrichmentWork New(string id) => new(id, "source-new", ticket =>
        { ticket.TryPublish(() => applied.Add(id)); return Task.FromResult(LibraryEnrichmentOutcome.Completed); });
        queue.Synchronize(LibraryEnrichmentKind.Local, [New("a"), New("b")]);
        check(oldTicket!.CancellationToken.IsCancellationRequested && !oldTicket.IsCurrent
            && !oldTicket.TryPublish(() => applied.Add("stale-write"))
            && queue.State(LibraryEnrichmentKind.Local) is { ActiveCount: 1, PendingCount: 2 },
            "新来源身份立即撤销旧任务写入权限，忽略取消的旧工作仍占真实并发位置");
        release.TrySetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(obsoletePendingCalls == 0 && applied.SequenceEqual(new[] { "a", "b" }),
            "替换旧排队版本且拒绝旧运行结果，只有新来源身份结果可发布");
    }

    private static async Task CheckRemovalAsync(Action<bool, string> check)
    {
        using var queue = new LibraryEnrichmentQueue(localConcurrency: 1);
        var started = Signal(); var release = Signal(); int removedWrites = 0, pendingCalls = 0, recovered = 0;
        LibraryEnrichmentTicket? ticket = null;
        queue.Synchronize(LibraryEnrichmentKind.Local,
        [new("removed-running", "source-v1", async current => { ticket = current; started.TrySetResult(); await release.Task;
            current.TryPublish(() => Interlocked.Increment(ref removedWrites)); return LibraryEnrichmentOutcome.Completed; }),
         new("removed-pending", "source-v1", _ => { Interlocked.Increment(ref pendingCalls); return Task.FromResult(LibraryEnrichmentOutcome.Completed); })]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Synchronize(LibraryEnrichmentKind.Local, []); release.TrySetResult();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(ticket!.CancellationToken.IsCancellationRequested && removedWrites == 0 && pendingCalls == 0 && queue.PendingCount == 0,
            "目录移除撤销运行票据并跳过尚未开始的来源，取消不残留可写结果");
        queue.Synchronize(LibraryEnrichmentKind.Local,
            [new("removed-running", "source-v1", _ => { Interlocked.Increment(ref recovered); return Task.FromResult(LibraryEnrichmentOutcome.Completed); })]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(recovered == 1, "重新添加相同来源可正常入队，先前移除取消不记为永久失败");
        // Cancellation can beat Task.Run startup. This repeatedly exercises reserved-but-not-started slots.
        for (int i = 0; i < 100; i++)
        {
            queue.Synchronize(LibraryEnrichmentKind.Online, [new("rapid", "v" + i, _ => Task.FromResult(LibraryEnrichmentOutcome.Completed))]);
            queue.Synchronize(LibraryEnrichmentKind.Online, []);
        }
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(queue.ActiveCount == 0, "快速添加移除与任务启动竞争时仍释放并发计数，不因已释放取消源卡住队列");
    }

    private static async Task CheckRetryAsync(Action<bool, string> check)
    {
        var clock = new ManualClock(); using var queue = new LibraryEnrichmentQueue(retryDelay: TimeSpan.FromMinutes(1), timeProvider: clock);
        int attempts = 0, newSongCalls = 0;
        var failed = new LibraryEnrichmentWork("retry", "same-input", _ =>
        {
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1) throw new IOException("controlled provider failure");
            return Task.FromResult(attempt == 2 ? LibraryEnrichmentOutcome.Retry : LibraryEnrichmentOutcome.Completed);
        });
        queue.Synchronize(LibraryEnrichmentKind.Online, [failed]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 20; i++) queue.Synchronize(LibraryEnrichmentKind.Online, [failed]);
        check(attempts == 1 && !queue.IsBusy && queue.State(LibraryEnrichmentKind.Online).WaitingRetryCount == 1,
            "失败进入延迟重试且不阻塞WhenIdle，重复资料快照不会制造热循环");
        queue.Synchronize(LibraryEnrichmentKind.Online,
            [failed, new("new-song", "new-input", _ => { Interlocked.Increment(ref newSongCalls); return Task.FromResult(LibraryEnrichmentOutcome.Completed); })]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(59));
        check(attempts == 1 && newSongCalls == 1, "旧请求冷却时新曲目可继续，未到重试间隔不再次访问失败来源");
        clock.Advance(TimeSpan.FromSeconds(1)); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(attempts == 2 && queue.State(LibraryEnrichmentKind.Online).WaitingRetryCount == 1,
            "重试时机由时钟控制，显式Retry结果同样重新等待间隔");
        clock.Advance(TimeSpan.FromMinutes(1)); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(attempts == 3 && queue.State(LibraryEnrichmentKind.Online) is { WaitingRetryCount: 0, CompletedCount: 2 },
            "网络恢复后的延迟重试可完成，完成后停止自动重试");
    }

    private static async Task CheckCancellationAsync(Action<bool, string> check)
    {
        var clock = new ManualClock(); using var queue = new LibraryEnrichmentQueue(timeProvider: clock);
        int attempts = 0;
        var cancelled = new LibraryEnrichmentWork("cancelled", "input", _ =>
        { Interlocked.Increment(ref attempts); throw new OperationCanceledException("controlled cancellation"); });
        queue.Synchronize(LibraryEnrichmentKind.Local, [cancelled]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromMinutes(2));
        for (int i = 0; i < 20; i++) queue.Synchronize(LibraryEnrichmentKind.Local, [cancelled]);
        check(attempts == 1 && queue.State(LibraryEnrichmentKind.Local).WaitingRetryCount == 0,
            "操作取消不会被异常路径改记为失败重试，重复未变化快照也不会热循环");
        queue.Synchronize(LibraryEnrichmentKind.Local, []);
        queue.Synchronize(LibraryEnrichmentKind.Local,
            [new("cancelled", "input", _ => { Interlocked.Increment(ref attempts); return Task.FromResult(LibraryEnrichmentOutcome.Completed); })]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(attempts == 2 && queue.State(LibraryEnrichmentKind.Local).CompletedCount == 1,
            "移除后重新添加可恢复已取消的同身份工作，不留永久失败状态");
    }

    private static async Task CheckDisposeAsync(Action<bool, string> check)
    {
        var clock = new ManualClock(); var queue = new LibraryEnrichmentQueue(timeProvider: clock);
        var started = Signal(); var release = Signal(); LibraryEnrichmentTicket? current = null; int writes = 0, attempts = 0;
        queue.Synchronize(LibraryEnrichmentKind.Online, [new("dispose", "input", async ticket =>
        { current = ticket; started.TrySetResult(); await release.Task; ticket.TryPublish(() => Interlocked.Increment(ref writes)); return LibraryEnrichmentOutcome.Retry; })]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); queue.Dispose();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); release.TrySetResult();
        clock.Advance(TimeSpan.FromHours(1));
        check(current!.CancellationToken.IsCancellationRequested && !current.IsCurrent && writes == 0 && !queue.IsBusy,
            "关闭取消所有票据且WhenIdle可结束，忽略取消的晚到结果无法再发布");
        using var waiting = new LibraryEnrichmentQueue(timeProvider: clock);
        waiting.Synchronize(LibraryEnrichmentKind.Online, [new("waiting-close", "input", _ =>
        { Interlocked.Increment(ref attempts); return Task.FromResult(LibraryEnrichmentOutcome.Retry); })]);
        await waiting.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); waiting.Dispose(); clock.Advance(TimeSpan.FromHours(1));
        check(attempts == 1, "关闭同时撤销等待中的自动重试定时器，不在后台继续联网");
    }

    private static void Max(ref int location, int value)
    { int previous; do { previous = Volatile.Read(ref location); if (previous >= value) return; } while (Interlocked.CompareExchange(ref location, value, previous) != previous); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(1, timeout.Token);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (gate)
            {
                now += elapsed; due = timers.Where(timer => timer.At <= now).ToArray();
                foreach (var timer in due) timer.At = timer.Period > TimeSpan.Zero ? now + timer.Period : DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public DateTimeOffset At { get; set; } = DateTimeOffset.MaxValue;
            public TimeSpan Period { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { if (disposed) return false; At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; Period = period; return true; } }
            public void Fire() { if (!disposed) callback(state); }
            public void Dispose() { lock (owner.gate) { disposed = true; At = DateTimeOffset.MaxValue; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
