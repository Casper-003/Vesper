using System.Collections.Concurrent;
using Vesper.App;
using Vesper.Core;

internal static class BackgroundVideoQueueChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        await CheckIsolationAsync(root, check);
        await CheckCancellationAsync(root, check);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CheckIsolationAsync(string root, Action<bool, string> check)
    {
        string initialFolder = Path.Combine(root, "video-isolation-initial");
        string newFolder = Path.Combine(root, "video-isolation-new");
        Directory.CreateDirectory(initialFolder); Directory.CreateDirectory(newFolder);
        await File.WriteAllTextAsync(Path.Combine(initialFolder, "Initial.mp3"), "Initial video queue fixture audio");
        for (int i = 0; i < 4; i++)
            await File.WriteAllTextAsync(Path.Combine(newFolder, $"New{i}.mp3"), "New fixture " + i + new string('x', i));
        using var ui = new SerialDispatcher();
        using var model = new MainViewModel(Path.Combine(root, "video-isolation.json"));
        await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(initialFolder); });
        var videoEntered = Signal(); var releaseVideo = Signal();
        var fourMetadataEntered = Signal(); var releaseMetadata = Signal();
        var newPublished = Signal(); var newAssetsReady = Signal();
        int videoStarted = 0, videoActive = 0, videoMaximum = 0;
        int newRequests = 0, metadataActive = 0, metadataMaximum = 0;
        var assetSongs = new ConcurrentDictionary<string, byte>();
        Task<LibraryMetadataRead?> Read(Song song, CancellationToken token) => Task.FromResult<LibraryMetadataRead?>(
            new(song.Sources[0], new(song.Title, "Fixture Singer", "Fixture Album", Lyrics: "Cached lyrics", DurationSeconds: 180)));
        async Task<SongLookupResult> Lookup(SongLookupInput input, CancellationToken token)
        {
            bool addedSong = input.FileName.StartsWith("New", StringComparison.Ordinal);
            if (addedSong)
            {
                int active = Interlocked.Increment(ref metadataActive); Max(ref metadataMaximum, active);
                if (Interlocked.Increment(ref newRequests) == 4) fourMetadataEntered.TrySetResult();
                try { await releaseMetadata.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref metadataActive); }
            }
            var metadata = input.Details.Effective("");
            return new(new(metadata.Title, metadata.Artist, metadata.Album), null, LookupOutcome.Matched, LookupOutcome.Matched);
        }
        async Task<LibraryEnrichmentOutcome> Assets(LibraryEnrichmentTicket ticket, string sourceKey)
        {
            await ui.Run(() =>
            {
                if (model.FindSong(ticket.SongId)?.Song.Title.StartsWith("New", StringComparison.Ordinal) == true)
                {
                    assetSongs.TryAdd(ticket.SongId, 0);
                    if (assetSongs.Count == 4) newAssetsReady.TrySetResult();
                }
                return Task.CompletedTask;
            });
            return LibraryEnrichmentOutcome.Completed;
        }
        async Task<LibraryEnrichmentOutcome> Video(LibraryEnrichmentTicket ticket, string sourceKey)
        {
            int active = Interlocked.Increment(ref videoActive); Max(ref videoMaximum, active);
            Interlocked.Increment(ref videoStarted);
            videoEntered.TrySetResult();
            try { await releaseVideo.Task.WaitAsync(ticket.CancellationToken); }
            finally { Interlocked.Decrement(ref videoActive); }
            return LibraryEnrichmentOutcome.Completed;
        }
        void SourcesChanged(object? sender, EventArgs args)
        {
            var added = model.Library.Songs.Where(song => song.Title.StartsWith("New", StringComparison.Ordinal)).ToArray();
            if (added.Length == 4 && added.All(song => model.Library.Details.GetValueOrDefault(song.Id)?.Online is not null))
                newPublished.TrySetResult();
        }
        model.LibrarySourcesChanged += SourcesChanged;
        using var controller = new LibraryEnrichmentController(model, Read, Lookup, ui.Run,
            synchronizeAssets: Assets, onlineConcurrency: 4, synchronizeVideo: Video);
        try
        {
            controller.Start();
            await videoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(controller.State.Video.ActiveCount == 1 && controller.State.IsBusy && model.CanManage,
                "独立MV队列运行时纳入后台忙碌状态，前台目录操作仍保持可用");
            await ui.Run(() => model.AddDirectoryAsync(newFolder));
            await fourMetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            check(controller.State.Video.ActiveCount == 1 && videoStarted == 1 && metadataActive == 4
                && controller.State.Online.ActiveCount == 4 && metadataMaximum == 4,
                "一个阻塞的MV请求不占资料队列位置，四个新歌曲资料请求可同时推进");
            releaseMetadata.TrySetResult();
            await Task.WhenAll(newPublished.Task, newAssetsReady.Task).WaitAsync(TimeSpan.FromSeconds(12));
            check(newRequests == 4 && assetSongs.Count == 4 && videoStarted == 1 && controller.State.Video.ActiveCount == 1,
                "MV仍未返回时新增歌曲资料与图片阶段均可发布，不被单路MV拖住");
            var idle = controller.WhenIdleAsync();
            check(!idle.IsCompleted, "等待后台就绪同时等待独立MV队列，不提前忽略仍运行的匹配");
            releaseVideo.TrySetResult();
            await idle.WaitAsync(TimeSpan.FromSeconds(15));
            check(videoStarted == 5 && videoMaximum == 1 && videoActive == 0
                && controller.State.Video.ActiveCount == 0 && !controller.State.IsBusy,
                "MV队列始终单路且完成全部可用音频，资料与MV都完成后才恢复就绪");
        }
        finally
        {
            releaseMetadata.TrySetResult(); releaseVideo.TrySetResult();
            model.LibrarySourcesChanged -= SourcesChanged;
        }
    }

    private static async Task CheckCancellationAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "video-isolation-cancel"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Cancel.mp3"), "Canceled MV fixture audio");
        using var ui = new SerialDispatcher();
        using var model = new MainViewModel(Path.Combine(root, "video-cancel.json"));
        await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
        var entered = Signal(); var canceled = Signal();
        using var controller = new LibraryEnrichmentController(model,
            (song, _) => Task.FromResult<LibraryMetadataRead?>(new(song.Sources[0],
                new(song.Title, "Singer", "Album", Lyrics: "Cached lyrics", DurationSeconds: 180))),
            (_, _) => throw new InvalidOperationException("Complete local tags do not need metadata in this fixture."), ui.Run,
            synchronizeVideo: async (ticket, _) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ticket.CancellationToken); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                return LibraryEnrichmentOutcome.Completed;
            });
        controller.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        controller.Dispose();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(controller.State.Video.PendingCount == 0 && controller.State.Video.WaitingRetryCount == 0,
            "关闭控制器同时取消独立MV票据、清理等待与重试队列并结束就绪等待");
    }

    private static void Max(ref int target, int value)
    {
        int seen;
        do { seen = Volatile.Read(ref target); }
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private sealed class SerialDispatcher : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task Run(Func<Task> action)
        {
            await gate.WaitAsync();
            try { await action(); }
            finally { gate.Release(); }
        }
        public void Dispose() => gate.Dispose();
    }
}
